-- Agentic Document Processing Demo — schema + mock mapping data
-- Phase 0: mock mapping tables + a health marker.
-- Phase 5 fills in the full task/document tables.

CREATE TABLE IF NOT EXISTS mock_suppliers (
    supplier_code TEXT PRIMARY KEY,
    supplier_name TEXT NOT NULL,
    vendor_id     INTEGER,
    enabled       BOOLEAN NOT NULL DEFAULT TRUE
);

CREATE TABLE IF NOT EXISTS mock_parts (
    part_no       TEXT PRIMARY KEY,
    description   TEXT NOT NULL,
    supplier_code TEXT REFERENCES mock_suppliers(supplier_code)
);

INSERT INTO mock_suppliers (supplier_code, supplier_name, vendor_id, enabled) VALUES
    ('V00123', 'Foxlink Precision',  310001, TRUE),
    ('V00321', 'Delta Components',   310002, TRUE),
    ('V00555', 'ACME Electronics',   310003, TRUE),
    -- Scenario B (multiple matches for "ACME"). Disabled by default so the
    -- default demo run resolves ACME to exactly one supplier.
    ('V00556', 'ACME Logistics',     310004, FALSE)
ON CONFLICT (supplier_code) DO NOTHING;

INSERT INTO mock_parts (part_no, description, supplier_code) VALUES
    ('ABC-9981', 'Connector housing 12P', 'V00123'),
    ('ABC-9982', 'Connector pin set',     'V00123'),
    ('DLT-2201', 'Power module 65W',      'V00321')
ON CONFLICT (part_no) DO NOTHING;

CREATE INDEX IF NOT EXISTS idx_mock_suppliers_name
    ON mock_suppliers (UPPER(supplier_name));
