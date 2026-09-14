"""Switch which vendor the quality report names, and therefore what the agent can do.

quality_002.xlsx carries a vendor name and no supplier code, so the agent has to
look the vendor up by name. What it finds decides the outcome, and both outcomes
come from real Oracle EBS master data:

    unique     百辰光電   matches exactly one vendor  -> agent resolves it and archives
    ambiguous  勝宏科技   matches two real companies  -> agent declines and hands over

Nothing is staged. The Huizhou and Thailand 勝宏科技 companies both exist and both
have 2026 purchase orders; 百辰光電 genuinely has one record. The switch only
changes which name is printed on the document.

    python scripts/scenario.py            # show current state
    python scripts/scenario.py ambiguous
    python scripts/scenario.py unique     # default

Regenerating the document needs the mcp-worker image, which has openpyxl.
"""

from __future__ import annotations

import json
import pathlib
import os
import subprocess
import sys
import urllib.request

REPO = pathlib.Path(__file__).resolve().parent.parent
IMAGE = "agentic-document-demo-mcp-worker"
DOCUMENT = REPO / "mock-portal" / "MockData" / "quality_002.xlsx"

VENDORS = {
    "unique": "百辰光電",
    "ambiguous": "勝宏科技",
}

DBQUERY_URL = "https://srm.ecs.com.tw:8017/F83/T1"


def _docker(args: list[str], **kw) -> subprocess.CompletedProcess:
    """Run docker, reading its output as UTF-8 rather than the console code page."""
    return subprocess.run(
        ["docker", *args],
        capture_output=True, text=True, encoding="utf-8", errors="replace",
        env={**os.environ, "MSYS_NO_PATHCONV": "1"}, **kw)


def current_vendor() -> str | None:
    """Read the vendor straight out of the workbook, so this reports fact not intent."""
    code = (
        "from openpyxl import load_workbook;"
        "ws = load_workbook('/out/quality_002.xlsx').active;"
        "print(next((ws.cell(row=r, column=2).value for r in range(1, 15)"
        " if ws.cell(row=r, column=1).value == 'Vendor'), ''))"
    )
    out = _docker(["run", "--rm", "-v", f"{DOCUMENT.parent.as_posix()}:/out:ro",
                   IMAGE, "python", "-c", code])
    return out.stdout.strip() or None


def ebs_matches(name: str) -> list[tuple[str, str]]:
    """Ask the live ERP how many vendors that name matches."""
    sql = (f"select segment1, vendor_name from apps.po_vendors "
           f"where vendor_name like '%{name}%' and rownum <= 8")
    req = urllib.request.Request(
        DBQUERY_URL, data=json.dumps({"SQL": sql}).encode("utf-8"),
        headers={"Content-Type": "application/json"}, method="POST")
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            rows = json.loads(r.read().decode("utf-8"))
        return [(x["SEGMENT1"], x["VENDOR_NAME"]) for x in rows]
    except Exception as exc:
        print(f"  (無法連線 ERP 查詢：{exc})")
        return []


def regenerate(vendor: str) -> None:
    _docker(["run", "--rm",
             "-v", f"{(REPO / 'scripts').as_posix()}:/scripts:ro",
             "-v", f"{DOCUMENT.parent.as_posix()}:/out",
             IMAGE, "python", "/scripts/gen_mock_data.py", "/out", vendor],
            check=True)


def show() -> None:
    vendor = current_vendor()
    if not vendor:
        print("找不到 quality_002.xlsx，先跑 ./scripts/reset-demo.ps1")
        return

    print(f"quality_002.xlsx 上印的廠商：{vendor}")
    matches = ebs_matches(vendor)
    if matches:
        print(f"ERP 主檔比對結果：{len(matches)} 筆")
        for code, name in matches:
            print(f"  {code:<10} {name}")
        if len(matches) == 1:
            print("\n→ 唯一命中，agent 會自己完成對應並歸檔")
        else:
            print("\n→ 多筆命中，agent 不會挑，會轉人工複核")


def main() -> int:
    arg = (sys.argv[1] if len(sys.argv) > 1 else "show").lower()

    if arg in VENDORS:
        regenerate(VENDORS[arg])
        print(f"已重新產生文件，廠商改為 {VENDORS[arg]}\n")
    elif arg not in ("show", "status"):
        print(__doc__)
        return 2

    show()
    return 0


if __name__ == "__main__":
    sys.exit(main())
