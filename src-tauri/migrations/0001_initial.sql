BEGIN IMMEDIATE;

CREATE TABLE IF NOT EXISTS schema_migrations (
    version INTEGER PRIMARY KEY,
    applied_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS categories (
    id TEXT PRIMARY KEY,
    parent_id TEXT REFERENCES categories(id) ON DELETE RESTRICT,
    name TEXT NOT NULL CHECK (length(trim(name)) > 0),
    normalized_name TEXT NOT NULL,
    depth INTEGER NOT NULL CHECK (depth BETWEEN 1 AND 5),
    sort_order INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_categories_root_name
    ON categories(normalized_name) WHERE parent_id IS NULL;
CREATE UNIQUE INDEX IF NOT EXISTS uq_categories_parent_name
    ON categories(parent_id, normalized_name) WHERE parent_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_categories_parent_sort
    ON categories(parent_id, sort_order);

CREATE TABLE IF NOT EXISTS articles (
    id TEXT PRIMARY KEY,
    category_id TEXT NOT NULL REFERENCES categories(id) ON DELETE RESTRICT,
    title TEXT NOT NULL CHECK (length(trim(title)) > 0),
    normalized_title TEXT NOT NULL,
    summary TEXT NOT NULL DEFAULT '',
    body_doc_json TEXT NOT NULL,
    body_format_version INTEGER NOT NULL DEFAULT 1,
    body_plain_text TEXT NOT NULL DEFAULT '',
    procedure_text TEXT NOT NULL DEFAULT '',
    caution_text TEXT NOT NULL DEFAULT '',
    status TEXT NOT NULL CHECK (status IN ('draft', 'published', 'archived')),
    importance INTEGER NOT NULL DEFAULT 1 CHECK (importance BETWEEN 1 AND 3),
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    deleted_at TEXT
);

CREATE INDEX IF NOT EXISTS ix_articles_category ON articles(category_id);
CREATE INDEX IF NOT EXISTS ix_articles_status_updated ON articles(status, deleted_at, updated_at DESC);

CREATE TABLE IF NOT EXISTS article_symptoms (
    id TEXT PRIMARY KEY,
    article_id TEXT NOT NULL REFERENCES articles(id) ON DELETE CASCADE,
    value TEXT NOT NULL,
    normalized_value TEXT NOT NULL,
    sort_order INTEGER NOT NULL DEFAULT 0,
    UNIQUE(article_id, normalized_value)
);

CREATE TABLE IF NOT EXISTS article_causes (
    id TEXT PRIMARY KEY,
    article_id TEXT NOT NULL REFERENCES articles(id) ON DELETE CASCADE,
    value TEXT NOT NULL,
    normalized_value TEXT NOT NULL,
    sort_order INTEGER NOT NULL DEFAULT 0,
    UNIQUE(article_id, normalized_value)
);

CREATE TABLE IF NOT EXISTS article_targets (
    id TEXT PRIMARY KEY,
    article_id TEXT NOT NULL REFERENCES articles(id) ON DELETE CASCADE,
    value TEXT NOT NULL,
    normalized_value TEXT NOT NULL,
    sort_order INTEGER NOT NULL DEFAULT 0,
    UNIQUE(article_id, normalized_value)
);

CREATE TABLE IF NOT EXISTS article_error_codes (
    id TEXT PRIMARY KEY,
    article_id TEXT NOT NULL REFERENCES articles(id) ON DELETE CASCADE,
    value TEXT NOT NULL,
    normalized_value TEXT NOT NULL,
    sort_order INTEGER NOT NULL DEFAULT 0,
    UNIQUE(article_id, normalized_value)
);

CREATE TABLE IF NOT EXISTS article_search_terms (
    id TEXT PRIMARY KEY,
    article_id TEXT NOT NULL REFERENCES articles(id) ON DELETE CASCADE,
    value TEXT NOT NULL,
    normalized_value TEXT NOT NULL,
    sort_order INTEGER NOT NULL DEFAULT 0,
    UNIQUE(article_id, normalized_value)
);

CREATE TABLE IF NOT EXISTS tags (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    normalized_name TEXT NOT NULL UNIQUE,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS article_tags (
    article_id TEXT NOT NULL REFERENCES articles(id) ON DELETE CASCADE,
    tag_id TEXT NOT NULL REFERENCES tags(id) ON DELETE RESTRICT,
    PRIMARY KEY(article_id, tag_id)
);

CREATE TABLE IF NOT EXISTS article_relations (
    source_article_id TEXT NOT NULL REFERENCES articles(id) ON DELETE CASCADE,
    target_article_id TEXT NOT NULL REFERENCES articles(id) ON DELETE CASCADE,
    sort_order INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY(source_article_id, target_article_id),
    CHECK(source_article_id <> target_article_id)
);

CREATE TABLE IF NOT EXISTS article_attachments (
    id TEXT PRIMARY KEY,
    article_id TEXT NOT NULL REFERENCES articles(id) ON DELETE CASCADE,
    relative_path TEXT NOT NULL,
    original_name TEXT NOT NULL,
    media_type TEXT NOT NULL,
    byte_size INTEGER NOT NULL CHECK (byte_size >= 0),
    sha256 TEXT NOT NULL,
    alt_text TEXT NOT NULL DEFAULT '',
    created_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS manuals (
    id TEXT PRIMARY KEY,
    title TEXT NOT NULL,
    manual_dir TEXT NOT NULL UNIQUE,
    entry_path TEXT NOT NULL,
    last_checked_at TEXT,
    check_status TEXT NOT NULL DEFAULT 'unchecked',
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS article_manual_links (
    article_id TEXT NOT NULL REFERENCES articles(id) ON DELETE CASCADE,
    manual_id TEXT NOT NULL REFERENCES manuals(id) ON DELETE RESTRICT,
    display_name TEXT NOT NULL,
    sort_order INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY(article_id, manual_id)
);

CREATE TABLE IF NOT EXISTS synonym_groups (
    id TEXT PRIMARY KEY,
    display_name TEXT NOT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS synonyms (
    id TEXT PRIMARY KEY,
    group_id TEXT NOT NULL REFERENCES synonym_groups(id) ON DELETE CASCADE,
    term TEXT NOT NULL,
    normalized_term TEXT NOT NULL,
    UNIQUE(group_id, normalized_term)
);

CREATE TABLE IF NOT EXISTS article_search_documents (
    article_id TEXT PRIMARY KEY REFERENCES articles(id) ON DELETE CASCADE,
    title TEXT NOT NULL,
    summary TEXT NOT NULL,
    body TEXT NOT NULL,
    symptoms TEXT NOT NULL DEFAULT '',
    causes TEXT NOT NULL DEFAULT '',
    targets TEXT NOT NULL DEFAULT '',
    error_codes TEXT NOT NULL DEFAULT '',
    tags TEXT NOT NULL DEFAULT '',
    search_terms TEXT NOT NULL DEFAULT ''
);

CREATE VIRTUAL TABLE IF NOT EXISTS article_search_fts USING fts5(
    article_id UNINDEXED,
    title,
    summary,
    body,
    symptoms,
    causes,
    targets,
    error_codes,
    tags,
    search_terms,
    tokenize='trigram'
);

CREATE TABLE IF NOT EXISTS search_logs (
    id TEXT PRIMARY KEY,
    query_text TEXT NOT NULL,
    normalized_query TEXT NOT NULL,
    scope TEXT NOT NULL CHECK (scope IN ('descendants', 'current', 'all')),
    category_id TEXT REFERENCES categories(id) ON DELETE SET NULL,
    result_count INTEGER NOT NULL CHECK (result_count >= 0),
    created_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_search_logs_created ON search_logs(created_at DESC);
CREATE INDEX IF NOT EXISTS ix_search_logs_result_count ON search_logs(result_count, created_at DESC);

CREATE TABLE IF NOT EXISTS view_logs (
    id TEXT PRIMARY KEY,
    article_id TEXT NOT NULL REFERENCES articles(id) ON DELETE CASCADE,
    source_search_log_id TEXT REFERENCES search_logs(id) ON DELETE SET NULL,
    viewed_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_view_logs_article_viewed ON view_logs(article_id, viewed_at DESC);

CREATE TABLE IF NOT EXISTS app_settings (
    key TEXT PRIMARY KEY,
    value_json TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

INSERT OR IGNORE INTO schema_migrations(version, applied_at)
VALUES (1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));

COMMIT;
