use std::{path::Path, time::Duration};

use chrono::Utc;
use rusqlite::{Connection, OpenFlags, OptionalExtension, backup::Backup, params};
use serde_json::Value;
use unicode_normalization::UnicodeNormalization;
use uuid::Uuid;

use crate::{
    errors::{AppError, AppResult},
    models::{Article, ArticleListItem, BackupCounts, Category, SearchArticlesInput},
};

const INITIAL_MIGRATION: &str = include_str!("../../migrations/0001_initial.sql");

pub struct Database {
    connection: Connection,
}

pub struct ArticleRecord<'a> {
    pub id: Option<&'a str>,
    pub category_id: &'a str,
    pub title: &'a str,
    pub summary: &'a str,
    pub body_doc: &'a Value,
    pub body_plain_text: &'a str,
    pub status: &'a str,
    pub importance: i64,
}

impl Database {
    pub fn open(path: &Path) -> AppResult<Self> {
        let connection = Connection::open(path)
            .map_err(|_| AppError::database("FAQデータベースを開けませんでした。"))?;
        connection
            .busy_timeout(Duration::from_secs(5))
            .map_err(AppError::from)?;
        connection
            .execute_batch(
                "PRAGMA foreign_keys = ON;\nPRAGMA journal_mode = WAL;\nPRAGMA synchronous = NORMAL;",
            )
            .map_err(AppError::from)?;
        connection
            .execute_batch(INITIAL_MIGRATION)
            .map_err(|_| AppError::database("データベースの初期設定に失敗しました。"))?;
        Ok(Self { connection })
    }

    pub fn backup_to(&self, destination: &Path) -> AppResult<()> {
        let mut destination_connection = Connection::open(destination).map_err(|_| {
            AppError::database("バックアップ用データベースを作成できませんでした。")
        })?;
        let backup = Backup::new(&self.connection, &mut destination_connection).map_err(|_| {
            AppError::database("データベースのバックアップを開始できませんでした。")
        })?;
        backup
            .run_to_completion(128, Duration::from_millis(10), None)
            .map_err(|_| {
                AppError::database("データベースのバックアップを完了できませんでした。")
            })?;
        Ok(())
    }

    pub fn restore_from(&mut self, source: &Path) -> AppResult<()> {
        Self::validate_snapshot(source)?;
        let source_connection =
            Connection::open_with_flags(source, OpenFlags::SQLITE_OPEN_READ_ONLY)
                .map_err(|_| AppError::database("復元するデータベースを開けませんでした。"))?;
        let backup = Backup::new(&source_connection, &mut self.connection)
            .map_err(|_| AppError::database("データベースの復元を開始できませんでした。"))?;
        backup
            .run_to_completion(128, Duration::from_millis(10), None)
            .map_err(|_| AppError::database("データベースの復元を完了できませんでした。"))?;
        drop(backup);
        self.connection
            .execute_batch("PRAGMA foreign_keys = ON;\nPRAGMA journal_mode = WAL;\nPRAGMA synchronous = NORMAL;")
            .map_err(AppError::from)?;
        self.quick_check()
    }

    pub fn validate_snapshot(path: &Path) -> AppResult<()> {
        let connection = Connection::open_with_flags(path, OpenFlags::SQLITE_OPEN_READ_ONLY)
            .map_err(|_| {
                AppError::new(
                    "BK-006",
                    "バックアップ内のデータベースを開けませんでした。",
                    "別のバックアップファイルを選択してください。",
                )
            })?;
        let check: String = connection
            .query_row("PRAGMA quick_check", [], |row| row.get(0))
            .map_err(|_| {
                AppError::new(
                    "BK-006",
                    "バックアップ内のデータベースを検査できませんでした。",
                    "別のバックアップファイルを選択してください。",
                )
            })?;
        if check != "ok" {
            return Err(AppError::new(
                "BK-006",
                "バックアップ内のデータベースが破損しています。",
                "別のバックアップファイルを選択してください。",
            ));
        }
        let required_tables: i64 = connection
            .query_row(
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('schema_migrations', 'categories', 'articles')",
                [],
                |row| row.get(0),
            )
            .map_err(AppError::from)?;
        if required_tables != 3 {
            return Err(AppError::new(
                "BK-007",
                "このファイルはKnowledgeAppのバックアップではありません。",
                "拡張子が.faqbackupの正しいファイルを選択してください。",
            ));
        }
        Ok(())
    }

    pub fn schema_version(&self) -> AppResult<i64> {
        self.connection
            .query_row(
                "SELECT COALESCE(MAX(version), 0) FROM schema_migrations",
                [],
                |row| row.get(0),
            )
            .map_err(AppError::from)
    }

