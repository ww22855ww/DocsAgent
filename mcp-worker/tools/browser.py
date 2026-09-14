"""Browser automation for the mock portal.

The agent gets one coarse tool. Everything between "log in" and "the files are
in staging" is deterministic code here, so the agent never sees a selector and
cannot get stuck mid-flow. See roadmap.md section 8.
"""

from __future__ import annotations

import logging
import os

from playwright.sync_api import TimeoutError as PWTimeout
from playwright.sync_api import sync_playwright

import config
from .common import human_size, list_staging, move_to

log = logging.getLogger("mcp-worker.browser")

NAV_TIMEOUT_MS = 20_000
DOWNLOAD_TIMEOUT_MS = 30_000


def _video_options() -> dict:
    """Record the session when PORTAL_VIDEO_DIR is set, otherwise record nothing.

    The browser is headless, so "does it really drive the site?" has no visible
    answer; Playwright's own recording is the one that cannot be staged, because
    it comes out of the same run the trace describes. Off by default: recording
    costs a video file per call and the demo never needs it. Read at call time
    so a one-off `docker exec -e PORTAL_VIDEO_DIR=...` can turn it on without
    touching the running server's environment.
    """
    out = os.environ.get("PORTAL_VIDEO_DIR", "").strip()
    if not out:
        return {}
    os.makedirs(out, exist_ok=True)
    return {"record_video_dir": out, "record_video_size": {"width": 1280, "height": 720}}


def _slow_mo_ms() -> float:
    """Pace the browser for a recording. Zero — full speed — everywhere else.

    The whole login-query-download sequence finishes in about a second, so a
    real-time video is a blur of blank frames. Pacing it is the only way to see
    it happen, and it is the same code either way: nothing is re-enacted for
    the camera. Say so wherever the recording is shown.
    """
    try:
        return max(0.0, float(os.environ.get("PORTAL_SLOW_MO_MS", "0")))
    except ValueError:
        return 0.0


class Journal:
    """Records what the browser actually did, step by step.

    The audience for this is the room, not the agent: people who have not met
    Playwright ask how it "reads" a page, and the honest answer is that it does
    not read anything. It addresses elements the page already names. Showing the
    selector next to each action is what makes that land.
    """

    def __init__(self):
        self.entries: list[dict] = []

    def add(self, action: str, target: str, detail: str, found: str | None = None) -> None:
        self.entries.append({
            "action": action,
            "target": target,
            "detail": detail,
            "found": found,
        })


def _login(page, j: Journal) -> None:
    url = f"{config.PORTAL_URL}/Account/Login"
    page.goto(url, timeout=NAV_TIMEOUT_MS)
    j.add("開啟網址", url, "載入入口網站的登入頁")

    page.fill("#username", config.PORTAL_USERNAME)
    j.add("填入欄位", "#username",
          f"找到 id 為 username 的輸入框，輸入帳號 {config.PORTAL_USERNAME}")

    page.fill("#password", config.PORTAL_PASSWORD)
    j.add("填入欄位", "#password",
          "找到 id 為 password 的輸入框，輸入密碼（不記錄內容）")

    page.click("#btn-login")
    j.add("點擊按鈕", "#btn-login", "送出登入表單")

    page.wait_for_selector("#sidebar", timeout=NAV_TIMEOUT_MS)
    j.add("等待元素", "#sidebar",
          "側邊選單出現，代表登入成功並進入系統")


def _search_documents(page, j: Journal, date: str, department: str, document_type: str) -> list[str]:
    url = f"{config.PORTAL_URL}/Documents/Query"
    page.goto(url, timeout=NAV_TIMEOUT_MS)
    page.wait_for_selector("#query-form", timeout=NAV_TIMEOUT_MS)
    j.add("開啟網址", url, "進入 SCM 文件查詢畫面")

    page.fill("#queryDate", date)
    j.add("填入欄位", "#queryDate", f"日期欄位填入 {date}")

    page.select_option("#department", department)
    j.add("選擇下拉選項", "#department",
          f"部門下拉選單選擇 {department}")

    page.select_option("#documentType", document_type)
    j.add("選擇下拉選項", "#documentType",
          f"文件類型下拉選單選擇 {document_type}")

    page.click("#btn-search")
    j.add("點擊按鈕", "#btn-search", "送出查詢")

    return _read_results(page, j, "#result-summary", "#result-table tr.result-row")


def _search_surveys(page, j: Journal, year: str, status: str) -> list[str]:
    """The ESG questionnaires live on their own screen, with their own filters."""
    url = f"{config.PORTAL_URL}/Esg/Surveys"
    page.goto(url, timeout=NAV_TIMEOUT_MS)
    page.wait_for_selector("#esg-form", timeout=NAV_TIMEOUT_MS)
    j.add("開啟網址", url,
          "進入 ESG 問卷畫面，這是與 SCM 文件不同的另一個分頁")

    page.select_option("#surveyYear", year)
    j.add("選擇下拉選項", "#surveyYear", f"年度下拉選單選擇 {year}")

    page.select_option("#surveyStatus", status)
    j.add("選擇下拉選項", "#surveyStatus", f"狀態下拉選單選擇 {status}")

    page.click("#btn-esg-search")
    j.add("點擊按鈕", "#btn-esg-search", "送出查詢")

    return _read_results(page, j, "#esg-summary", "#esg-table tr.esg-row")


