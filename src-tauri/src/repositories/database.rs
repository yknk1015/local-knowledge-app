use std::{path::Path, time::Duration};

use chrono::Utc;
use rusqlite::{Connection, OpenFlags, OptionalExtension, Transaction, backup::Backup, params};
use serde_json::Value;
use unicode_normalization::UnicodeNormalization;
use uuid::Uuid;

use crate::{
    errors::{AppError, AppResult},
    models::{
        AppSettings, Article, ArticleAttachment, ArticleListItem, BackupCounts, Category,
        CodexFaqProposal, CodexProposalHistoryItem, CodexProposalKind, CodexSourceArticle,
        ManagementArticleListItem, ManagementArticlePage, ManagementArticlesInput,
        SearchArticlesInput,
    },
};

const INITIAL_MIGRATION: &str = include_str!("../../migrations/0001_initial.sql");
const ARTICLE_DISPLAY_FLAGS_MIGRATION: &str =
    include_str!("../../migrations/0002_article_display_flags.sql");
const CODEX_PROPOSALS_MIGRATION: &str = include_str!("../../migrations/0003_codex_proposals.sql");
const CODEX_DELEGATION_HISTORY_MIGRATION: &str =
    include_str!("../../migrations/0004_codex_delegation_history.sql");
const CURRENT_SCHEMA_VERSION: i64 = 4;
const APPEARANCE_SETTINGS_KEY: &str = "appearance";

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
    pub new_badge_until: Option<&'a str>,
    pub updated_badge_until: Option<&'a str>,
    pub is_hidden: bool,
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

pub struct NewCategoryRecord<'a> {
    pub id: &'a str,
    pub name: &'a str,
    pub description: &'a str,
    pub parent_id: Option<&'a str>,
}

pub struct CodexProposalArticleRecord<'a> {
    pub request_id: &'a str,
    pub proposal_kind: CodexProposalKind,
    pub source_articles: &'a [CodexSourceArticle],
    pub article_id: &'a str,
    pub category_id: &'a str,
    pub new_category: Option<NewCategoryRecord<'a>>,
    pub title: &'a str,
    pub summary: &'a str,
    pub body_doc: &'a Value,
    pub body_plain_text: &'a str,
    pub importance: i64,
}