    pub fn backup_counts(&self) -> AppResult<BackupCounts> {
        self.connection
            .query_row(
                r#"
                SELECT
                    (SELECT COUNT(*) FROM articles WHERE deleted_at IS NULL),
                    (SELECT COUNT(*) FROM categories),
                    (SELECT COUNT(*) FROM article_attachments),
                    (SELECT COUNT(*) FROM manuals)
                "#,
                [],
                |row| {
                    Ok(BackupCounts {
                        articles: row.get(0)?,
                        categories: row.get(1)?,
                        attachments: row.get(2)?,
                        manuals: row.get(3)?,
                    })
                },
            )
            .map_err(AppError::from)
    }

    fn quick_check(&self) -> AppResult<()> {
        let check: String = self
            .connection
            .query_row("PRAGMA quick_check", [], |row| row.get(0))
            .map_err(AppError::from)?;
        if check == "ok" {
            Ok(())
        } else {
            Err(AppError::database(
                "データベースの整合性確認に失敗しました。",
            ))
        }
    }

    pub fn list_categories(&self) -> AppResult<Vec<Category>> {
        let mut statement = self.connection.prepare(
            r#"
            WITH RECURSIVE category_tree AS (
                SELECT id, parent_id, name, depth, sort_order,
                       printf('%08d', sort_order) AS sort_path
                  FROM categories
                 WHERE parent_id IS NULL
                UNION ALL
                SELECT child.id, child.parent_id, child.name, child.depth, child.sort_order,
                       category_tree.sort_path || '.' || printf('%08d', child.sort_order)
                  FROM categories child
                  JOIN category_tree ON child.parent_id = category_tree.id
            )
            SELECT tree.id, tree.parent_id, tree.name, tree.depth, tree.sort_order,
                   (SELECT COUNT(*) FROM articles a
                     WHERE a.category_id = tree.id AND a.deleted_at IS NULL) AS article_count
              FROM category_tree tree
             ORDER BY tree.sort_path, tree.name
            "#,
        )?;
        let rows = statement.query_map([], |row| {
            Ok(Category {
                id: row.get(0)?,
                parent_id: row.get(1)?,
                name: row.get(2)?,
                depth: row.get(3)?,
                sort_order: row.get(4)?,
                article_count: row.get(5)?,
            })
        })?;
        rows.collect::<Result<Vec<_>, _>>().map_err(AppError::from)
    }

    pub fn create_category(&mut self, name: &str, parent_id: Option<&str>) -> AppResult<Category> {
        let name = name.trim();
        if name.is_empty() || name.chars().count() > 100 {
            return Err(AppError::new(
                "CAT-001",
                "分類名は1～100文字で入力してください。",
                "分類名を確認して、もう一度作成してください。",
            ));
        }

        let transaction = self.connection.transaction()?;
        let depth = match parent_id {
            Some(parent_id) => transaction
                .query_row(
                    "SELECT depth + 1 FROM categories WHERE id = ?1",
                    [parent_id],
                    |row| row.get::<_, i64>(0),
                )
                .optional()?
                .ok_or_else(|| {
                    AppError::new(
                        "CAT-002",
                        "親となる分類が見つかりません。",
                        "分類一覧を更新して、もう一度選択してください。",
                    )
                })?,
            None => 1,
        };
        if depth > 5 {
            return Err(AppError::new(
                "CAT-003",
                "分類は5階層まで作成できます。",
                "より上の階層を親として選択してください。",
            ));
        }

        let normalized_name = normalize(name);
        let duplicate: bool = transaction.query_row(
            "SELECT EXISTS(SELECT 1 FROM categories WHERE parent_id IS ?1 AND normalized_name = ?2)",
            params![parent_id, normalized_name],
            |row| row.get(0),
        )?;
        if duplicate {
            return Err(AppError::new(
                "CAT-004",
                "同じ場所に同名の分類があります。",
                "別の分類名を入力してください。",
            ));
        }

        let sort_order: i64 = transaction.query_row(
            "SELECT COALESCE(MAX(sort_order), -1) + 1 FROM categories WHERE parent_id IS ?1",
            [parent_id],
            |row| row.get(0),
        )?;
        let id = Uuid::now_v7().to_string();
        let now = Utc::now().to_rfc3339();
        transaction.execute(
            "INSERT INTO categories(id, parent_id, name, normalized_name, depth, sort_order, created_at, updated_at) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?7)",
            params![id, parent_id, name, normalized_name, depth, sort_order, now],
        )?;
        transaction.commit()?;

        Ok(Category {
            id,
            parent_id: parent_id.map(str::to_owned),
            name: name.to_owned(),
            depth,
            sort_order,
            article_count: 0,
        })
    }