def _read_results(page, j: Journal, summary_selector: str, row_selector: str) -> list[str]:
    """Read the row count and the file name each row carries.

    The page states its own count in a data-count attribute and tags every row
    with data-file, so nothing here guesses at layout or parses visible text.
    That is the whole trick: the page was built to be addressed.
    """
    page.wait_for_selector(summary_selector, timeout=NAV_TIMEOUT_MS)
    count = int(page.get_attribute(summary_selector, "data-count") or "0")
    j.add("讀取屬性", summary_selector + "[data-count]",
          "結果區塊自己標示了筆數，直接讀這個屬性，不用去數畫面上的列",
          found=f"{count} 筆")

    if count == 0:
        return []

    rows = page.query_selector_all(row_selector)
    files = [el.get_attribute("data-file") for el in rows]
    j.add("讀取屬性", row_selector + "[data-file]",
          "每一列都帶著自己的檔名屬性，逐列讀出來",
          found="、".join(f for f in files if f))
    return files


def _download_one(page, j: Journal, filename: str) -> str:
    """Click one row's download link and save the file into the downloads dir."""
    link = f'a.btn-download[data-file="{filename}"]'
    with page.expect_download(timeout=DOWNLOAD_TIMEOUT_MS) as info:
        page.click(link)
    download = info.value
    target = os.path.join(config.DOWNLOADS_DIR, filename)
    download.save_as(target)
    j.add("點擊下載", link,
          "點該列的下載連結，攔截瀏覽器的下載事件並存檔",
          found=filename)
    return target


def _fetch(label: str, search, query: dict) -> dict:
    """Log in, run one section's search, stage whatever it returned.

    Idempotent: files already in staging are reported as skipped rather than
    downloaded again, so a repeated call is harmless.
    """
    os.makedirs(config.DOWNLOADS_DIR, exist_ok=True)
    os.makedirs(config.STAGING_DIR, exist_ok=True)

    already = set(list_staging())
    downloaded: list[str] = []
    skipped: list[str] = []
    files_meta: list[dict] = []

    j = Journal()

    with sync_playwright() as p:
        browser = p.chromium.launch(args=["--no-sandbox", "--disable-dev-shm-usage"],
                                    slow_mo=_slow_mo_ms())
        j.add("啟動瀏覽器", "chromium (headless)",
              "在容器內開一個沒有畫面的瀏覽器")
        try:
            ctx = browser.new_context(accept_downloads=True, **_video_options())
            page = ctx.new_page()
            page.set_default_timeout(NAV_TIMEOUT_MS)

            _login(page, j)
            log.info("portal login ok as %s", config.PORTAL_USERNAME)

            found = search(page, j)
            log.info("%s query %s -> %d rows", label, query, len(found))

            for name in found:
                if not name:
                    continue
                if name in already:
                    skipped.append(name)
                    staged = os.path.join(config.STAGING_DIR, name)
                    j.add("略過", name, "暫存區已經有這個檔案，不重複下載")
                else:
                    tmp = _download_one(page, j, name)
                    staged = move_to(tmp, config.STAGING_DIR)
                    downloaded.append(name)

                files_meta.append({
                    "filename": name,
                    "size": human_size(os.path.getsize(staged)),
                    "staged_path": staged,
                })

            j.add("\u95dc\u9589\u700f\u89bd\u5668", "chromium",
                  f"\u5171\u53d6\u5f97 {len(files_meta)} \u4efd\u6a94\u6848\uff0c\u642c\u5165\u66ab\u5b58\u5340")
            ctx.close()
        except PWTimeout as exc:
            raise RuntimeError(f"Portal automation timed out: {exc}") from exc
        finally:
            browser.close()

    return {
        "status": "success",
        "source": label,
        "query": query,
        "downloaded_count": len(downloaded),
        "skipped_count": len(skipped),
        "files": [m["filename"] for m in files_meta],
        "details": files_meta,
        "staging_dir": config.STAGING_DIR,
        # What the browser did, for the trace. Trimmed before it reaches the model.
        "browser_steps": j.entries,
    }


def download_documents(date: str, department: str = "SCM", document_type: str = "ALL") -> dict:
    """Fetch documents from the portal's SCM Document Query screen."""
    return _fetch(
        "scm_documents",
        lambda page, j: _search_documents(page, j, date, department, document_type),
        {"date": date, "department": department, "document_type": document_type},
    )


def download_esg_surveys(year: str = "2026", status: str = "ALL") -> dict:
    """Fetch questionnaires from the portal's ESG Questionnaires screen."""
    return _fetch(
        "esg_surveys",
        lambda page, j: _search_surveys(page, j, year, status),
        {"year": year, "status": status},
    )
