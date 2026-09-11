"""Switch the ambiguous-supplier scenario on or off between demo runs.

The mock supplier table holds two vendors whose names both contain "ACME".
V00556 ships disabled, so a lookup by the name on quality_002.xlsx resolves to
exactly one row and the agent can finish the document on its own.

Enabling V00556 makes that same lookup ambiguous. Nothing else changes: the
document, the prompt and the tools are identical. What changes is whether the
agent has enough information to decide, which is the point being demonstrated.

    python scripts/scenario.py            # show current state
    python scripts/scenario.py ambiguous  # two ACME suppliers
    python scripts/scenario.py unique     # one ACME supplier (default)
"""

from __future__ import annotations

import subprocess
import sys

CONTAINER = "adp-postgres"
AMBIGUOUS_CODE = "V00556"


def psql(sql: str) -> str:
    result = subprocess.run(
        ["docker", "exec", CONTAINER, "psql", "-U", "agent", "-d", "agentdemo", "-tAc", sql],
        capture_output=True, text=True, encoding="utf-8",
    )
    if result.returncode != 0:
        raise SystemExit(f"psql failed: {result.stderr.strip()}")
    return result.stdout.strip()


def show() -> None:
    rows = psql(
        "SELECT supplier_code || '  ' || supplier_name || '  enabled=' || enabled "
        "FROM mock_suppliers WHERE upper(supplier_name) LIKE '%ACME%' ORDER BY supplier_code"
    )
    enabled = psql(
        "SELECT count(*) FROM mock_suppliers "
        "WHERE enabled AND upper(supplier_name) LIKE '%ACME%'"
    )
    print("ACME suppliers:")
    for line in rows.splitlines():
        print("  " + line)
    mode = "ambiguous" if int(enabled) > 1 else "unique"
    print(f"\nlookup for \"ACME\" -> {mode} ({enabled} enabled)")
    print("quality_002.xlsx will be " +
          ("sent to manual review" if mode == "ambiguous" else "archived by the agent"))


def main() -> int:
    arg = (sys.argv[1] if len(sys.argv) > 1 else "show").lower()

    if arg in ("ambiguous", "on"):
        psql(f"UPDATE mock_suppliers SET enabled = TRUE WHERE supplier_code = '{AMBIGUOUS_CODE}'")
    elif arg in ("unique", "off"):
        psql(f"UPDATE mock_suppliers SET enabled = FALSE WHERE supplier_code = '{AMBIGUOUS_CODE}'")
    elif arg not in ("show", "status"):
        print(__doc__)
        return 2

    show()
    return 0


if __name__ == "__main__":
    sys.exit(main())
