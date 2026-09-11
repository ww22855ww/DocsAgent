"""MCP Worker — Streamable HTTP MCP server plus a plain /health endpoint.

Phase 0 exposes the transport and health only; the document tools are
registered in Phase 2 (tools/ package).
"""
import contextlib
import logging
import os

import uvicorn
from mcp.server.fastmcp import FastMCP
from starlette.applications import Starlette
from starlette.responses import JSONResponse
from starlette.routing import Mount, Route

import config

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s: %(message)s")
log = logging.getLogger("mcp-worker")

mcp = FastMCP("agentic-document-worker", stateless_http=True, json_response=True)
# streamable_http_app() serves at settings.streamable_http_path. Keep that at
# "/mcp" and mount the app at the root, so the endpoint is exactly /mcp with no
# trailing-slash redirect (a 307 on POST would break stricter MCP clients).
mcp.settings.streamable_http_path = "/mcp"


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
    return JSONResponse(
        {
            "status": "ok",
            "app": "MCP Worker",
            "dbquery_mode": config.DBQUERY_MODE,
            "mail_enabled": config.MAIL_ENABLED,
        }
    )


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
