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


def _login(page) -> None:
    page.goto(f"{config.PORTAL_URL}/Account/Login", timeout=NAV_TIMEOUT_MS)
    page.fill("#username", config.PORTAL_USERNAME)
    page.fill("#password", config.PORTAL_PASSWORD)
    page.click("#btn-login")
    page.wait_for_selector("#query-form", timeout=NAV_TIMEOUT_MS)


def _search(page, date: str, department: str, document_type: str) -> list[str]:
    page.fill("#queryDate", date)
    page.select_option("#department", department)
    page.select_option("#documentType", document_type)
    page.click("#btn-search")
    page.wait_for_selector("#result-summary", timeout=NAV_TIMEOUT_MS)

    count = int(page.get_attribute("#result-summary", "data-count") or "0")
    if count == 0:
        return []

    page.wait_for_selector("#result-table", timeout=NAV_TIMEOUT_MS)
    return [
        el.get_attribute("data-file")
        for el in page.query_selector_all("#result-table tr.result-row")
    ]


def _download_one(page, filename: str) -> str:
    """Click one row's download link and save the file into the downloads dir."""
    link = f'a.btn-download[data-file="{filename}"]'
    with page.expect_download(timeout=DOWNLOAD_TIMEOUT_MS) as info:
        page.click(link)
    download = info.value
    target = os.path.join(config.DOWNLOADS_DIR, filename)
    download.save_as(target)
    return target


def download_documents(date: str, department: str = "SCM", document_type: str = "ALL") -> dict:
    """Log into the portal, run the document query, download the results, stage them.

    Idempotent: files already present in staging are reported as skipped rather
    than downloaded again, so a repeated call is harmless.
    """
    os.makedirs(config.DOWNLOADS_DIR, exist_ok=True)
    os.makedirs(config.STAGING_DIR, exist_ok=True)

    already = set(list_staging())
    downloaded: list[str] = []
    skipped: list[str] = []
    files_meta: list[dict] = []

    with sync_playwright() as p:
        browser = p.chromium.launch(args=["--no-sandbox", "--disable-dev-shm-usage"])
        try:
            ctx = browser.new_context(accept_downloads=True)
            page = ctx.new_page()
            page.set_default_timeout(NAV_TIMEOUT_MS)

            _login(page)
            log.info("portal login ok as %s", config.PORTAL_USERNAME)

            found = _search(page, date, department, document_type)
            log.info("query dept=%s type=%s -> %d rows", department, document_type, len(found))

            for name in found:
                if not name:
                    continue
                if name in already:
                    skipped.append(name)
                    staged = os.path.join(config.STAGING_DIR, name)
                else:
                    tmp = _download_one(page, name)
                    staged = move_to(tmp, config.STAGING_DIR)
                    downloaded.append(name)

                files_meta.append({
                    "filename": name,
                    "size": human_size(os.path.getsize(staged)),
                    "staged_path": staged,
                })

            ctx.close()
        except PWTimeout as exc:
            raise RuntimeError(f"Portal automation timed out: {exc}") from exc
        finally:
            browser.close()

    return {
        "status": "success",
        "query": {"date": date, "department": department, "document_type": document_type},
        "downloaded_count": len(downloaded),
        "skipped_count": len(skipped),
        "files": [m["filename"] for m in files_meta],
        "details": files_meta,
        "staging_dir": config.STAGING_DIR,
    }
