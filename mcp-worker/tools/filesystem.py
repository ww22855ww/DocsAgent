"""Staging directory inspection."""

from __future__ import annotations

import os

import config
from .common import human_size, list_staging


def list_staging_files() -> dict:
    """List the files currently waiting in staging."""
    names = list_staging()
    files = []
    for n in names:
        p = os.path.join(config.STAGING_DIR, n)
        files.append({
            "filename": n,
            "extension": os.path.splitext(n)[1].lower(),
            "size": human_size(os.path.getsize(p)),
        })
    return {"count": len(files), "files": files, "staging_dir": config.STAGING_DIR}
