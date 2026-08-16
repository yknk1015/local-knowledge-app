BEGIN IMMEDIATE;

CREATE TABLE IF NOT EXISTS users (
    id TEXT PRIMARY KEY,
    login_id TEXT NOT NULL CHECK (length(trim(login_id)) BETWEEN 1 AND 100),
    normalized_login_id TEXT NOT NULL UNIQUE,
    display_name TEXT NOT NULL CHECK (length(trim(display_name)) BETWEEN 1 AND 100),
    password_hash TEXT NOT NULL,
    role TEXT NOT NULL CHECK (role IN ('admin', 'user')),
    is_active INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    last_login_at TEXT
);

CREATE INDEX IF NOT EXISTS ix_users_active_role
    ON users(is_active, role);

ALTER TABLE articles ADD COLUMN created_by_user_id TEXT REFERENCES users(id) ON DELETE RESTRICT;
ALTER TABLE articles ADD COLUMN updated_by_user_id TEXT REFERENCES users(id) ON DELETE RESTRICT;

INSERT OR IGNORE INTO schema_migrations(version, applied_at)
VALUES (6, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));

COMMIT;
