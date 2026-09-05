BEGIN IMMEDIATE;

ALTER TABLE articles ADD COLUMN new_badge_until TEXT;
ALTER TABLE articles ADD COLUMN updated_badge_until TEXT;
ALTER TABLE articles ADD COLUMN is_hidden INTEGER NOT NULL DEFAULT 0 CHECK (is_hidden IN (0, 1));

CREATE INDEX IF NOT EXISTS ix_articles_visibility
    ON articles(is_hidden, status, deleted_at, updated_at DESC);

INSERT INTO schema_migrations(version, applied_at)
VALUES (2, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));

COMMIT;
