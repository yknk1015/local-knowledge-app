BEGIN IMMEDIATE;

ALTER TABLE categories ADD COLUMN description TEXT NOT NULL DEFAULT ''
    CHECK (length(description) <= 500);

CREATE TABLE IF NOT EXISTS codex_proposal_receipts (
    request_id TEXT PRIMARY KEY,
    article_id TEXT NOT NULL REFERENCES articles(id) ON DELETE RESTRICT,
    accepted_at TEXT NOT NULL
);

INSERT INTO schema_migrations(version, applied_at)
VALUES (3, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));

COMMIT;
