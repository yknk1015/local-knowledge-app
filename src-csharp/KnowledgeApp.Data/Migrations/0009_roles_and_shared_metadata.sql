PRAGMA foreign_keys = OFF;
BEGIN IMMEDIATE;
CREATE TABLE users_new (
    id TEXT PRIMARY KEY,
    login_id TEXT NOT NULL CHECK (length(trim(login_id)) BETWEEN 1 AND 100),
    normalized_login_id TEXT NOT NULL UNIQUE,
    display_name TEXT NOT NULL CHECK (length(trim(display_name)) BETWEEN 1 AND 100),
    password_hash TEXT NOT NULL,
    role TEXT NOT NULL CHECK (role IN ('admin', 'editor', 'viewer')),
    is_active INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    last_login_at TEXT
);

INSERT INTO users_new SELECT id, login_id, normalized_login_id, display_name, password_hash,
    CASE role WHEN 'user' THEN 'editor' ELSE role END, is_active, created_at, updated_at, last_login_at FROM users;
DROP TABLE users;
ALTER TABLE users_new RENAME TO users;
CREATE INDEX ix_users_active_role ON users(is_active, role);
CREATE TRIGGER users_create_recovery AFTER INSERT ON users BEGIN
    INSERT INTO user_recovery_keys(user_id) VALUES (NEW.id);
END;

CREATE TRIGGER users_invalidate_recovery AFTER UPDATE OF password_hash, is_active, role ON users
WHEN OLD.password_hash != NEW.password_hash OR OLD.is_active != NEW.is_active OR OLD.role != NEW.role
BEGIN
    UPDATE user_recovery_keys SET key_hash = NULL, generation = generation + 1,
        auth_version = auth_version + 1, issued_at = NULL, failed_attempts = 0, blocked_until = NULL
    WHERE user_id = NEW.id;
END;


ALTER TABLE articles ADD COLUMN revision INTEGER NOT NULL DEFAULT 1 CHECK (revision > 0);
CREATE TRIGGER articles_revision AFTER UPDATE ON articles BEGIN
    UPDATE articles SET revision = OLD.revision + 1 WHERE id = NEW.id;
END;
ALTER TABLE search_logs ADD COLUMN user_id TEXT REFERENCES users(id) ON DELETE RESTRICT;
ALTER TABLE view_logs ADD COLUMN user_id TEXT REFERENCES users(id) ON DELETE RESTRICT;
CREATE INDEX ix_search_logs_user ON search_logs(user_id, created_at);
CREATE INDEX ix_view_logs_user ON view_logs(user_id, viewed_at);
CREATE TABLE codex_proposal_owners (request_id TEXT PRIMARY KEY, user_id TEXT NOT NULL REFERENCES users(id) ON DELETE RESTRICT, series_id TEXT NOT NULL);
CREATE INDEX ix_codex_proposal_owners_series ON codex_proposal_owners(series_id);
UPDATE search_logs SET user_id = (SELECT id FROM users WHERE role = 'admin' ORDER BY created_at, id LIMIT 1);
UPDATE view_logs SET user_id = (SELECT id FROM users WHERE role = 'admin' ORDER BY created_at, id LIMIT 1);
INSERT INTO app_settings(key, value_json, updated_at)
SELECT 'appearance:' || u.id, a.value_json, a.updated_at FROM app_settings a
JOIN users u ON u.id = (SELECT id FROM users WHERE role = 'admin' ORDER BY created_at, id LIMIT 1)
WHERE a.key = 'appearance' ON CONFLICT(key) DO NOTHING;
INSERT INTO codex_proposal_owners(request_id, user_id, series_id)
SELECT h.request_id, u.id, COALESCE(h.series_id, h.request_id) FROM codex_proposal_history h
JOIN users u ON u.id = (SELECT id FROM users WHERE role = 'admin' ORDER BY created_at, id LIMIT 1);
INSERT INTO schema_migrations(version, applied_at)
VALUES (9, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
COMMIT;
PRAGMA foreign_keys = ON;