    pub fn save_article(&mut self, article: ArticleRecord<'_>) -> AppResult<Article> {
        let transaction = self.connection.transaction()?;
        let category_exists: bool = transaction.query_row(
            "SELECT EXISTS(SELECT 1 FROM categories WHERE id = ?1)",
            [article.category_id],
            |row| row.get(0),
        )?;
        if !category_exists {
            return Err(AppError::new(
                "ART-002",
                "選択した分類が見つかりません。",
                "分類を選び直して、もう一度保存してください。",
            ));
        }

        let id = article
            .id
            .map(str::to_owned)
            .unwrap_or_else(|| Uuid::now_v7().to_string());
        let now = Utc::now().to_rfc3339();
        let body_doc_json = serde_json::to_string(article.body_doc).map_err(|_| {
            AppError::new(
                "ART-003",
                "回答の保存形式を確認できませんでした。",
                "回答を入力し直して、もう一度保存してください。",
            )
        })?;

        if article.id.is_some() {
            let updated = transaction.execute(
                r#"
                UPDATE articles
                   SET category_id = ?2, title = ?3, normalized_title = ?4, summary = ?5,
                       body_doc_json = ?6, body_plain_text = ?7, status = ?8,
                       importance = ?9, updated_at = ?10
                 WHERE id = ?1 AND deleted_at IS NULL
                "#,
                params![
                    id,
                    article.category_id,
                    article.title,
                    normalize(article.title),
                    article.summary,
                    body_doc_json,
                    article.body_plain_text,
                    article.status,
                    article.importance,
                    now
                ],
            )?;
            if updated == 0 {
                return Err(article_not_found());
            }
        } else {
            transaction.execute(
                r#"
                INSERT INTO articles(
                    id, category_id, title, normalized_title, summary, body_doc_json,
                    body_format_version, body_plain_text, status, importance, created_at, updated_at
                ) VALUES (?1, ?2, ?3, ?4, ?5, ?6, 1, ?7, ?8, ?9, ?10, ?10)
                "#,
                params![
                    id,
                    article.category_id,
                    article.title,
                    normalize(article.title),
                    article.summary,
                    body_doc_json,
                    article.body_plain_text,
                    article.status,
                    article.importance,
                    now
                ],
            )?;
        }

        transaction.execute(
            r#"
            INSERT INTO article_search_documents(article_id, title, summary, body)
            VALUES (?1, ?2, ?3, ?4)
            ON CONFLICT(article_id) DO UPDATE SET
                title = excluded.title, summary = excluded.summary, body = excluded.body
            "#,
            params![
                id,
                normalize(article.title),
                normalize(article.summary),
                normalize(article.body_plain_text)
            ],
        )?;
        transaction.execute(
            "DELETE FROM article_search_fts WHERE article_id = ?1",
            [&id],
        )?;
        transaction.execute(
            "INSERT INTO article_search_fts(article_id, title, summary, body) VALUES (?1, ?2, ?3, ?4)",
            params![id, normalize(article.title), normalize(article.summary), normalize(article.body_plain_text)],
        )?;
        transaction.commit()?;
        self.get_article(&id)
    }

    pub fn get_article(&self, id: &str) -> AppResult<Article> {
        self.connection
            .query_row(
                r#"
                SELECT a.id, a.category_id, c.name, a.title, a.summary, a.body_doc_json,
                       a.body_plain_text, a.status, a.importance, a.created_at, a.updated_at
                  FROM articles a
                  JOIN categories c ON c.id = a.category_id
                 WHERE a.id = ?1 AND a.deleted_at IS NULL
                "#,
                [id],
                article_from_row,
            )
            .optional()?
            .ok_or_else(article_not_found)
    }

