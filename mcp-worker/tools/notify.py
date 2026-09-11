"""Manual-review notification through the internal Mail API.

Credentials live in this process only. They are read from the environment, never
returned to the caller, and never placed in anything the agent or the LLM sees.
The recipient is fixed by configuration: the agent chooses the wording, not the
destination.

MAIL_ENABLED=false (the default) records the review and logs what would have
been sent without contacting the mail service.
"""

from __future__ import annotations

import base64
import html
import logging
import os

import httpx

import config
from .common import ToolError, db, staging_path

log = logging.getLogger("mcp-worker.notify")

MAX_ATTACH_BYTES = 4 * 1024 * 1024  # base64 inflates ~33%; keep the request sane


def _build_body(filename: str, reason: str, summary: str) -> str:
    return (
        "<p>A document could not be processed automatically and needs manual review.</p>"
        "<table cellpadding='6' style='border-collapse:collapse'>"
        f"<tr><td><b>File</b></td><td>{html.escape(filename)}</td></tr>"
        f"<tr><td><b>Reason</b></td><td>{html.escape(reason)}</td></tr>"
        f"<tr><td><b>Summary</b></td><td>{html.escape(summary or '-')}</td></tr>"
        "</table>"
        "<p style='color:#666;font-size:12px'>Sent by the Agentic Document Processing demo.</p>"
    )


def _attachment(filename: str) -> dict | None:
    """Attach the source document so the recipient can act without the portal."""
    try:
        path = staging_path(filename)
    except ToolError:
        return None
    if not os.path.isfile(path) or os.path.getsize(path) > MAX_ATTACH_BYTES:
        return None
    with open(path, "rb") as f:
        return {"filename": filename, "content_base64": base64.b64encode(f.read()).decode("ascii")}


def _send(subject: str, body: str, attachment: dict | None) -> tuple[bool, str]:
    missing = [k for k, v in (("MAIL_USERNAME", config.MAIL_USERNAME),
                              ("MAIL_PASSWORD", config.MAIL_PASSWORD),
                              ("MAIL_TO", config.MAIL_TO)) if not v]
    if missing:
        return False, f"mail not configured, missing: {', '.join(missing)}"

    payload = {
        "username": config.MAIL_USERNAME,
        "password": config.MAIL_PASSWORD,
        "to": [t.strip() for t in config.MAIL_TO.split(",") if t.strip()],
        "subject": subject,
        "body": body,
    }
    if config.MAIL_FROM_EMAIL:
        payload["from_email"] = config.MAIL_FROM_EMAIL
    if attachment:
        payload["attachments"] = [attachment]

    url = f"{config.MAIL_API_BASE_URL.rstrip('/')}/api/send"
    try:
        with httpx.Client(timeout=config.MAIL_TIMEOUT_SEC) as client:
            res = client.post(url, json=payload)
        data = res.json() if res.headers.get("content-type", "").startswith("application/json") else {}
        ok = bool(data.get("success")) and res.is_success
        # data["message"] echoes sender and recipient only, never the password.
        return ok, str(data.get("message") or f"HTTP {res.status_code}")
    except Exception as exc:  # noqa: BLE001 - reported to the agent as data
        return False, f"{type(exc).__name__}: {exc}"


def notify_manual_review(
    filename: str,
    reason: str,
    summary: str = "",
    task_id: str | None = None,
) -> dict:
    """Flag a document for manual review and email the reviewer.

    Always records the review. Whether mail actually goes out depends on
    MAIL_ENABLED and on the mail credentials being configured.
    """
    if not reason or not reason.strip():
        raise ToolError("Provide a reason for manual review.")

    subject = f"[Manual Review] {filename}"
    body = _build_body(filename, reason, summary)
    attachment = _attachment(filename)

    if config.MAIL_ENABLED:
        sent, detail = _send(subject, body, attachment)
    else:
        sent, detail = False, "MAIL_ENABLED=false, notification logged but not sent"
        log.info("manual review (mail disabled): %s | %s", filename, reason)

    with db() as conn, conn.cursor() as cur:
        cur.execute(
            """
            INSERT INTO manual_reviews (task_id, filename, reason, summary, notified, notify_detail)
            VALUES (%s, %s, %s, %s, %s, %s)
            RETURNING id, created_at
            """,
            (task_id, filename, reason, summary, sent, detail),
        )
        saved = cur.fetchone()
        conn.commit()

    return {
        "status": "manual_review_recorded",
        "review_id": saved["id"],
        "filename": filename,
        "reason": reason,
        "mail_enabled": config.MAIL_ENABLED,
        "notified": sent,
        "detail": detail,
        "attached": attachment is not None,
        "created_at": saved["created_at"].isoformat(),
    }
