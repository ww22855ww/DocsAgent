"""Generate the four demo documents served by the mock portal.

Deterministic on purpose: the demo numbers in roadmap.md section 2 assume this
exact set. Re-run it to restore the files after a demo reset.

Each file exercises a different path through the pipeline:
  supplier_001.xlsx  clean invoice, supplier code present        -> auto
  quality_002.xlsx   vendor NAME only, no code                   -> lookup by name
  debit_003.csv      flat CSV, supplier code present             -> auto
  unknown_004.txt    unstructured prose, no reliable fields      -> manual review

Run inside the mcp-worker image (it already has openpyxl):
  docker run --rm -v <repo>/mock-portal/MockData:/out \
      agentic-document-demo-mcp-worker python /out/../gen_mock_data.py
"""

import csv
import pathlib
import sys

from openpyxl import Workbook
from openpyxl.styles import Alignment, Font

OUT = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else "/out")

LABEL = Font(bold=True)
TITLE = Font(bold=True, size=14)


def _autosize(ws, widths):
    for col, w in widths.items():
        ws.column_dimensions[col].width = w


def supplier_invoice(path: pathlib.Path) -> None:
    """A well-formed supplier invoice. Everything needed for mapping is present."""
    wb = Workbook()
    ws = wb.active
    ws.title = "Invoice"

    ws["A1"] = "SUPPLIER INVOICE"
    ws["A1"].font = TITLE
    ws.merge_cells("A1:D1")
    ws["A1"].alignment = Alignment(horizontal="left")

    header = [
        ("InvoiceNo", "INV-001"),
        ("InvoiceDate", "2026-09-11"),
        ("SupplierCode", "V00123"),
        ("SupplierName", "Foxlink Precision"),
        ("Currency", "TWD"),
        ("Department", "SCM"),
    ]
    for i, (k, v) in enumerate(header, start=3):
        ws.cell(row=i, column=1, value=k).font = LABEL
        ws.cell(row=i, column=2, value=v)

    ws.cell(row=10, column=1, value="Line Items").font = LABEL
    cols = ["PartNo", "Description", "Qty", "UnitPrice", "Amount"]
    for c, name in enumerate(cols, start=1):
        ws.cell(row=11, column=c, value=name).font = LABEL
    ws.append([])  # keep the writer cursor clear of the merged title

    rows = [
        ("ABC-9981", "Connector housing 12P", 500, 12.5, 6250),
        ("ABC-9982", "Connector pin set", 200, 3.0, 600),
    ]
    for r, row in enumerate(rows, start=12):
        for c, val in enumerate(row, start=1):
            ws.cell(row=r, column=c, value=val)

    ws.cell(row=15, column=4, value="Total").font = LABEL
    ws.cell(row=15, column=5, value=6850)

    _autosize(ws, {"A": 18, "B": 24, "C": 10, "D": 12, "E": 12})
    wb.save(path)


def quality_report(path: pathlib.Path) -> None:
    """Quality report carrying the vendor NAME but no supplier code.

    This is the Phase 7 exception scenario: the agent must fall back to
    search_supplier(name="ACME") to resolve the mapping.
    """
    wb = Workbook()
    ws = wb.active
    ws.title = "IQC Report"

    ws["A1"] = "INCOMING QUALITY INSPECTION REPORT"
    ws["A1"].font = TITLE
    ws.merge_cells("A1:C1")

    fields = [
        ("Vendor", "ACME"),
        ("Lot", "LOT-99123"),
        ("InspectionDate", "2026-09-11"),
        ("Inspector", "QA-07"),
        ("SampleSize", 50),
        ("Defects", 6),
        ("Inspection Result", "FAIL"),
        ("Department", "SCM"),
    ]
    for i, (k, v) in enumerate(fields, start=3):
        ws.cell(row=i, column=1, value=k).font = LABEL
        ws.cell(row=i, column=2, value=v)

    ws.cell(row=12, column=1, value="Remark").font = LABEL
    ws.cell(row=12, column=2, value="Solder pad oxidation found on 6 of 50 sampled units.")

    _autosize(ws, {"A": 20, "B": 56, "C": 12})
    wb.save(path)


def debit_note(path: pathlib.Path) -> None:
    """Flat CSV debit note. Supplier code present, so mapping is direct."""
    with path.open("w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(["supplier", "reason", "amount", "currency", "debit_no", "department"])
        w.writerow(["V00321", "late delivery", 25000, "TWD", "DN-2026-0093", "SCM"])


def unknown_notice(path: pathlib.Path) -> None:
    """Unstructured prose with no dependable fields. Expected to reach manual review."""
    path.write_text(
        "Vendor ABC Corp.\n"
        "\n"
        "We have identified abnormal damage\n"
        "during incoming inspection.\n"
        "\n"
        "The affected cartons arrived with visible crushing on the outer\n"
        "packaging. Pending your reply we are holding the shipment in the\n"
        "quarantine area and have not booked it into stock.\n"
        "\n"
        "Reference: LOT-88881\n"
        "\n"
        "Please advise on disposition.\n",
        encoding="utf-8",
    )


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    builders = [
        ("supplier_001.xlsx", supplier_invoice),
        ("quality_002.xlsx", quality_report),
        ("debit_003.csv", debit_note),
        ("unknown_004.txt", unknown_notice),
    ]
    for name, build in builders:
        target = OUT / name
        build(target)
        print(f"wrote {target} ({target.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
