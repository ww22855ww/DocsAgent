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

-- Real vendors and part numbers, taken from Oracle EBS: suppliers with 2026
-- purchase orders at operating unit 152, and items actually bought from them.
-- Using real master data means DBQUERY_MODE=mock and DBQUERY_MODE=real answer
-- the same questions the same way, so switching modes during the demo proves
-- the adapter rather than changing the story.
--
-- 勝宏科技 is a genuine two-entity ambiguity in the live data: the Huizhou and
-- Thailand companies share a name stem. scripts/scenario.py enables or disables
-- the Thailand row to switch the demo between the resolvable and the ambiguous
-- case. In DBQUERY_MODE=real the name is always ambiguous, which is the point.
INSERT INTO mock_suppliers (supplier_code, supplier_name, vendor_id, enabled) VALUES
    ('3707',    '華碩電腦股份有限公司',       4056,    TRUE),
    ('3385',    '聯強國際股份有限公司',       3598,    TRUE),
    ('40891',   '勝宏科技(惠州)股份有限公司', 70356,   TRUE),
    ('2410179', '勝宏科技（泰國）有限公司',   4092820, FALSE),
    ('9414',    '瀚宇博德科技(江陰)有限公司', 61927,   TRUE),
    ('2607',    '福華電子股份有限公司',       2607,    TRUE),
    ('50163',   '至上電子股份有限公司',       10789,   TRUE),
    ('3150',    '麗臺科技股份有限公司',       3150,    TRUE)
ON CONFLICT (supplier_code) DO NOTHING;

INSERT INTO mock_parts (part_no, description, supplier_code) VALUES
    ('05-152-649111', 'RES.649 OHM.1/16W.1%...SMD 0402......LEAD-FREE(RoHS/HF)', '9414'),
    ('04-888-105003', 'C/C.1uF.10V.10%..X5R.....SMD 0402....LEAD-FREE(RoHS/HF)', '9414'),
    ('10-261-004393', 'HEADER.BV..4*1 180D SMD..P1.25mm......46.W/CAP..NATURE', '2607'),
    ('05C531-103530', 'THERMISTOR NTC.10K..3%.(CI-ASUS).SMD 0603..NCP18XH103E03RB', '3707')
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
