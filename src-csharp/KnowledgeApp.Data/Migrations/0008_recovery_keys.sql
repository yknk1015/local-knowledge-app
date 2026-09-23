BEGIN IMMEDIATE;

CREATE TABLE user_recovery_keys (
    user_id TEXT PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,
    key_hash TEXT,
    generation INTEGER NOT NULL DEFAULT 0 CHECK (generation >= 0),
    auth_version INTEGER NOT NULL DEFAULT 0 CHECK (auth_version >= 0),
    setup_state TEXT NOT NULL DEFAULT 'pending' CHECK (setup_state IN ('pending', 'existing', 'skipped', 'issued')),
    issued_at TEXT,
    failed_attempts INTEGER NOT NULL DEFAULT 0 CHECK (failed_attempts BETWEEN 0 AND 5),
    blocked_until TEXT
);

INSERT INTO user_recovery_keys(user_id, setup_state)
SELECT id, 'existing' FROM users;

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

INSERT INTO schema_migrations(version, applied_at)
VALUES (8, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
COMMIT;
