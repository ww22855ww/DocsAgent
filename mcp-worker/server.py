"""MCP Worker — Streamable HTTP MCP server plus a plain /health endpoint.

Every tool the agent may call is registered here. The set is kept small on
purpose: tool-selection reliability drops as the list grows, and each tool is
coarse enough that the agent never has to drive a multi-step sequence itself.

Blocking work (Playwright, psycopg, httpx) runs in a worker thread so the event
loop is never held up.
"""

from __future__ import annotations

import contextlib
import logging
import os

import anyio
import uvicorn
from mcp.server.fastmcp import FastMCP
from starlette.applications import Starlette
from starlette.responses import JSONResponse
from starlette.routing import Mount, Route

import config
from tools import archive as archive_tools
from tools import browser as browser_tools
from tools import documents as document_tools
from tools import excel as excel_tools
from tools import filesystem as fs_tools
from tools import mapping as mapping_tools
from tools import notify as notify_tools
from tools.common import ToolError

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s: %(message)s")
log = logging.getLogger("mcp-worker")

mcp = FastMCP("agentic-document-worker", stateless_http=True, json_response=True)
# streamable_http_app() serves at settings.streamable_http_path. Keep that at
# "/mcp" and mount the app at the root, so the endpoint is exactly /mcp with no
# trailing-slash redirect (a 307 on POST would break stricter MCP clients).
mcp.settings.streamable_http_path = "/mcp"


async def _run(fn, *args, **kwargs):
    """Run a blocking tool off the event loop, turning ToolError into a clean result."""
    try:
        return await anyio.to_thread.run_sync(lambda: fn(*args, **kwargs))
    except ToolError as exc:
        return {"status": "error", "error": str(exc)}


# ---------------------------------------------------------------------------
# Browser
# ---------------------------------------------------------------------------

@mcp.tool()
async def download_documents(date: str, department: str = "SCM", document_type: str = "ALL") -> dict:
    """Log into the document portal, search for documents, download them into staging.

    Args:
        date: Query date as YYYY-MM-DD.
        department: Department code, e.g. SCM.
        document_type: ALL, Invoice, QualityReport, DebitNote or Other.

    This is the SCM Document Query screen: invoices, quality reports, debit notes
    and correspondence. ESG questionnaires live on a different screen and need
    download_esg_surveys instead.

    Safe to call twice: documents already in staging are skipped, not re-downloaded.
    """
    return await _run(browser_tools.download_documents, date, department, document_type)


@mcp.tool()
async def download_esg_surveys(year: str = "2026", status: str = "ALL") -> dict:
    """Download supplier ESG questionnaires from the portal into staging.

    Args:
        year: Survey year, e.g. 2026.
        status: ALL, Submitted or Draft.

    This is the ESG Questionnaires screen, a different part of the portal from
    the SCM documents. Use it for sustainability or ESG survey tasks.

    Safe to call twice: files already in staging are skipped, not re-downloaded.
    """
    return await _run(browser_tools.download_esg_surveys, year, status)


# ---------------------------------------------------------------------------
# Files and parsing
# ---------------------------------------------------------------------------

@mcp.tool()
async def list_staging_files() -> dict:
    """List the documents currently waiting in staging."""
    return await _run(fs_tools.list_staging_files)


@mcp.tool()
async def extract_documents(filenames: list[str] | None = None) -> dict:
    """Read staged documents and return their text content for classification.

    Args:
        filenames: Specific files to parse. Omit to parse everything in staging.

    Parses all requested documents in one call, so prefer one batched call over
    one call per document.
    """
    return await _run(document_tools.extract_documents, filenames)


# ---------------------------------------------------------------------------
# Business mapping
# ---------------------------------------------------------------------------

@mcp.tool()
async def search_supplier(supplier_code: str | None = None, supplier_name: str | None = None) -> dict:
    """Look up a supplier by code and/or name. Name matching is fuzzy.

    Args:
        supplier_code: Exact supplier code, if the document has one.
        supplier_name: Vendor name or part of it. Use this when the code is missing.

    The `match` field is `unique`, `ambiguous` or `not_found`. Only `unique`
    settles the mapping; the others need another approach or manual review.
    """
    return await _run(mapping_tools.search_supplier, supplier_code, supplier_name)


@mcp.tool()
async def search_part(part_no: str) -> dict:
    """Look up a part by part number (exact match).

    Args:
        part_no: The part number as printed on the document.
    """
    return await _run(mapping_tools.search_part, part_no)


# ---------------------------------------------------------------------------
# Outcomes
# ---------------------------------------------------------------------------

@mcp.tool()
async def archive_record(
    filename: str,
    classification: dict | None = None,
    mapping: dict | None = None,
    task_id: str | None = None,
) -> dict:
    """Record a successfully processed document and move it out of staging.

    Args:
        filename: The staged file being archived.
        classification: The classification result for this document.
        mapping: The supplier/part lookup results that were applied.
        task_id: The task this document belongs to.
    """
    return await _run(archive_tools.archive_record, filename, classification, mapping, task_id)


@mcp.tool()
async def notify_manual_review(
    filename: str,
    reason: str,
    summary: str = "",
    task_id: str | None = None,
) -> dict:
    """Flag a document for manual review and notify the reviewer by email.

    Args:
        filename: The document that could not be processed automatically.
        reason: Why it needs a human, e.g. "category Unknown" or "3 possible suppliers".
        summary: Short description of what was found in the document.
        task_id: The task this document belongs to.

    The recipient is fixed by configuration; you choose only the wording.
    """
    return await _run(notify_tools.notify_manual_review, filename, reason, summary, task_id)


@mcp.tool()
async def write_excel(task_id: str, filename: str = "result.xlsx") -> dict:
    """Write the result workbook for a finished task into the output directory.

    Args:
        task_id: The task whose archived documents and manual reviews to report.
        filename: Output name, must end in .xlsx.

    Reads back what was committed to the database rather than what was reported,
    so the workbook cannot disagree with the stored record.
    """
    return await _run(excel_tools.write_excel, task_id, filename)


# ---------------------------------------------------------------------------
# Health and app wiring
# ---------------------------------------------------------------------------

@mcp.tool()
def ping() -> dict:
    """Health probe. Returns worker status and the active configuration modes."""
    return {
        "status": "ok",
        "dbquery_mode": config.DBQUERY_MODE,
        "mail_enabled": config.MAIL_ENABLED,
        "portal_url": config.PORTAL_URL,
    }


async def health(_request):
    return JSONResponse({
        "status": "ok",
        "app": "MCP Worker",
        "dbquery_mode": config.DBQUERY_MODE,
        "mail_enabled": config.MAIL_ENABLED,
    })


@contextlib.asynccontextmanager
async def lifespan(_app):
    for d in (config.DOWNLOADS_DIR, config.STAGING_DIR, config.ARCHIVE_DIR, config.OUTPUT_DIR):
        os.makedirs(d, exist_ok=True)
    log.info("MCP worker starting: dbquery_mode=%s mail_enabled=%s", config.DBQUERY_MODE, config.MAIL_ENABLED)
    async with mcp.session_manager.run():
        yield


app = Starlette(
    debug=False,
    routes=[Route("/health", health), Mount("/", app=mcp.streamable_http_app())],
    lifespan=lifespan,
)

if __name__ == "__main__":
    uvicorn.run(app, host="0.0.0.0", port=8000, log_level="info")
