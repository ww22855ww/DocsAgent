"""Archive a processed document: record the outcome, then move the file."""

from __future__ import annotations

import json
import logging
import os

import config
from .common import ToolError, db, move_to, staging_path

log = logging.getLogger("mcp-worker.archive")


def archive_record(
    filename: str,
    classification: dict | None = None,
    mapping: dict | None = None,
    task_id: str | None = None,
) -> dict:
    """Write the result row and move the file from staging to archive.

    The database row is written first: if the move then fails, the outcome is
    still recorded rather than silently lost.
    """
    path = staging_path(filename)
    if not os.path.isfile(path):
        raise ToolError(f"{filename} is not in staging. Call list_staging_files to see what is.")

    classification = classification or {}
    mapping = mapping or {}

    # Pull the headline fields out for easy querying; keep the full payloads too.
    confidence = classification.get("confidence")
    try:
        confidence = round(float(confidence), 3) if confidence is not None else None
    except (TypeError, ValueError):
        confidence = None

    supplier = (mapping.get("supplier") or {}) if isinstance(mapping.get("supplier"), dict) else {}

    row = {
        "task_id": task_id,
        "filename": filename,
        "category": classification.get("category"),
        "confidence": confidence,
        "document_no": classification.get("document_no"),
        "supplier_code": supplier.get("supplier_code") or classification.get("supplier_code"),
        "supplier_name": supplier.get("supplier_name") or classification.get("supplier_name"),
        "part_no": classification.get("part_no"),
        "classification": json.dumps(classification, ensure_ascii=False),
        "mapping": json.dumps(mapping, ensure_ascii=False),
    }

    archived_path = os.path.join(config.ARCHIVE_DIR, filename)

    with db() as conn, conn.cursor() as cur:
        cur.execute(
            """
            INSERT INTO archives (task_id, filename, category, confidence, document_no,
                                  supplier_code, supplier_name, part_no,
                                  classification, mapping, archived_path)
            VALUES (%(task_id)s, %(filename)s, %(category)s, %(confidence)s, %(document_no)s,
                    %(supplier_code)s, %(supplier_name)s, %(part_no)s,
                    %(classification)s, %(mapping)s, %(archived_path)s)
            RETURNING id, archived_at
            """,
            {**row, "archived_path": archived_path},
        )
        saved = cur.fetchone()
        conn.commit()

    final_path = move_to(path, config.ARCHIVE_DIR)
    log.info("archived %s as id=%s", filename, saved["id"])

    return {
        "status": "archived",
        "archive_id": saved["id"],
        "filename": filename,
        "category": row["category"],
        "supplier_code": row["supplier_code"],
        "archived_path": final_path,
        "archived_at": saved["archived_at"].isoformat(),
    }
