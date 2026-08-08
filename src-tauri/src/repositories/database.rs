use std::{path::Path, time::Duration};

use chrono::Utc;
use rusqlite::{Connection, OpenFlags, OptionalExtension, backup::Backup, params};
use serde_json::Value;
use unicode_normalization::UnicodeNormalization;
use uuid::Uuid;

use crate::{
    errors::{AppError, AppResult},
    models::{
        Article, ArticleAttachment, ArticleListItem, BackupCounts, Category,
        ManagementArticleListItem, ManagementArticlePage, ManagementArticlesInput,
        SearchArticlesInput,
    },
};

const INITIAL_MIGRATION: &str = include_str!("../../migrations/0001_initial.sql");

pub struct Database {
    connection: Connection,
}

pub struct ArticleRecord<'a> {
    pub id: &'a str,
    pub is_new: bool,
    pub category_id: &'a str,
    pub title: &'a str,
    pub summary: &'a str,
    pub body_doc: &'a Value,
    pub body_plain_text: &'a str,
    pub status: &'a str,
    pub importance: i64,
    pub attachments: &'a [AttachmentRecord],
}

#[derive(Debug, Clone)]
pub struct AttachmentRecord {
    pub id: String,
    pub relative_path: String,
    pub original_name: String,
    pub media_type: String,
    pub byte_size: i64,
    pub sha256: String,
    pub alt_text: String,
    pub created_at: String,
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
        let name = validate_category_name(name)?;

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

    pub fn update_category(
        &mut self,
        id: &str,
        name: &str,
        parent_id: Option<&str>,
    ) -> AppResult<Category> {
        let name = validate_category_name(name)?;
        let transaction = self.connection.transaction()?;
        let current = transaction
            .query_row(
                "SELECT parent_id, depth FROM categories WHERE id = ?1",
                [id],
                |row| Ok((row.get::<_, Option<String>>(0)?, row.get::<_, i64>(1)?)),
            )
            .optional()?
            .ok_or_else(category_not_found)?;

        if parent_id == Some(id) {
            return Err(category_cycle_error());
        }
        if let Some(parent_id) = parent_id {
            let parent_is_descendant: bool = transaction.query_row(
                r#"
                WITH RECURSIVE descendants(id) AS (
                    SELECT id FROM categories WHERE parent_id = ?1
                    UNION ALL
                    SELECT child.id FROM categories child
                    JOIN descendants parent ON child.parent_id = parent.id
                )
                SELECT EXISTS(SELECT 1 FROM descendants WHERE id = ?2)
                "#,
                params![id, parent_id],
                |row| row.get(0),
            )?;
            if parent_is_descendant {
                return Err(category_cycle_error());
            }
        }

        let target_depth = match parent_id {
            Some(parent_id) => transaction
                .query_row(
                    "SELECT depth + 1 FROM categories WHERE id = ?1",
                    [parent_id],
                    |row| row.get::<_, i64>(0),
                )
                .optional()?
                .ok_or_else(category_not_found)?,
            None => 1,
        };
        let subtree_relative_depth: i64 = transaction.query_row(
            r#"
            WITH RECURSIVE descendants(id, depth) AS (
                SELECT id, depth FROM categories WHERE id = ?1
                UNION ALL
                SELECT child.id, child.depth FROM categories child
                JOIN descendants parent ON child.parent_id = parent.id
            )
            SELECT COALESCE(MAX(depth), ?2) - ?2 FROM descendants
            "#,
            params![id, current.1],
            |row| row.get(0),
        )?;
        if target_depth + subtree_relative_depth > 5 {
            return Err(AppError::new(
                "CAT-003",
                "移動すると分類が6階層以上になります。",
                "より上の階層を移動先として選択してください。",
            ));
        }

        let normalized_name = normalize(name);
        let duplicate: bool = transaction.query_row(
            "SELECT EXISTS(SELECT 1 FROM categories WHERE id <> ?1 AND parent_id IS ?2 AND normalized_name = ?3)",
            params![id, parent_id, normalized_name],
            |row| row.get(0),
        )?;
        if duplicate {
            return Err(AppError::new(
                "CAT-004",
                "移動先に同名の分類があります。",
                "分類名または移動先を変更してください。",
            ));
        }

        let parent_changed = current.0.as_deref() != parent_id;
        let sort_order = if parent_changed {
            transaction.query_row(
                "SELECT COALESCE(MAX(sort_order), -1) + 1 FROM categories WHERE parent_id IS ?1 AND id <> ?2",
                params![parent_id, id],
                |row| row.get::<_, i64>(0),
            )?
        } else {
            transaction.query_row(
                "SELECT sort_order FROM categories WHERE id = ?1",
                [id],
                |row| row.get::<_, i64>(0),
            )?
        };
        let depth_delta = target_depth - current.1;
        let now = Utc::now().to_rfc3339();
        transaction.execute(
            r#"
            UPDATE categories
               SET parent_id = ?2, name = ?3, normalized_name = ?4,
                   depth = ?5, sort_order = ?6, updated_at = ?7
             WHERE id = ?1
            "#,
            params![
                id,
                parent_id,
                name,
                normalized_name,
                target_depth,
                sort_order,
                now
            ],
        )?;
        if depth_delta != 0 {
            transaction.execute(
                r#"
                WITH RECURSIVE descendants(id) AS (
                    SELECT id FROM categories WHERE parent_id = ?1
                    UNION ALL
                    SELECT child.id FROM categories child
                    JOIN descendants parent ON child.parent_id = parent.id
                )
                UPDATE categories SET depth = depth + ?2, updated_at = ?3
                 WHERE id IN (SELECT id FROM descendants)
                "#,
                params![id, depth_delta, now],
            )?;
        }
        transaction.commit()?;
        self.list_categories()?
            .into_iter()
            .find(|category| category.id == id)
            .ok_or_else(category_not_found)
    }

