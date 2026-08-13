BEGIN IMMEDIATE;

CREATE TABLE codex_proposal_history (
    history_id INTEGER PRIMARY KEY AUTOINCREMENT,
    request_id TEXT NOT NULL UNIQUE,
    series_id TEXT NOT NULL,
    proposal_kind TEXT NOT NULL
        CHECK (proposal_kind IN ('create', 'revise', 'merge')),
    payload_json TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'pending'
        CHECK (status IN ('pending', 'accepted', 'rejected')),
    received_at TEXT NOT NULL,
    decided_at TEXT,
    accepted_article_id TEXT REFERENCES articles(id) ON DELETE SET NULL
);

CREATE INDEX idx_codex_proposal_history_series
    ON codex_proposal_history(series_id, history_id DESC);

CREATE INDEX idx_codex_proposal_history_status
    ON codex_proposal_history(status, history_id DESC);

INSERT INTO schema_migrations(version, applied_at)
VALUES (4, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));

COMMIT;