pub struct CodexProposalRevisionRecord<'a> {
    pub request_id: &'a str,
    pub source_article: &'a CodexSourceArticle,
    pub title: &'a str,
    pub summary: &'a str,
    pub body_doc: &'a Value,
    pub body_plain_text: &'a str,
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
        let database = Self { connection };
        database.apply_migrations()?;
        Ok(database)
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
        self.apply_migrations()?;
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

    fn apply_migrations(&self) -> AppResult<()> {
        let version = self.schema_version()?;
        if version > CURRENT_SCHEMA_VERSION {
            return Err(AppError::database(
                "このFAQデータは、現在のアプリより新しい形式です。アプリを更新してください。",
            ));
        }
        if version < 2 {
            self.connection
                .execute_batch(ARTICLE_DISPLAY_FLAGS_MIGRATION)
                .map_err(|_| AppError::database("FAQデータの更新に失敗しました。"))?;
        }
        if version < 3 {
            self.connection
                .execute_batch(CODEX_PROPOSALS_MIGRATION)
                .map_err(|_| AppError::database("Codex提案用データの更新に失敗しました。"))?;
        }
        if version < 4 {
            self.connection
                .execute_batch(CODEX_DELEGATION_HISTORY_MIGRATION)
                .map_err(|_| AppError::database("Codex委譲・履歴用データの更新に失敗しました。"))?;
        }
        Ok(())
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

    pub fn get_settings(&self) -> AppResult<AppSettings> {
        let stored: Option<String> = self
            .connection
            .query_row(
                "SELECT value_json FROM app_settings WHERE key = ?1",
                [APPEARANCE_SETTINGS_KEY],
                |row| row.get(0),
            )
            .optional()?;

        let Some(stored) = stored else {
            return Ok(AppSettings::default());
        };

        serde_json::from_str(&stored).map_err(|_| {
            AppError::new(
                "SET-001",
                "画面の表示設定を読み込めませんでした。",
                "設定画面で表示設定を選び直して保存してください。",
            )
        })
    }

    pub fn save_settings(&self, settings: &AppSettings) -> AppResult<()> {
        let value_json = serde_json::to_string(settings).map_err(|_| {
            AppError::new(
                "SET-002",
                "画面の表示設定を保存できませんでした。",
                "設定内容を確認して、もう一度保存してください。",
            )
        })?;
        self.connection.execute(
            r#"
            INSERT INTO app_settings(key, value_json, updated_at)
            VALUES (?1, ?2, ?3)
            ON CONFLICT(key) DO UPDATE SET
                value_json = excluded.value_json,
                updated_at = excluded.updated_at
            "#,
            params![APPEARANCE_SETTINGS_KEY, value_json, Utc::now().to_rfc3339()],
        )?;
        Ok(())
    }

    pub fn list_categories(&self) -> AppResult<Vec<Category>> {
        let mut statement = self.connection.prepare(
            r#"
            WITH RECURSIVE category_tree AS (
                SELECT id, parent_id, name, description, depth, sort_order,
                       printf('%08d', sort_order) AS sort_path
                  FROM categories
                 WHERE parent_id IS NULL
                UNION ALL
                SELECT child.id, child.parent_id, child.name, child.description, child.depth, child.sort_order,
                       category_tree.sort_path || '.' || printf('%08d', child.sort_order)
                  FROM categories child
                  JOIN category_tree ON child.parent_id = category_tree.id
            )
            SELECT tree.id, tree.parent_id, tree.name, tree.description, tree.depth, tree.sort_order,
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
                description: row.get(3)?,
                depth: row.get(4)?,
                sort_order: row.get(5)?,
                article_count: row.get(6)?,
            })
        })?;
        rows.collect::<Result<Vec<_>, _>>().map_err(AppError::from)
    }

    #[cfg(test)]
    pub fn create_category(&mut self, name: &str, parent_id: Option<&str>) -> AppResult<Category> {
        self.create_category_with_description(name, "", parent_id)
    }

    pub fn create_category_with_description(
        &mut self,
        name: &str,
        description: &str,
        parent_id: Option<&str>,
    ) -> AppResult<Category> {
        let name = validate_category_name(name)?;
        let description = validate_category_description(description)?;

        let transaction = self.connection.transaction()?;
        let id = Uuid::now_v7().to_string();
        let category = insert_category(&transaction, &id, name, description, parent_id)?;
        transaction.commit()?;
        Ok(category)
    }

    #[cfg(test)]
    pub fn update_category(
        &mut self,
        id: &str,
        name: &str,
        parent_id: Option<&str>,
    ) -> AppResult<Category> {
        self.update_category_with_description(id, name, "", parent_id)
    }

    pub fn update_category_with_description(
        &mut self,
        id: &str,
        name: &str,
        description: &str,
        parent_id: Option<&str>,
    ) -> AppResult<Category> {
        let name = validate_category_name(name)?;
        let description = validate_category_description(description)?;
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
                   description = ?5, depth = ?6, sort_order = ?7, updated_at = ?8
             WHERE id = ?1
            "#,
            params![
                id,
                parent_id,
                name,
                normalized_name,
                description,
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
                       body_doc_json = ?6, body_format_version = 2, body_plain_text = ?7, status = ?8,
                       importance = ?9, new_badge_until = ?10, updated_badge_until = ?11,
                       is_hidden = ?12, updated_at = ?13
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
                    article.new_badge_until,
                    article.updated_badge_until,
                    article.is_hidden,
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
                    , new_badge_until, updated_badge_until, is_hidden
                ) VALUES (?1, ?2, ?3, ?4, ?5, ?6, 2, ?7, ?8, ?9, ?10, ?10, ?11, ?12, ?13)
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
                    now,
                    article.new_badge_until,
                    article.updated_badge_until,
                    article.is_hidden
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

    pub fn is_codex_proposal_accepted(&self, request_id: &str) -> AppResult<bool> {
        self.connection
            .query_row(
                "SELECT EXISTS(SELECT 1 FROM codex_proposal_receipts WHERE request_id = ?1)",
                [request_id],
                |row| row.get(0),
            )
            .map_err(AppError::from)
    }

    pub fn record_codex_proposal(&self, proposal: &CodexFaqProposal) -> AppResult<()> {
        let payload_json = serde_json::to_string(proposal).map_err(|_| {
            AppError::new(
                "CDX-002",
                "Codex提案を履歴へ記録できませんでした。",
                "提案一覧を更新し、もう一度お試しください。",
            )
        })?;
        let existing: Option<String> = self
            .connection
            .query_row(
                "SELECT payload_json FROM codex_proposal_history WHERE request_id = ?1",
                [&proposal.request_id],
                |row| row.get(0),
            )
            .optional()?;
        if let Some(existing) = existing {
            if existing != payload_json {
                return Err(AppError::new(
                    "CDX-013",
                    "同じ受付番号で内容の異なるCodex提案が届いています。",
                    "Codexへ新しい受付番号で提案を作り直すよう依頼してください。",
                ));
            }
            return Ok(());
        }
        let series_id = proposal
            .series_id
            .as_deref()
            .unwrap_or(&proposal.request_id);
        self.connection.execute(
            r#"
            INSERT INTO codex_proposal_history(
                request_id, series_id, proposal_kind, payload_json, status, received_at
            ) VALUES (?1, ?2, ?3, ?4, 'pending', ?5)
            "#,
            params![
                proposal.request_id,
                series_id,
                proposal.proposal_kind.as_str(),
                payload_json,
                Utc::now().to_rfc3339(),
            ],
        )?;
        Ok(())
    }

    pub fn get_pending_codex_proposal(&self, request_id: &str) -> AppResult<CodexFaqProposal> {
        let payload: Option<String> = self
            .connection
            .query_row(
                "SELECT payload_json FROM codex_proposal_history WHERE request_id = ?1 AND status = 'pending'",
                [request_id],
                |row| row.get(0),
            )
            .optional()?;
        parse_stored_proposal(payload.ok_or_else(|| {
            AppError::new(
                "CDX-003",
                "確認待ちのCodex提案が見つかりません。",
                "提案一覧または履歴を更新して、もう一度選択してください。",
            )
        })?)
    }

    pub fn list_pending_codex_proposals(&self) -> AppResult<Vec<CodexFaqProposal>> {
        let mut statement = self.connection.prepare(
            "SELECT payload_json FROM codex_proposal_history WHERE status = 'pending' ORDER BY history_id DESC",
        )?;
        let rows = statement.query_map([], |row| row.get::<_, String>(0))?;
        rows.map(|row| parse_stored_proposal(row?)).collect()
    }

    pub fn list_codex_proposal_history(&self) -> AppResult<Vec<CodexProposalHistoryItem>> {
        let mut statement = self.connection.prepare(
            r#"
            SELECT h.history_id, h.payload_json, h.status, h.received_at, h.decided_at,
                   h.accepted_article_id,
                   CASE WHEN h.status = 'rejected' AND NOT EXISTS (
                       SELECT 1 FROM codex_proposal_history newer
                        WHERE newer.series_id = h.series_id
                          AND newer.history_id > h.history_id
                   ) THEN 1 ELSE 0 END AS can_reopen
              FROM codex_proposal_history h
             WHERE h.status <> 'pending'
             ORDER BY h.history_id DESC
            "#,
        )?;
        let rows = statement.query_map([], |row| {
            let payload: String = row.get(1)?;
            let proposal = serde_json::from_str(&payload).map_err(|error| {
                rusqlite::Error::FromSqlConversionFailure(
                    1,
                    rusqlite::types::Type::Text,
                    Box::new(error),
                )
            })?;
            Ok(CodexProposalHistoryItem {
                history_id: row.get(0)?,
                proposal,
                status: row.get(2)?,
                received_at: row.get(3)?,
                decided_at: row.get(4)?,
                accepted_article_id: row.get(5)?,
                can_reopen: row.get(6)?,
            })
        })?;
        rows.collect::<Result<Vec<_>, _>>().map_err(AppError::from)
    }

    pub fn reject_codex_proposal(&mut self, request_id: &str) -> AppResult<()> {
        let transaction = self.connection.transaction()?;
        let changed = transaction.execute(
            "UPDATE codex_proposal_history SET status = 'rejected', decided_at = ?2 WHERE request_id = ?1 AND status = 'pending'",
            params![request_id, Utc::now().to_rfc3339()],
        )?;
        if changed == 0 {
            return Err(AppError::new(
                "CDX-003",
                "確認待ちのCodex提案が見つかりません。",
                "提案一覧を更新し、もう一度選択してください。",
            ));
        }
        transaction.commit()?;
        Ok(())
    }

    pub fn reopen_rejected_codex_proposal(&mut self, request_id: &str) -> AppResult<()> {
        let transaction = self.connection.transaction()?;
        let record: Option<(i64, String, String)> = transaction
            .query_row(
                "SELECT history_id, series_id, status FROM codex_proposal_history WHERE request_id = ?1",
                [request_id],
                |row| Ok((row.get(0)?, row.get(1)?, row.get(2)?)),
            )
            .optional()?;
        let Some((history_id, series_id, status)) = record else {
            return Err(codex_history_not_found());
        };
        if status != "rejected" {
            return Err(AppError::new(
                "CDX-014",
                "このCodex提案は再検討へ戻せません。",
                "却下済みの提案を選択してください。",
            ));
        }
        let newer_exists: bool = transaction.query_row(
            "SELECT EXISTS(SELECT 1 FROM codex_proposal_history WHERE series_id = ?1 AND history_id > ?2)",
            params![series_id, history_id],
            |row| row.get(0),
        )?;
        if newer_exists {
            return Err(AppError::new(
                "CDX-015",
                "同じ依頼に、これより新しいCodex提案があります。",
                "取り違えを防ぐため、同じ依頼では最新の却下案だけを再検討できます。",
            ));
        }
        transaction.execute(
            "UPDATE codex_proposal_history SET status = 'pending', decided_at = NULL WHERE request_id = ?1",
            [request_id],
        )?;
        transaction.commit()?;
        Ok(())
    }

    pub fn accept_codex_proposal(
        &mut self,
        record: CodexProposalArticleRecord<'_>,
    ) -> AppResult<(Article, Option<Category>)> {
        let transaction = self.connection.transaction()?;
        let already_accepted: bool = transaction.query_row(
            "SELECT EXISTS(SELECT 1 FROM codex_proposal_receipts WHERE request_id = ?1)",
            [record.request_id],
            |row| row.get(0),
        )?;
        if already_accepted {
            return Err(AppError::new(
                "CDX-006",
                "このCodex提案はすでに取り込み済みです。",
                "提案一覧を更新してください。同じFAQは重複登録されていません。",
            ));
        }
        ensure_pending_codex_history(&transaction, record.request_id)?;

        match record.proposal_kind {
            CodexProposalKind::Create if !record.source_articles.is_empty() => {
                return Err(AppError::database(
                    "新規FAQ提案に既存FAQの参照が含まれています。",
                ));
            }
            CodexProposalKind::Merge if record.source_articles.len() >= 2 => {
                verify_codex_source_versions(&transaction, record.source_articles)?;
            }
            CodexProposalKind::Merge => {
                return Err(AppError::new(
                    "CDX-002",
                    "統合提案には2件以上の元FAQが必要です。",
                    "KnowledgeAppで統合対象を選び直し、Codexへ再依頼してください。",
                ));
            }
            CodexProposalKind::Revise => {
                return Err(AppError::database(
                    "修正提案が新規FAQの取込処理へ送られました。",
                ));
            }
            CodexProposalKind::Create => {}
        }

        let created_category = match record.new_category {
            Some(category) => {
                if category.id != record.category_id {
                    return Err(AppError::database(
                        "Codex提案の分類情報に不整合があります。",
                    ));
                }
                Some(insert_category(
                    &transaction,
                    category.id,
                    category.name,
                    category.description,
                    category.parent_id,
                )?)
            }
            None => {
                let exists: bool = transaction.query_row(
                    "SELECT EXISTS(SELECT 1 FROM categories WHERE id = ?1)",
                    [record.category_id],
                    |row| row.get(0),
                )?;
                if !exists {
                    return Err(AppError::new(
                        "CDX-005",
                        "選択した分類が現在の分類一覧にありません。",
                        "提案一覧を更新し、所属分類を選び直してください。",
                    ));
                }
                None
            }
        };

        let now = Utc::now().to_rfc3339();
        let body_doc_json = serde_json::to_string(record.body_doc).map_err(|_| {
            AppError::new(
                "CDX-002",
                "Codex提案の回答形式を保存できません。",
                "Codexへもう一度下書き作成を依頼してください。",
            )
        })?;
        transaction.execute(
            r#"
            INSERT INTO articles(
                id, category_id, title, normalized_title, summary, body_doc_json,
                body_format_version, body_plain_text, status, importance, created_at, updated_at,
                new_badge_until, updated_badge_until, is_hidden
            ) VALUES (?1, ?2, ?3, ?4, ?5, ?6, 2, ?7, 'draft', ?8, ?9, ?9, NULL, NULL, 0)
            "#,
            params![
                record.article_id,
                record.category_id,
                record.title,
                normalize(record.title),
                record.summary,
                body_doc_json,
                record.body_plain_text,
                record.importance,
                now,
            ],
        )?;
        transaction.execute(
            "INSERT INTO article_search_documents(article_id, title, summary, body) VALUES (?1, ?2, ?3, ?4)",
            params![
                record.article_id,
                normalize(record.title),
                normalize(record.summary),
                normalize(record.body_plain_text),
            ],
        )?;
        transaction.execute(
            "INSERT INTO article_search_fts(article_id, title, summary, body) VALUES (?1, ?2, ?3, ?4)",
            params![
                record.article_id,
                normalize(record.title),
                normalize(record.summary),
                normalize(record.body_plain_text),
            ],
        )?;
        transaction.execute(
            "INSERT INTO codex_proposal_receipts(request_id, article_id, accepted_at) VALUES (?1, ?2, ?3)",
            params![record.request_id, record.article_id, now],
        )?;
        mark_codex_history_accepted(&transaction, record.request_id, record.article_id, &now)?;
        transaction.commit()?;
        let created_category = created_category.map(|mut category| {
            category.article_count = 1;
            category
        });
        Ok((self.get_article(record.article_id)?, created_category))
    }

    pub fn accept_codex_revision(
        &mut self,
        record: CodexProposalRevisionRecord<'_>,
    ) -> AppResult<Article> {
        let transaction = self.connection.transaction()?;
        let already_accepted: bool = transaction.query_row(
            "SELECT EXISTS(SELECT 1 FROM codex_proposal_receipts WHERE request_id = ?1)",
            [record.request_id],
            |row| row.get(0),
        )?;
        if already_accepted {
            return Err(AppError::new(
                "CDX-006",
                "このCodex提案はすでに反映済みです。",
                "提案一覧を更新してください。同じ修正は重複反映されていません。",
            ));
        }
        ensure_pending_codex_history(&transaction, record.request_id)?;
        verify_codex_source_versions(&transaction, std::slice::from_ref(record.source_article))?;

        let now = Utc::now().to_rfc3339();
        let body_doc_json = serde_json::to_string(record.body_doc).map_err(|_| {
            AppError::new(
                "CDX-002",
                "Codex修正案の回答形式を保存できません。",
                "Codexへもう一度修正を依頼してください。",
            )
        })?;
        let updated = transaction.execute(
            r#"
            UPDATE articles
               SET title = ?2, normalized_title = ?3, summary = ?4,
                   body_doc_json = ?5, body_format_version = 2, body_plain_text = ?6, importance = ?7,
                   updated_at = ?8
             WHERE id = ?1 AND deleted_at IS NULL
            "#,
            params![
                record.source_article.article_id,
                record.title,
                normalize(record.title),
                record.summary,
                body_doc_json,
                record.body_plain_text,
                record.importance,
                now,
            ],
        )?;
        if updated == 0 {
            return Err(article_not_found());
        }
        transaction.execute(
            r#"
            INSERT INTO article_search_documents(article_id, title, summary, body)
            VALUES (?1, ?2, ?3, ?4)
            ON CONFLICT(article_id) DO UPDATE SET
                title = excluded.title, summary = excluded.summary, body = excluded.body
            "#,
            params![
                record.source_article.article_id,
                normalize(record.title),
                normalize(record.summary),
                normalize(record.body_plain_text),
            ],
        )?;
        transaction.execute(
            "DELETE FROM article_search_fts WHERE article_id = ?1",
            [&record.source_article.article_id],
        )?;
        transaction.execute(
            "INSERT INTO article_search_fts(article_id, title, summary, body) VALUES (?1, ?2, ?3, ?4)",
            params![
                record.source_article.article_id,
                normalize(record.title),
                normalize(record.summary),
                normalize(record.body_plain_text),
            ],
        )?;
        transaction.execute(
            "INSERT INTO codex_proposal_receipts(request_id, article_id, accepted_at) VALUES (?1, ?2, ?3)",
            params![record.request_id, record.source_article.article_id, now],
        )?;
        mark_codex_history_accepted(
            &transaction,
            record.request_id,
            &record.source_article.article_id,
            &now,
        )?;
        transaction.commit()?;
        self.get_article(&record.source_article.article_id)
    }

    pub fn get_article(&self, id: &str) -> AppResult<Article> {
        let mut article = self
            .connection
            .query_row(
                r#"
                SELECT a.id, a.category_id, c.name, a.title, a.summary, a.body_doc_json,
                       a.body_plain_text, a.status, a.importance, a.created_at, a.updated_at,
                       a.deleted_at, a.new_badge_until, a.updated_badge_until, a.is_hidden
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
                   a.status, a.importance, a.new_badge_until, a.updated_badge_until,
                   a.is_hidden, a.updated_at
              FROM articles a
              JOIN categories c ON c.id = a.category_id
              JOIN article_search_documents search_doc ON search_doc.article_id = a.id
             WHERE a.deleted_at IS NULL
               AND a.is_hidden = 0
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
                    new_badge_until: row.get(7)?,
                    updated_badge_until: row.get(8)?,
                    is_hidden: row.get(9)?,
                    updated_at: row.get(10)?,
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
                       a.status, a.importance, a.new_badge_until, a.updated_badge_until,
                       a.is_hidden, a.updated_at, a.deleted_at
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
                   new_badge_until, updated_badge_until, is_hidden, updated_at, deleted_at,
                   COUNT(*) OVER()
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
                        new_badge_until: row.get(7)?,
                        updated_badge_until: row.get(8)?,
                        is_hidden: row.get(9)?,
                        updated_at: row.get(10)?,
                        deleted_at: row.get(11)?,
                    },
                    row.get::<_, i64>(12)?,
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

fn parse_stored_proposal(payload: String) -> AppResult<CodexFaqProposal> {
    serde_json::from_str(&payload)
        .map_err(|_| AppError::database("保存済みのCodex提案履歴を読み取れませんでした。"))
}

fn ensure_pending_codex_history(transaction: &Transaction<'_>, request_id: &str) -> AppResult<()> {
    let pending: bool = transaction.query_row(
        "SELECT EXISTS(SELECT 1 FROM codex_proposal_history WHERE request_id = ?1 AND status = 'pending')",
        [request_id],
        |row| row.get(0),
    )?;
    if pending {
        Ok(())
    } else {
        Err(AppError::new(
            "CDX-003",
            "確認待ちのCodex提案が見つかりません。",
            "提案一覧または履歴を更新し、もう一度選択してください。",
        ))
    }
}

fn mark_codex_history_accepted(
    transaction: &Transaction<'_>,
    request_id: &str,
    article_id: &str,
    decided_at: &str,
) -> AppResult<()> {
    let changed = transaction.execute(
        "UPDATE codex_proposal_history SET status = 'accepted', decided_at = ?2, accepted_article_id = ?3 WHERE request_id = ?1 AND status = 'pending'",
        params![request_id, decided_at, article_id],
    )?;
    if changed == 1 {
        Ok(())
    } else {
        Err(AppError::database(
            "Codex提案履歴の確定状態を更新できませんでした。",
        ))
    }
}

fn verify_codex_source_versions(
    transaction: &Transaction<'_>,
    sources: &[CodexSourceArticle],
) -> AppResult<()> {
    for source in sources {
        let current: Option<(String, Option<String>)> = transaction
            .query_row(
                "SELECT updated_at, deleted_at FROM articles WHERE id = ?1",
                [&source.article_id],
                |row| Ok((row.get(0)?, row.get(1)?)),
            )
            .optional()?;
        let Some((updated_at, deleted_at)) = current else {
            return Err(AppError::new(
                "CDX-012",
                "Codexへ委譲した元FAQが見つかりません。",
                "現在のFAQを選び直して、新しい委譲番号で再依頼してください。",
            ));
        };
        if deleted_at.is_some() || updated_at != source.source_updated_at {
            return Err(AppError::new(
                "CDX-012",
                "Codexへの委譲後に元FAQが変更または削除されています。",
                "古い提案の上書きを防ぐため反映を中止しました。現在のFAQから再度Codexへ依頼してください。",
            ));
        }
    }
    Ok(())
}

fn codex_history_not_found() -> AppError {
    AppError::new(
        "CDX-014",
        "指定したCodex提案履歴が見つかりません。",
        "履歴一覧を更新し、もう一度選択してください。",
    )
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
        new_badge_until: row.get(12)?,
        updated_badge_until: row.get(13)?,
        is_hidden: row.get(14)?,
        attachments: Vec::new(),
    })
}

