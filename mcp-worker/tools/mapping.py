"""Supplier and part lookup, with interchangeable mock and real backends.

Security boundary (roadmap.md section 20): the LLM never produces SQL. It calls
these tools with plain values; the SQL is a fixed template here, every argument
is whitelist-validated by common.validate_arg before interpolation, and only
SELECT is ever issued. The real backend runs against Oracle EBS through an API
that will execute whatever SQL it is handed, so this boundary is load-bearing.

Both backends return the same field shape, so nothing above this module needs to
know which one is active:

    {"supplier_code", "supplier_name", "vendor_id", "source"}
    {"part_no", "description", "supplier_code", "source"}
"""

from __future__ import annotations

import logging

import httpx

import config
from .common import ToolError, db, validate_arg

log = logging.getLogger("mcp-worker.mapping")

MAX_ROWS = 10


# --------------------------------------------------------------------------
# Real backend: internal SQL Execution API over Oracle EBS
# --------------------------------------------------------------------------

def _run_sql(sql: str) -> list[dict]:
    """POST one SELECT to the SQL Execution API and return its rows."""
    stripped = sql.strip().lstrip("(").lstrip().lower()
    if not stripped.startswith("select"):
        raise ToolError("Only SELECT statements may be sent to the query API.")

    url = f"{config.DBQUERY_BASE_URL.rstrip('/')}{config.DBQUERY_PATH}"
    with httpx.Client(timeout=config.DBQUERY_TIMEOUT_SEC) as client:
        res = client.post(url, json={"SQL": sql})
        res.raise_for_status()
        payload = res.json()

    if isinstance(payload, dict):
        payload = payload.get("data") or payload.get("rows") or []
    return payload if isinstance(payload, list) else []


def _real_supplier(code: str | None, name: str | None) -> list[dict]:
    where = []
    if code:
        where.append(f"segment1 = '{code}'")
    if name:
        where.append(f"upper(vendor_name) like upper('%{name}%')")
    clause = " or ".join(where)

    sql = (
        "select vendor_id, segment1, vendor_name "
        "from apps.po_vendors "
        f"where ({clause}) and rownum <= {MAX_ROWS}"
    )
    log.info("dbquery supplier: %s", sql)

    return [
        {
            "supplier_code": r.get("SEGMENT1"),
            "supplier_name": r.get("VENDOR_NAME"),
            "vendor_id": r.get("VENDOR_ID"),
            "source": "oracle_ebs",
        }
        for r in _run_sql(sql)
    ]


# The item master holds one row per inventory organization (155 for a typical
# part), so an unfiltered lookup returns the same part many times and reads as
# ambiguous. Organization 1 is the master org and carries exactly one row.
MASTER_ORG_ID = 1


def _real_part(part_no: str) -> list[dict]:
    sql = (
        "select segment1, description "
        "from apps.mtl_system_items_b "
        f"where segment1 = '{part_no}' and organization_id = {MASTER_ORG_ID} "
        f"and rownum <= {MAX_ROWS}"
    )
    log.info("dbquery part: %s", sql)

    return [
        {
            "part_no": r.get("SEGMENT1"),
            "description": r.get("DESCRIPTION"),
            "supplier_code": None,  # not carried on the item master
            "source": "oracle_ebs",
        }
        for r in _run_sql(sql)
    ]


# --------------------------------------------------------------------------
# Mock backend: seeded tables in the demo's own Postgres
# --------------------------------------------------------------------------

def _mock_supplier(code: str | None, name: str | None) -> list[dict]:
    clauses, params = [], []
    if code:
        clauses.append("supplier_code = %s")
        params.append(code)
    if name:
        clauses.append("upper(supplier_name) LIKE upper(%s)")
        params.append(f"%{name}%")

    sql = (
        "SELECT supplier_code, supplier_name, vendor_id FROM mock_suppliers "
        f"WHERE enabled AND ({' OR '.join(clauses)}) ORDER BY supplier_code LIMIT {MAX_ROWS}"
    )
    with db() as conn, conn.cursor() as cur:
        cur.execute(sql, params)
        rows = cur.fetchall()

    return [{**r, "source": "mock"} for r in rows]


def _mock_part(part_no: str) -> list[dict]:
    with db() as conn, conn.cursor() as cur:
        cur.execute(
            "SELECT part_no, description, supplier_code FROM mock_parts "
            "WHERE upper(part_no) = upper(%s) LIMIT %s",
            (part_no, MAX_ROWS),
        )
        rows = cur.fetchall()

    return [{**r, "source": "mock"} for r in rows]


# --------------------------------------------------------------------------
# Tool entry points
# --------------------------------------------------------------------------

def _verdict(results: list[dict]) -> str:
    if not results:
        return "not_found"
    return "unique" if len(results) == 1 else "ambiguous"


def search_supplier(supplier_code: str | None = None, supplier_name: str | None = None) -> dict:
    """Look up a supplier by code, by name, or both. Name matching is fuzzy.

    `match` tells the agent what to do next: `unique` means the mapping is
    settled, `ambiguous` and `not_found` mean it is not.
    """
    code = validate_arg(supplier_code, "supplier_code")
    name = validate_arg(supplier_name, "supplier_name")
    if not code and not name:
        raise ToolError("Provide supplier_code or supplier_name.")

    results = (_real_supplier if config.DBQUERY_MODE == "real" else _mock_supplier)(code, name)
    return {
        "mode": config.DBQUERY_MODE,
        "query": {"supplier_code": code, "supplier_name": name},
        "match": _verdict(results),
        "count": len(results),
        "results": results,
    }


def search_part(part_no: str) -> dict:
    """Look up a part by its part number (exact match)."""
    pn = validate_arg(part_no, "part_no")
    if not pn:
        raise ToolError("Provide part_no.")

    results = (_real_part if config.DBQUERY_MODE == "real" else _mock_part)(pn)
    return {
        "mode": config.DBQUERY_MODE,
        "query": {"part_no": pn},
        "match": _verdict(results),
        "count": len(results),
        "results": results,
    }
