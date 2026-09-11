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

-- ---------------------------------------------------------------------------
-- Phase 2: the tables the archive and manual-review tools write to.
-- Phase 5 extends this with tasks / task_steps / documents and the full
-- classification and mapping history. Everything here is IF NOT EXISTS so the
-- file stays re-runnable against a live database.
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS archives (
    id              BIGSERIAL PRIMARY KEY,
    task_id         TEXT,
    filename        TEXT NOT NULL,
    category        TEXT,
    confidence      NUMERIC(4, 3),
    document_no     TEXT,
    supplier_code   TEXT,
    supplier_name   TEXT,
    part_no         TEXT,
    classification  JSONB,
    mapping         JSONB,
    archived_path   TEXT,
    archived_at     TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_archives_task ON archives (task_id);
CREATE INDEX IF NOT EXISTS idx_archives_filename ON archives (filename);

CREATE TABLE IF NOT EXISTS manual_reviews (
    id            BIGSERIAL PRIMARY KEY,
    task_id       TEXT,
    filename      TEXT NOT NULL,
    reason        TEXT NOT NULL,
    summary       TEXT,
    notified      BOOLEAN NOT NULL DEFAULT FALSE,
    notify_detail TEXT,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_manual_reviews_task ON manual_reviews (task_id);
