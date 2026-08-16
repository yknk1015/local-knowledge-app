CREATE TABLE article_merge_relations (
    source_article_id TEXT PRIMARY KEY,
    target_article_id TEXT NOT NULL,
    source_updated_at TEXT NOT NULL,
    merged_at TEXT NOT NULL,
    CHECK (source_article_id <> target_article_id),
    FOREIGN KEY (source_article_id) REFERENCES articles(id) ON DELETE RESTRICT,
    FOREIGN KEY (target_article_id) REFERENCES articles(id) ON DELETE RESTRICT
);

CREATE INDEX idx_article_merge_relations_target
    ON article_merge_relations(target_article_id, merged_at);

INSERT INTO schema_migrations(version, applied_at)
VALUES (5, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
