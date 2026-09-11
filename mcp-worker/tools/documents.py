"""Document extraction, batched.

Batching matters: one call per document would make the agent's step count grow
with the document count and push it into max_steps. See roadmap.md section 11.
"""

from __future__ import annotations

import logging

from parsers.extract import extract

from .common import list_staging, staging_path

log = logging.getLogger("mcp-worker.documents")


def extract_documents(filenames: list[str] | None = None) -> dict:
    """Parse staged documents to text. Omit filenames to parse everything in staging."""
    targets = filenames if filenames else list_staging()

    documents = []
    for name in targets:
        path = staging_path(name)
        documents.append(extract(path))

    failed = [d["filename"] for d in documents if d.get("error")]
    log.info("extracted %d document(s), %d failed", len(documents), len(failed))

    return {
        "count": len(documents),
        "failed": failed,
        "documents": documents,
    }
