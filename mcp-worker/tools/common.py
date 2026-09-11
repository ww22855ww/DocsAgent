"""Shared helpers for the MCP tools: DB access, paths, and argument validation."""

from __future__ import annotations

import logging
import os
import re
import shutil

import psycopg
from psycopg.rows import dict_row

import config

log = logging.getLogger("mcp-worker.tools")

# Mapping arguments are interpolated into SQL templates for the real adapter, so
# they are restricted to an explicit character set first. Anything outside it is
# rejected rather than escaped, which keeps the rule easy to audit.
_SAFE_ARG = re.compile(r"^[A-Za-z0-9 ._@%+-]{1,80}$")


class ToolError(Exception):
    """Raised for input the agent can correct by calling again with better arguments."""


def validate_arg(value: str | None, field: str) -> str | None:
    """Whitelist-check one mapping argument. None and blank pass through as None."""
    if value is None:
        return None
    value = value.strip()
    if not value:
        return None
    if not _SAFE_ARG.match(value):
        raise ToolError(
            f"{field} contains characters that are not allowed. "
            "Use letters, digits, space, and . _ @ % + - only."
        )
    return value


def db():
    """Open a short-lived connection returning dict rows."""
    return psycopg.connect(config.PG_DSN, row_factory=dict_row)


def staging_path(filename: str) -> str:
    """Resolve a name inside the staging directory, refusing anything that escapes it."""
    if not filename or "/" in filename or "\\" in filename or filename.startswith("."):
        raise ToolError(f"Invalid filename: {filename!r}")
    full = os.path.normpath(os.path.join(config.STAGING_DIR, filename))
    if os.path.dirname(full) != os.path.normpath(config.STAGING_DIR):
        raise ToolError(f"Invalid filename: {filename!r}")
    return full


def list_staging() -> list[str]:
    if not os.path.isdir(config.STAGING_DIR):
        return []
    return sorted(
        f for f in os.listdir(config.STAGING_DIR)
        if os.path.isfile(os.path.join(config.STAGING_DIR, f)) and not f.startswith(".")
    )


def move_to(src: str, dest_dir: str) -> str:
    """Move a file into dest_dir, replacing any existing file of the same name."""
    os.makedirs(dest_dir, exist_ok=True)
    dest = os.path.join(dest_dir, os.path.basename(src))
    if os.path.exists(dest):
        os.remove(dest)
    shutil.move(src, dest)
    return dest


def human_size(n: int) -> str:
    return f"{n} B" if n < 1024 else f"{n / 1024:.1f} KB"
