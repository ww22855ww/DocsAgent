"""Central configuration, read from environment (docker compose env_file)."""
import os


def _bool(name: str, default: bool = False) -> bool:
    return os.getenv(name, str(default)).strip().lower() in ("1", "true", "yes", "on")


def _int(name: str, default: int) -> int:
    try:
        return int(os.getenv(name, "").strip() or default)
    except ValueError:
        return default


# Storage roots (bind-mounted to ./data on the host)
DOWNLOADS_DIR = os.getenv("DOWNLOADS_DIR", "/app/downloads")
STAGING_DIR = os.getenv("STAGING_DIR", "/app/staging")
ARCHIVE_DIR = os.getenv("ARCHIVE_DIR", "/app/archive")
OUTPUT_DIR = os.getenv("OUTPUT_DIR", "/app/output")

# Mock portal
PORTAL_URL = os.getenv("PORTAL_URL", "http://mock-portal:8080")
PORTAL_USERNAME = os.getenv("PORTAL_USERNAME", "admin")
PORTAL_PASSWORD = os.getenv("PORTAL_PASSWORD", "123456")

# Business mapping source
DBQUERY_MODE = os.getenv("DBQUERY_MODE", "mock").strip().lower()
DBQUERY_BASE_URL = os.getenv("DBQUERY_BASE_URL", "https://srm.ecs.com.tw:8017")
DBQUERY_PATH = os.getenv("DBQUERY_PATH", "/F83/T1")
DBQUERY_TIMEOUT_SEC = _int("DBQUERY_TIMEOUT_SEC", 60)

# Mail
MAIL_API_BASE_URL = os.getenv("MAIL_API_BASE_URL", "https://scm.ecs.com.tw:8020")
MAIL_USERNAME = os.getenv("MAIL_USERNAME", "")
MAIL_PASSWORD = os.getenv("MAIL_PASSWORD", "")
MAIL_FROM_EMAIL = os.getenv("MAIL_FROM_EMAIL", "")
MAIL_TO = os.getenv("MAIL_TO", "")
MAIL_TIMEOUT_SEC = _int("MAIL_TIMEOUT_SEC", 60)
# Guard rail: when false, notify_manual_review logs instead of sending.
MAIL_ENABLED = _bool("MAIL_ENABLED", False)

# Postgres
PG_HOST = os.getenv("PG_HOST", "postgres")
PG_PORT = _int("PG_PORT", 5432)
PG_DB = os.getenv("POSTGRES_DB", "agentdemo")
PG_USER = os.getenv("POSTGRES_USER", "agent")
PG_PASSWORD = os.getenv("POSTGRES_PASSWORD", "")

PG_DSN = f"host={PG_HOST} port={PG_PORT} dbname={PG_DB} user={PG_USER} password={PG_PASSWORD}"