    pub fn delete_category(&mut self, id: &str) -> AppResult<()> {
        let transaction = self.connection.transaction()?;
        let exists: bool = transaction.query_row(
            "SELECT EXISTS(SELECT 1 FROM categories WHERE id = ?1)",
            [id],
            |row| row.get(0),
        )?;
        if !exists {
            return Err(category_not_found());
        }
        let child_count: i64 = transaction.query_row(
            "SELECT COUNT(*) FROM categories WHERE parent_id = ?1",
            [id],
            |row| row.get(0),
        )?;
        let article_count: i64 = transaction.query_row(
            "SELECT COUNT(*) FROM articles WHERE category_id = ?1",
            [id],
            |row| row.get(0),
        )?;
        if child_count > 0 || article_count > 0 {
            return Err(AppError::new(
                "CAT-005",
                "配下分類またはFAQが残っているため、この分類は削除できません。",
                "配下分類とFAQを別の分類へ移動するか、先に削除してください。",
            ));
        }
        transaction.execute("DELETE FROM categories WHERE id = ?1", [id])?;
        transaction.commit()?;
        Ok(())
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

        let id = article.id.to_owned();
        let now = Utc::now().to_rfc3339();
        let body_doc_json = serde_json::to_string(article.body_doc).map_err(|_| {
            AppError::new(
                "ART-003",
                "回答の保存形式を確認できませんでした。",
                "回答を入力し直して、もう一度保存してください。",
            )
        })?;

        if !article.is_new {
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
            "DELETE FROM article_attachments WHERE article_id = ?1",
            [&id],
        )?;
        for attachment in article.attachments {
            transaction.execute(
                r#"
                INSERT INTO article_attachments(
                    id, article_id, relative_path, original_name, media_type,
                    byte_size, sha256, alt_text, created_at
                ) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9)
                "#,
                params![
                    attachment.id,
                    id,
                    attachment.relative_path,
                    attachment.original_name,
                    attachment.media_type,
                    attachment.byte_size,
                    attachment.sha256,
                    attachment.alt_text,
                    attachment.created_at,
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
        let mut article = self
            .connection
            .query_row(
                r#"
                SELECT a.id, a.category_id, c.name, a.title, a.summary, a.body_doc_json,
                       a.body_plain_text, a.status, a.importance, a.created_at, a.updated_at,
                       a.deleted_at
                  FROM articles a
                  JOIN categories c ON c.id = a.category_id
                 WHERE a.id = ?1
                "#,
                [id],
                article_from_row,
            )
            .optional()?
            .ok_or_else(article_not_found)?;
        article.attachments = self.list_article_attachments(id)?;
        Ok(article)
    }

    pub fn list_article_attachments(&self, article_id: &str) -> AppResult<Vec<ArticleAttachment>> {
        let mut statement = self.connection.prepare(
            r#"
            SELECT id, original_name, media_type, byte_size, sha256, alt_text, relative_path, created_at
              FROM article_attachments
             WHERE article_id = ?1
             ORDER BY created_at, id
            "#,
        )?;
        let rows = statement.query_map([article_id], |row| {
            Ok(ArticleAttachment {
                id: row.get(0)?,
                original_name: row.get(1)?,
                media_type: row.get(2)?,
                byte_size: row.get(3)?,
                sha256: row.get(4)?,
                alt_text: row.get(5)?,
                asset_path: row.get(6)?,
                created_at: row.get(7)?,
            })
        })?;
        rows.collect::<Result<Vec<_>, _>>().map_err(AppError::from)
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

    pub fn list_articles_for_management(
        &self,
        input: &ManagementArticlesInput,
    ) -> AppResult<ManagementArticlePage> {
        const PAGE_SIZE: i64 = 50;
        let page = input.page.max(1);
        let offset = (page - 1) * PAGE_SIZE;
        let normalized_query = normalize(input.query.trim());
        let like_query = format!("%{}%", escape_like(&normalized_query));
        let category_id = input.category_id.as_deref();
        let status = input.status.as_deref();
        let mut statement = self.connection.prepare(
            r#"
            WITH RECURSIVE selected_categories(id) AS (
                SELECT id FROM categories WHERE id = ?2
                UNION ALL
                SELECT c.id FROM categories c
                JOIN selected_categories parent ON c.parent_id = parent.id
            ), filtered AS (
                SELECT a.id, a.category_id, c.name AS category_name, a.title, a.summary,
                       a.status, a.importance, a.updated_at, a.deleted_at
                  FROM articles a
                  JOIN categories c ON c.id = a.category_id
                  JOIN article_search_documents search_doc ON search_doc.article_id = a.id
                 WHERE ((?4 = 1 AND a.deleted_at IS NOT NULL) OR (?4 = 0 AND a.deleted_at IS NULL))
                   AND (?2 IS NULL OR a.category_id IN (SELECT id FROM selected_categories))
                   AND (?3 IS NULL OR a.status = ?3)
                   AND (?1 = '' OR search_doc.title LIKE ?5 ESCAPE '\'
                        OR search_doc.summary LIKE ?5 ESCAPE '\'
                        OR search_doc.body LIKE ?5 ESCAPE '\')
            )
            SELECT id, category_id, category_name, title, summary, status, importance,
                   updated_at, deleted_at, COUNT(*) OVER()
              FROM filtered
             ORDER BY updated_at DESC
             LIMIT ?6 OFFSET ?7
            "#,
        )?;
        let rows = statement.query_map(
            params![
                normalized_query,
                category_id,
                status,
                input.deleted,
                like_query,
                PAGE_SIZE,
                offset,
            ],
            |row| {
                Ok((
                    ManagementArticleListItem {
                        id: row.get(0)?,
                        category_id: row.get(1)?,
                        category_name: row.get(2)?,
                        title: row.get(3)?,
                        summary: row.get(4)?,
                        status: row.get(5)?,
                        importance: row.get(6)?,
                        updated_at: row.get(7)?,
                        deleted_at: row.get(8)?,
                    },
                    row.get::<_, i64>(9)?,
                ))
            },
        )?;
        let results = rows.collect::<Result<Vec<_>, _>>()?;
        let total = results.first().map(|(_, total)| *total).unwrap_or(0);
        Ok(ManagementArticlePage {
            items: results.into_iter().map(|(article, _)| article).collect(),
            total,
            page,
            page_size: PAGE_SIZE,
        })
    }

    pub fn delete_article(&mut self, id: &str) -> AppResult<Article> {
        let transaction = self.connection.transaction()?;
        let now = Utc::now().to_rfc3339();
        let updated = transaction.execute(
            "UPDATE articles SET deleted_at = ?2, updated_at = ?2 WHERE id = ?1 AND deleted_at IS NULL",
            params![id, now],
        )?;
        if updated == 0 {
            return Err(article_not_found());
        }
        transaction.execute("DELETE FROM article_search_fts WHERE article_id = ?1", [id])?;
        transaction.commit()?;
        self.get_article(id)
    }

    pub fn restore_article(&mut self, id: &str) -> AppResult<Article> {
        let transaction = self.connection.transaction()?;
        let now = Utc::now().to_rfc3339();
        let updated = transaction.execute(
            "UPDATE articles SET deleted_at = NULL, updated_at = ?2 WHERE id = ?1 AND deleted_at IS NOT NULL",
            params![id, now],
        )?;
        if updated == 0 {
            return Err(AppError::new(
                "ART-005",
                "復元できる削除済みFAQが見つかりません。",
                "FAQ管理画面を更新して、削除済みFAQを選び直してください。",
            ));
        }
        transaction.execute("DELETE FROM article_search_fts WHERE article_id = ?1", [id])?;
        transaction.execute(
            r#"
            INSERT INTO article_search_fts(
                article_id, title, summary, body, symptoms, causes, targets,
                error_codes, tags, search_terms
            )
            SELECT article_id, title, summary, body, symptoms, causes, targets,
                   error_codes, tags, search_terms
              FROM article_search_documents
             WHERE article_id = ?1
            "#,
            [id],
        )?;
        transaction.commit()?;
        self.get_article(id)
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
        deleted_at: row.get(11)?,
        attachments: Vec::new(),
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

fn validate_category_name(name: &str) -> AppResult<&str> {
    let name = name.trim();
    if name.is_empty() || name.chars().count() > 100 {
        return Err(AppError::new(
            "CAT-001",
            "分類名は1～100文字で入力してください。",
            "分類名を確認して、もう一度保存してください。",
        ));
    }
    Ok(name)
}

fn category_not_found() -> AppError {
    AppError::new(
        "CAT-002",
        "指定した分類が見つかりません。",
        "分類一覧を更新して、選び直してください。",
    )
}

fn category_cycle_error() -> AppError {
    AppError::new(
        "CAT-006",
        "分類を自分自身または配下分類の下へ移動できません。",
        "別の分類を移動先として選択してください。",
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
    fn category_move_rejects_cycles_and_sixth_level() {
        let (_directory, mut database) = temporary_database();
        let root = database.create_category("移動元", None).unwrap();
        let child = database
            .create_category("移動元の子", Some(&root.id))
            .unwrap();
        let cycle_error = database
            .update_category(&root.id, &root.name, Some(&child.id))
            .unwrap_err();
        assert_eq!(cycle_error.code, "CAT-006");

        let target1 = database.create_category("対象1", None).unwrap();
        let target2 = database
            .create_category("対象2", Some(&target1.id))
            .unwrap();
        let target3 = database
            .create_category("対象3", Some(&target2.id))
            .unwrap();
        let target4 = database
            .create_category("対象4", Some(&target3.id))
            .unwrap();
        let depth_error = database
            .update_category(&root.id, &root.name, Some(&target4.id))
            .unwrap_err();
        assert_eq!(depth_error.code, "CAT-003");
    }

    #[test]
    fn category_can_be_renamed_moved_and_deleted_only_when_empty() {
        let (_directory, mut database) = temporary_database();
        let root_a = database.create_category("A", None).unwrap();
        let root_b = database.create_category("B", None).unwrap();
        let child = database
            .create_category("変更前", Some(&root_a.id))
            .unwrap();

        let updated = database
            .update_category(&child.id, "変更後", Some(&root_b.id))
            .unwrap();
        assert_eq!(updated.name, "変更後");
        assert_eq!(updated.parent_id.as_deref(), Some(root_b.id.as_str()));
        assert_eq!(updated.depth, 2);

        let nonempty_error = database.delete_category(&root_b.id).unwrap_err();
        assert_eq!(nonempty_error.code, "CAT-005");
        database.delete_category(&child.id).unwrap();
        assert!(
            database
                .list_categories()
                .unwrap()
                .iter()
                .all(|category| category.id != child.id)
        );
    }

    #[test]
    fn article_is_persisted_and_public_search_excludes_drafts() {
        let (directory, mut database) = temporary_database();
        let category = database.create_category("Windows", None).unwrap();
        let body = json!({"type": "doc", "content": [{"type": "paragraph", "content": [{"type": "text", "text": "再起動します"}]}]});
        let article_id = Uuid::now_v7().to_string();

        let draft = database
            .save_article(ArticleRecord {
                id: &article_id,
                is_new: true,
                category_id: &category.id,
                title: "画面が真っ暗",
                summary: "Windowsの画面を確認します",
                body_doc: &body,
                body_plain_text: "再起動します",
                status: "draft",
                importance: 1,
                attachments: &[],
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

    #[test]
    fn logically_deletes_and_restores_an_article() {
        let (_directory, mut database) = temporary_database();
        let category = database.create_category("PC", None).unwrap();
        let body = json!({"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"確認します"}]}]});
        let article_id = Uuid::now_v7().to_string();
        let article = database
            .save_article(ArticleRecord {
                id: &article_id,
                is_new: true,
                category_id: &category.id,
                title: "ネットワーク確認",
                summary: "接続を確認します",
                body_doc: &body,
                body_plain_text: "確認します",
                status: "published",
                importance: 1,
                attachments: &[],
            })
            .unwrap();

        let deleted = database.delete_article(&article.id).unwrap();
        assert!(deleted.deleted_at.is_some());
        assert!(
            database
                .search_articles(&SearchArticlesInput {
                    query: "ネットワーク".into(),
                    category_id: None,
                    include_drafts: true,
                })
                .unwrap()
                .is_empty()
        );
        let deleted_page = database
            .list_articles_for_management(&ManagementArticlesInput {
                query: "ネットワーク".into(),
                category_id: None,
                status: None,
                deleted: true,
                page: 1,
            })
            .unwrap();
        assert_eq!(deleted_page.total, 1);

        let restored = database.restore_article(&article.id).unwrap();
        assert!(restored.deleted_at.is_none());
        assert_eq!(
            database
                .search_articles(&SearchArticlesInput {
                    query: "ネットワーク".into(),
                    category_id: None,
                    include_drafts: false,
                })
                .unwrap()
                .len(),
            1
        );
    }
}
