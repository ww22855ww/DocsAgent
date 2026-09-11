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

-- ---------------------------------------------------------------------------
-- Phase 5: the full task record.
--
-- agent-api owns these tables; mcp-worker owns archives and manual_reviews
-- above. The split follows who produces the data, not who happens to be
-- connected.
--
-- The schema lives here rather than in EF Core migrations: this file is already
-- the single source of truth, it runs on first boot via docker-entrypoint, and
-- every statement is IF NOT EXISTS so it can be replayed against a live
-- database. agent-api maps onto it and never creates or migrates it.
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS tasks (
    id          TEXT PRIMARY KEY,
    prompt      TEXT NOT NULL,
    mode        TEXT NOT NULL,
    state       TEXT NOT NULL,
    summary     TEXT,
    error       TEXT,
    started_at  TIMESTAMPTZ NOT NULL,
    finished_at TIMESTAMPTZ,
    duration_ms INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS task_steps (
    id          BIGSERIAL PRIMARY KEY,
    task_id     TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
    step_index  INTEGER NOT NULL,
    kind        TEXT NOT NULL,
    title       TEXT NOT NULL,
    detail      TEXT,
    thought     TEXT,
    tool_name   TEXT,
    arguments   JSONB,
    result      JSONB,
    success     BOOLEAN NOT NULL DEFAULT TRUE,
    duration_ms INTEGER NOT NULL DEFAULT 0,
    at          TIMESTAMPTZ NOT NULL,
    UNIQUE (task_id, step_index)
);

CREATE TABLE IF NOT EXISTS documents (
    id            BIGSERIAL PRIMARY KEY,
    task_id       TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
    filename      TEXT NOT NULL,
    status        TEXT NOT NULL,
    category      TEXT,
    supplier_code TEXT,
    supplier_name TEXT,
    part_no       TEXT,
    document_no   TEXT,
    review_reason TEXT,
    notified      BOOLEAN NOT NULL DEFAULT FALSE,
    UNIQUE (task_id, filename)
);

CREATE TABLE IF NOT EXISTS document_classifications (
    id                  BIGSERIAL PRIMARY KEY,
    task_id             TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
    filename            TEXT NOT NULL,
    category            TEXT NOT NULL,
    confidence          NUMERIC(4, 3),
    supplier_code       TEXT,
    supplier_name       TEXT,
    part_no             TEXT,
    document_no         TEXT,
    provider            TEXT,
    model               TEXT,
    latency_ms          INTEGER NOT NULL DEFAULT 0,
    needs_manual_review BOOLEAN NOT NULL DEFAULT FALSE,
    review_reason       TEXT,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS document_mappings (
    id         BIGSERIAL PRIMARY KEY,
    task_id    TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
    filename   TEXT,
    kind       TEXT NOT NULL,          -- supplier | part
    query      JSONB,
    match      TEXT NOT NULL,          -- unique | ambiguous | not_found
    source     TEXT,                   -- mock | oracle_ebs
    resolved   JSONB,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_task_steps_task ON task_steps (task_id, step_index);
CREATE INDEX IF NOT EXISTS idx_documents_task ON documents (task_id);
CREATE INDEX IF NOT EXISTS idx_classifications_task ON document_classifications (task_id);
CREATE INDEX IF NOT EXISTS idx_mappings_task ON document_mappings (task_id);