fn insert_category(
    transaction: &Transaction<'_>,
    id: &str,
    name: &str,
    description: &str,
    parent_id: Option<&str>,
) -> AppResult<Category> {
    let name = validate_category_name(name)?;
    let description = validate_category_description(description)?;
    let depth = match parent_id {
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
            "既存分類を選ぶか、別の分類名へ変更してください。",
        ));
    }

    let sort_order: i64 = transaction.query_row(
        "SELECT COALESCE(MAX(sort_order), -1) + 1 FROM categories WHERE parent_id IS ?1",
        [parent_id],
        |row| row.get(0),
    )?;
    let now = Utc::now().to_rfc3339();
    transaction.execute(
        "INSERT INTO categories(id, parent_id, name, normalized_name, description, depth, sort_order, created_at, updated_at) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?8)",
        params![id, parent_id, name, normalized_name, description, depth, sort_order, now],
    )?;
    Ok(Category {
        id: id.to_owned(),
        parent_id: parent_id.map(str::to_owned),
        name: name.to_owned(),
        description: description.to_owned(),
        depth,
        sort_order,
        article_count: 0,
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

fn validate_category_description(description: &str) -> AppResult<&str> {
    let description = description.trim();
    if description.chars().count() > 500 {
        return Err(AppError::new(
            "CAT-001",
            "分類の説明は500文字以内で入力してください。",
            "説明を短くして、もう一度保存してください。",
        ));
    }
    Ok(description)
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

    fn record_pending_proposal(database: &Database, request_id: &str, body: &Value) {
        database
            .record_codex_proposal(&CodexFaqProposal {
                format_version: 2,
                request_id: request_id.to_owned(),
                series_id: Some(request_id.to_owned()),
                created_at: Utc::now().to_rfc3339(),
                proposal_kind: CodexProposalKind::Create,
                source_articles: vec![],
                faq: crate::models::CodexFaqDraft {
                    title: "テスト提案".into(),
                    summary: String::new(),
                    body_doc: body.clone(),
                    importance: 1,
                },
                existing_category_candidates: vec![],
                new_category_proposal: None,
            })
            .unwrap();
    }

    #[test]
    fn opens_and_upgrades_a_version_one_database() {
        let directory = tempfile::tempdir().unwrap();
        let path = directory.path().join("knowledge.db");
        let connection = Connection::open(&path).unwrap();
        connection.execute_batch(INITIAL_MIGRATION).unwrap();
        drop(connection);

        let database = Database::open(&path).unwrap();
        assert_eq!(database.schema_version().unwrap(), 4);
        let columns: i64 = database
            .connection
            .query_row(
                "SELECT COUNT(*) FROM pragma_table_info('articles') WHERE name IN ('new_badge_until', 'updated_badge_until', 'is_hidden')",
                [],
                |row| row.get(0),
            )
            .unwrap();
        assert_eq!(columns, 3);
        let category_description_columns: i64 = database
            .connection
            .query_row(
                "SELECT COUNT(*) FROM pragma_table_info('categories') WHERE name = 'description'",
                [],
                |row| row.get(0),
            )
            .unwrap();
        assert_eq!(category_description_columns, 1);
        let proposal_history_table: i64 = database
            .connection
            .query_row(
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'codex_proposal_history'",
                [],
                |row| row.get(0),
            )
            .unwrap();
        assert_eq!(proposal_history_table, 1);
    }

    #[test]
    fn display_settings_default_to_green_with_category_titles_and_persist() {
        let (_directory, database) = temporary_database();

        assert_eq!(database.get_settings().unwrap(), AppSettings::default());

        let settings = AppSettings {
            color_theme: crate::models::ColorTheme::Blue,
            show_top_category_in_title: false,
        };
        database.save_settings(&settings).unwrap();

        assert_eq!(database.get_settings().unwrap(), settings);
    }

    #[test]
    fn existing_appearance_settings_default_category_titles_to_visible() {
        let (_directory, database) = temporary_database();
        database
            .connection
            .execute(
                "INSERT INTO app_settings(key, value_json, updated_at) VALUES (?1, ?2, ?3)",
                params![
                    APPEARANCE_SETTINGS_KEY,
                    r#"{"colorTheme":"blue"}"#,
                    "2026-08-15T00:00:00Z"
                ],
            )
            .unwrap();

        let settings = database.get_settings().unwrap();
        assert_eq!(settings.color_theme, crate::models::ColorTheme::Blue);
        assert!(settings.show_top_category_in_title);
    }

    #[test]
    fn restore_upgrades_a_version_one_snapshot_and_keeps_articles() {
        let directory = tempfile::tempdir().unwrap();
        let source_path = directory.path().join("version-one.db");
        let source = Connection::open(&source_path).unwrap();
        source.execute_batch(INITIAL_MIGRATION).unwrap();
        let now = "2026-08-10T00:00:00Z";
        source
            .execute(
                "INSERT INTO categories(id, name, normalized_name, depth, sort_order, created_at, updated_at) VALUES ('cat', '分類', '分類', 1, 0, ?1, ?1)",
                [now],
            )
            .unwrap();
        source
            .execute(
                "INSERT INTO articles(id, category_id, title, normalized_title, summary, body_doc_json, body_plain_text, status, importance, created_at, updated_at) VALUES ('article', 'cat', '旧FAQ', '旧faq', '概要', '{\"type\":\"doc\"}', '回答', 'published', 1, ?1, ?1)",
                [now],
            )
            .unwrap();
        drop(source);

        let target_path = directory.path().join("current.db");
        let mut target = Database::open(&target_path).unwrap();
        target.restore_from(&source_path).unwrap();

        assert_eq!(target.schema_version().unwrap(), 4);
        let article = target.get_article("article").unwrap();
        assert_eq!(article.title, "旧FAQ");
        assert!(!article.is_hidden);
        assert!(article.new_badge_until.is_none());
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
                new_badge_until: Some("2026-08-31"),
                updated_badge_until: Some("2026-09-15"),
                is_hidden: false,
                attachments: &[],
            })
            .unwrap();
        drop(database);

        let database = Database::open(&directory.path().join("knowledge.db")).unwrap();
        let reopened = database.get_article(&draft.id).unwrap();
        assert_eq!(reopened.title, "画面が真っ暗");
        assert_eq!(reopened.new_badge_until.as_deref(), Some("2026-08-31"));
        assert_eq!(reopened.updated_badge_until.as_deref(), Some("2026-09-15"));
        let body_format_version: i64 = database
            .connection
            .query_row(
                "SELECT body_format_version FROM articles WHERE id = ?1",
                [&draft.id],
                |row| row.get(0),
            )
            .unwrap();
        assert_eq!(body_format_version, 2);
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
    fn hidden_articles_are_only_available_in_management() {
        let (_directory, mut database) = temporary_database();
        let category = database.create_category("社内", None).unwrap();
        let body = json!({"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"管理者向け"}]}]});
        let article_id = Uuid::now_v7().to_string();
        let hidden = database
            .save_article(ArticleRecord {
                id: &article_id,
                is_new: true,
                category_id: &category.id,
                title: "非表示のFAQ",
                summary: "管理一覧だけに表示します",
                body_doc: &body,
                body_plain_text: "管理者向け",
                status: "published",
                importance: 1,
                new_badge_until: Some("2026-08-31"),
                updated_badge_until: None,
                is_hidden: true,
                attachments: &[],
            })
            .unwrap();

        assert!(hidden.is_hidden);
        assert!(
            database
                .search_articles(&SearchArticlesInput {
                    query: "非表示".into(),
                    category_id: None,
                    include_drafts: true,
                })
                .unwrap()
                .is_empty()
        );
        let management = database
            .list_articles_for_management(&ManagementArticlesInput {
                query: "非表示".into(),
                category_id: None,
                status: None,
                deleted: false,
                page: 1,
            })
            .unwrap();
        assert_eq!(management.total, 1);
        assert!(management.items[0].is_hidden);
    }

    #[test]
    fn codex_proposal_creates_draft_and_category_once_in_one_transaction() {
        let (_directory, mut database) = temporary_database();
        let parent = database
            .create_category_with_description("PC", "PC全般", None)
            .unwrap();
        let request_id = Uuid::now_v7().to_string();
        let article_id = Uuid::now_v7().to_string();
        let category_id = Uuid::now_v7().to_string();
        let body = json!({"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"再起動します。"}]}]});
        record_pending_proposal(&database, &request_id, &body);

        let (article, created) = database
            .accept_codex_proposal(CodexProposalArticleRecord {
                request_id: &request_id,
                proposal_kind: CodexProposalKind::Create,
                source_articles: &[],
                article_id: &article_id,
                category_id: &category_id,
                new_category: Some(NewCategoryRecord {
                    id: &category_id,
                    name: "Windows",
                    description: "Windowsの操作",
                    parent_id: Some(&parent.id),
                }),
                title: "Windowsを再起動するには？",
                summary: "通常の再起動手順です。",
                body_doc: &body,
                body_plain_text: "再起動します。",
                importance: 1,
            })
            .unwrap();
        assert_eq!(article.status, "draft");
        assert_eq!(article.category_id, category_id);
        assert_eq!(created.unwrap().description, "Windowsの操作");
        assert!(database.is_codex_proposal_accepted(&request_id).unwrap());

        let duplicate_id = Uuid::now_v7().to_string();
        let error = database
            .accept_codex_proposal(CodexProposalArticleRecord {
                request_id: &request_id,
                proposal_kind: CodexProposalKind::Create,
                source_articles: &[],
                article_id: &duplicate_id,
                category_id: &category_id,
                new_category: None,
                title: "重複",
                summary: "",
                body_doc: &body,
                body_plain_text: "重複",
                importance: 1,
            })
            .unwrap_err();
        assert_eq!(error.code, "CDX-006");
        assert!(database.get_article(&duplicate_id).is_err());
    }

    #[test]
    fn codex_category_conflict_rolls_back_article_and_receipt() {
        let (_directory, mut database) = temporary_database();
        let parent = database.create_category("PC", None).unwrap();
        database
            .create_category("Windows", Some(&parent.id))
            .unwrap();
        let request_id = Uuid::now_v7().to_string();
        let article_id = Uuid::now_v7().to_string();
        let category_id = Uuid::now_v7().to_string();
        let body = json!({"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"回答"}]}]});
        record_pending_proposal(&database, &request_id, &body);

        let error = database
            .accept_codex_proposal(CodexProposalArticleRecord {
                request_id: &request_id,
                proposal_kind: CodexProposalKind::Create,
                source_articles: &[],
                article_id: &article_id,
                category_id: &category_id,
                new_category: Some(NewCategoryRecord {
                    id: &category_id,
                    name: "Windows",
                    description: "重複",
                    parent_id: Some(&parent.id),
                }),
                title: "競合テスト",
                summary: "",
                body_doc: &body,
                body_plain_text: "回答",
                importance: 1,
            })
            .unwrap_err();
        assert_eq!(error.code, "CAT-004");
        assert!(database.get_article(&article_id).is_err());
        assert!(!database.is_codex_proposal_accepted(&request_id).unwrap());
        assert_eq!(database.list_categories().unwrap().len(), 2);
    }

    #[test]
    fn codex_revision_checks_source_version_and_preserves_article_state() {
        let (_directory, mut database) = temporary_database();
        let category = database.create_category("PC", None).unwrap();
        let article_id = Uuid::now_v7().to_string();
        let original_body = json!({"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"元の回答"}]}]});
        let original = database
            .save_article(ArticleRecord {
                id: &article_id,
                is_new: true,
                category_id: &category.id,
                title: "元の質問",
                summary: "元の概要",
                body_doc: &original_body,
                body_plain_text: "元の回答",
                status: "published",
                importance: 1,
                new_badge_until: Some("2026-08-31"),
                updated_badge_until: None,
                is_hidden: true,
                attachments: &[],
            })
            .unwrap();
        let request_id = Uuid::now_v7().to_string();
        let series_id = Uuid::now_v7().to_string();
        let source = CodexSourceArticle {
            article_id: article_id.clone(),
            source_updated_at: original.updated_at.clone(),
        };
        let revised_body = json!({"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"読みやすい回答"}]}]});
        database
            .record_codex_proposal(&CodexFaqProposal {
                format_version: 2,
                request_id: request_id.clone(),
                series_id: Some(series_id),
                created_at: Utc::now().to_rfc3339(),
                proposal_kind: CodexProposalKind::Revise,
                source_articles: vec![source.clone()],
                faq: crate::models::CodexFaqDraft {
                    title: "読みやすい質問".into(),
                    summary: "読みやすい概要".into(),
                    body_doc: revised_body.clone(),
                    importance: 2,
                },
                existing_category_candidates: vec![],
                new_category_proposal: None,
            })
            .unwrap();

        let revised = database
            .accept_codex_revision(CodexProposalRevisionRecord {
                request_id: &request_id,
                source_article: &source,
                title: "読みやすい質問",
                summary: "読みやすい概要",
                body_doc: &revised_body,
                body_plain_text: "読みやすい回答",
                importance: 2,
            })
            .unwrap();
        assert_eq!(revised.status, "published");
        assert_eq!(revised.category_id, category.id);
        assert!(revised.is_hidden);
        assert_eq!(revised.new_badge_until.as_deref(), Some("2026-08-31"));
        assert_eq!(revised.title, "読みやすい質問");

        let stale_request = Uuid::now_v7().to_string();
        let stale = CodexFaqProposal {
            format_version: 2,
            request_id: stale_request.clone(),
            series_id: Some(Uuid::now_v7().to_string()),
            created_at: Utc::now().to_rfc3339(),
            proposal_kind: CodexProposalKind::Revise,
            source_articles: vec![source.clone()],
            faq: crate::models::CodexFaqDraft {
                title: "古い案".into(),
                summary: String::new(),
                body_doc: revised_body.clone(),
                importance: 1,
            },
            existing_category_candidates: vec![],
            new_category_proposal: None,
        };
        database.record_codex_proposal(&stale).unwrap();
        let error = database
            .accept_codex_revision(CodexProposalRevisionRecord {
                request_id: &stale_request,
                source_article: &source,
                title: "古い案",
                summary: "",
                body_doc: &revised_body,
                body_plain_text: "古い案",
                importance: 1,
            })
            .unwrap_err();
        assert_eq!(error.code, "CDX-012");
        assert_eq!(
            database.get_article(&article_id).unwrap().title,
            "読みやすい質問"
        );
    }

    #[test]
    fn codex_history_reopens_only_the_latest_rejected_proposal_in_a_series() {
        let (_directory, mut database) = temporary_database();
        let series_id = Uuid::now_v7().to_string();
        let body = json!({"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"回答"}]}]});
        let mut request_ids = Vec::new();
        for title in ["最初の案", "新しい案"] {
            let request_id = Uuid::now_v7().to_string();
            request_ids.push(request_id.clone());
            database
                .record_codex_proposal(&CodexFaqProposal {
                    format_version: 2,
                    request_id: request_id.clone(),
                    series_id: Some(series_id.clone()),
                    created_at: Utc::now().to_rfc3339(),
                    proposal_kind: CodexProposalKind::Create,
                    source_articles: vec![],
                    faq: crate::models::CodexFaqDraft {
                        title: title.into(),
                        summary: String::new(),
                        body_doc: body.clone(),
                        importance: 1,
                    },
                    existing_category_candidates: vec![],
                    new_category_proposal: None,
                })
                .unwrap();
            database.reject_codex_proposal(&request_id).unwrap();
        }

        let history = database.list_codex_proposal_history().unwrap();
        assert_eq!(history.len(), 2);
        assert!(history[0].can_reopen);
        assert!(!history[1].can_reopen);
        assert_eq!(
            database
                .reopen_rejected_codex_proposal(&request_ids[0])
                .unwrap_err()
                .code,
            "CDX-015"
        );
        database
            .reopen_rejected_codex_proposal(&request_ids[1])
            .unwrap();
        assert_eq!(database.list_pending_codex_proposals().unwrap().len(), 1);
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
                new_badge_until: None,
                updated_badge_until: None,
                is_hidden: false,
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
