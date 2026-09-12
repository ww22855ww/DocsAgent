"""Switch the ambiguous-supplier scenario on or off between demo runs.

勝宏科技 is a real two-entity vendor group in Oracle EBS: the Huizhou company
(40891) and the Thailand company (2410179) share a name stem, and both have 2026
purchase orders. The quality report names only "勝宏科技", with no supplier code.

The Thailand row ships disabled, so the lookup resolves to exactly one vendor and
the agent can finish the document on its own. Enabling it makes the same lookup
ambiguous. Nothing else changes: same document, same prompt, same tools. What
changes is whether the agent has enough information to decide.

In DBQUERY_MODE=real this name is always ambiguous, because both companies really
do exist. The toggle only shapes the mock data.

    python scripts/scenario.py            # show current state
    python scripts/scenario.py ambiguous  # both 勝宏科技 entities
    python scripts/scenario.py unique     # Huizhou only (default)
"""

from __future__ import annotations

import subprocess
import sys

CONTAINER = "adp-postgres"
AMBIGUOUS_CODE = "2410179"
NAME_STEM = "勝宏科技"


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
        f"FROM mock_suppliers WHERE supplier_name LIKE '%{NAME_STEM}%' ORDER BY supplier_code"
    )
    enabled = psql(
        f"SELECT count(*) FROM mock_suppliers "
        f"WHERE enabled AND supplier_name LIKE '%{NAME_STEM}%'"
    )
    print(f"{NAME_STEM} suppliers:")
    for line in rows.splitlines():
        print("  " + line)
    mode = "ambiguous" if int(enabled) > 1 else "unique"
    print(f"\nlookup for \"{NAME_STEM}\" -> {mode} ({enabled} enabled)")
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
