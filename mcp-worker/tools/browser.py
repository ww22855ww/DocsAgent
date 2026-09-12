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
    page.wait_for_selector("#sidebar", timeout=NAV_TIMEOUT_MS)


def _search_documents(page, date: str, department: str, document_type: str) -> list[str]:
    page.goto(f"{config.PORTAL_URL}/Documents/Query", timeout=NAV_TIMEOUT_MS)
    page.wait_for_selector("#query-form", timeout=NAV_TIMEOUT_MS)
    page.fill("#queryDate", date)
    page.select_option("#department", department)
    page.select_option("#documentType", document_type)
    page.click("#btn-search")
    return _read_results(page, "#result-summary", "#result-table tr.result-row")


def _search_surveys(page, year: str, status: str) -> list[str]:
    """The ESG questionnaires live on their own screen, with their own filters."""
    page.goto(f"{config.PORTAL_URL}/Esg/Surveys", timeout=NAV_TIMEOUT_MS)
    page.wait_for_selector("#esg-form", timeout=NAV_TIMEOUT_MS)
    page.select_option("#surveyYear", year)
    page.select_option("#surveyStatus", status)
    page.click("#btn-esg-search")
    return _read_results(page, "#esg-summary", "#esg-table tr.esg-row")


def _read_results(page, summary_selector: str, row_selector: str) -> list[str]:
    page.wait_for_selector(summary_selector, timeout=NAV_TIMEOUT_MS)
    count = int(page.get_attribute(summary_selector, "data-count") or "0")
    if count == 0:
        return []
    return [el.get_attribute("data-file") for el in page.query_selector_all(row_selector)]


def _download_one(page, filename: str) -> str:
    """Click one row's download link and save the file into the downloads dir."""
    link = f'a.btn-download[data-file="{filename}"]'
    with page.expect_download(timeout=DOWNLOAD_TIMEOUT_MS) as info:
        page.click(link)
    download = info.value
    target = os.path.join(config.DOWNLOADS_DIR, filename)
    download.save_as(target)
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

    with sync_playwright() as p:
        browser = p.chromium.launch(args=["--no-sandbox", "--disable-dev-shm-usage"])
        try:
            ctx = browser.new_context(accept_downloads=True)
            page = ctx.new_page()
            page.set_default_timeout(NAV_TIMEOUT_MS)

            _login(page)
            log.info("portal login ok as %s", config.PORTAL_USERNAME)

            found = search(page)
            log.info("%s query %s -> %d rows", label, query, len(found))

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
        "source": label,
        "query": query,
        "downloaded_count": len(downloaded),
        "skipped_count": len(skipped),
        "files": [m["filename"] for m in files_meta],
        "details": files_meta,
        "staging_dir": config.STAGING_DIR,
    }


def download_documents(date: str, department: str = "SCM", document_type: str = "ALL") -> dict:
    """Fetch documents from the portal's SCM Document Query screen."""
    return _fetch(
        "scm_documents",
        lambda page: _search_documents(page, date, department, document_type),
        {"date": date, "department": department, "document_type": document_type},
    )


def download_esg_surveys(year: str = "2026", status: str = "ALL") -> dict:
    """Fetch questionnaires from the portal's ESG Questionnaires screen."""
    return _fetch(
        "esg_surveys",
        lambda page: _search_surveys(page, year, status),
        {"year": year, "status": status},
    )