    pub fn search_articles(&self, input: &SearchArticlesInput) -> AppResult<Vec<ArticleListItem>> {
        let normalized_query = normalize(input.query.trim());
        let like_query = format!("%{}%", escape_like(&normalized_query));
        let category_id = input.category_id.as_deref();
        let mut statement = self.connection.prepare(
            r#"
            WITH RECURSIVE selected_categories(id) AS (
                SELECT id FROM categories WHERE id = ?2
                UNION ALL
                SELECT c.id FROM categories c
                JOIN selected_categories parent ON c.parent_id = parent.id
            )
            SELECT a.id, a.category_id, c.name, a.title, a.summary,
                   a.status, a.importance, a.updated_at
              FROM articles a
              JOIN categories c ON c.id = a.category_id
              JOIN article_search_documents search_doc ON search_doc.article_id = a.id
             WHERE a.deleted_at IS NULL
               AND (?3 = 1 OR a.status = 'published')
               AND (?2 IS NULL OR a.category_id IN (SELECT id FROM selected_categories))
               AND (?1 = '' OR search_doc.title LIKE ?4 ESCAPE '\'
                    OR search_doc.summary LIKE ?4 ESCAPE '\'
                    OR search_doc.body LIKE ?4 ESCAPE '\')
             ORDER BY a.importance DESC, a.updated_at DESC
             LIMIT 500
            "#,
        )?;
        let rows = statement.query_map(
            params![
                normalized_query,
                category_id,
                input.include_drafts,
                like_query
            ],
            |row| {
                Ok(ArticleListItem {
                    id: row.get(0)?,
                    category_id: row.get(1)?,
                    category_name: row.get(2)?,
                    title: row.get(3)?,
                    summary: row.get(4)?,
                    status: row.get(5)?,
                    importance: row.get(6)?,
                    updated_at: row.get(7)?,
                })
            },
        )?;
        rows.collect::<Result<Vec<_>, _>>().map_err(AppError::from)
    }
}

fn article_from_row(row: &rusqlite::Row<'_>) -> rusqlite::Result<Article> {
    let body_doc_json: String = row.get(5)?;
    let body_doc = serde_json::from_str(&body_doc_json).map_err(|error| {
        rusqlite::Error::FromSqlConversionFailure(
            body_doc_json.len(),
            rusqlite::types::Type::Text,
            Box::new(error),
        )
    })?;
    Ok(Article {
        id: row.get(0)?,
        category_id: row.get(1)?,
        category_name: row.get(2)?,
        title: row.get(3)?,
        summary: row.get(4)?,
        body_doc,
        body_plain_text: row.get(6)?,
        status: row.get(7)?,
        importance: row.get(8)?,
        created_at: row.get(9)?,
        updated_at: row.get(10)?,
    })
}

pub fn normalize(value: &str) -> String {
    value
        .nfkc()
        .flat_map(char::to_lowercase)
        .collect::<String>()
        .split_whitespace()
        .collect::<Vec<_>>()
        .join(" ")
}

fn escape_like(value: &str) -> String {
    value
        .replace('\\', "\\\\")
        .replace('%', "\\%")
        .replace('_', "\\_")
}

fn article_not_found() -> AppError {
    AppError::new(
        "ART-004",
        "指定したFAQが見つかりません。",
        "一覧を更新して、FAQを選び直してください。",
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn temporary_database() -> (tempfile::TempDir, Database) {
        let directory = tempfile::tempdir().unwrap();
        let database = Database::open(&directory.path().join("knowledge.db")).unwrap();
        (directory, database)
    }

    #[test]
    fn category_depth_is_limited_to_five() {
        let (_directory, mut database) = temporary_database();
        let mut parent = None;
        for depth in 1..=5 {
            let category = database
                .create_category(&format!("階層{depth}"), parent.as_deref())
                .unwrap();
            assert_eq!(category.depth, depth);
            parent = Some(category.id);
        }

        let error = database
            .create_category("階層6", parent.as_deref())
            .unwrap_err();
        assert_eq!(error.code, "CAT-003");
    }

    #[test]
    fn article_is_persisted_and_public_search_excludes_drafts() {
        let (directory, mut database) = temporary_database();
        let category = database.create_category("Windows", None).unwrap();
        let body = json!({"type": "doc", "content": [{"type": "paragraph", "content": [{"type": "text", "text": "再起動します"}]}]});

        let draft = database
            .save_article(ArticleRecord {
                id: None,
                category_id: &category.id,
                title: "画面が真っ暗",
                summary: "Windowsの画面を確認します",
                body_doc: &body,
                body_plain_text: "再起動します",
                status: "draft",
                importance: 1,
            })
            .unwrap();
        drop(database);

        let database = Database::open(&directory.path().join("knowledge.db")).unwrap();
        assert_eq!(
            database.get_article(&draft.id).unwrap().title,
            "画面が真っ暗"
        );
        let public_results = database
            .search_articles(&SearchArticlesInput {
                query: "画面".into(),
                category_id: None,
                include_drafts: false,
            })
            .unwrap();
        assert!(public_results.is_empty());
        let management_results = database
            .search_articles(&SearchArticlesInput {
                query: "画面".into(),
                category_id: None,
                include_drafts: true,
            })
            .unwrap();
        assert_eq!(management_results.len(), 1);
    }

    #[test]
    fn normalizes_width_case_and_spaces() {
        assert_eq!(normalize("  ＰＣ  Setup "), "pc setup");
    }
}
