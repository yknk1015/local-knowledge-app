use std::{
    cmp::Ordering,
    collections::{HashMap, HashSet},
    fs,
    io::Write,
    path::Path,
    time::Duration,
};

use chrono::{Days, NaiveDate, Utc};
use rusqlite::{Connection, OpenFlags, OptionalExtension, Transaction, backup::Backup, params};
use serde_json::Value;
use sha2::{Digest, Sha256};
use unicode_normalization::UnicodeNormalization;
use uuid::Uuid;

use crate::{
    errors::{AppError, AppResult},
    models::{
        AppSettings, Article, ArticleAttachment, ArticleListItem, ArticleMergeInfo,
        AuthenticatedUser, BackupCounts, Category, CategoryMoveDirection, CodexFaqProposal,
        CodexMergePublicationContext, CodexMergeSourcePreview, CodexProposalHistoryItem,
        CodexProposalKind, CodexSourceArticle, CsvExportResult, CsvImportPreview,
        CsvImportPreviewRow, CsvImportResult, HistoryTarget, JsonArticleData,
        JsonArticleRelationData, JsonCategoryData, JsonEntityCounts, JsonExportResult,
        JsonImportPreview, JsonImportResult, JsonMergeRelationData, JsonSynonymGroupData,
        JsonTagData, KnowledgeJsonDocument, ListSearchLogsInput, ListViewLogsInput,
        ManagementArticleListItem, ManagementArticlePage, ManagementArticlesInput,
        MarkCodexMergeSourcesResult, PasswordPolicySettings, RelatedArticleCandidate,
        RelatedArticleSummary, SearchArticlePage, SearchArticlesInput, SearchLogItem,
        SearchLogPage, SearchScope, SearchSort, SynonymGroup, UserRole, UserSummary, ViewLogItem,
        ViewLogPage,
    },
};

const INITIAL_MIGRATION: &str = include_str!("../../migrations/0001_initial.sql");
const ARTICLE_DISPLAY_FLAGS_MIGRATION: &str =
    include_str!("../../migrations/0002_article_display_flags.sql");
const CODEX_PROPOSALS_MIGRATION: &str = include_str!("../../migrations/0003_codex_proposals.sql");
const CODEX_DELEGATION_HISTORY_MIGRATION: &str =
    include_str!("../../migrations/0004_codex_delegation_history.sql");
const ARTICLE_MERGE_RELATIONS_MIGRATION: &str =
    include_str!("../../migrations/0005_article_merge_relations.sql");
const USERS_AND_ARTICLE_AUDIT_MIGRATION: &str =
    include_str!("../../migrations/0006_users_and_article_audit.sql");
const MANAGEMENT_CODES_MIGRATION: &str = include_str!("../../migrations/0007_management_codes.sql");
pub const CURRENT_SCHEMA_VERSION: i64 = 7;
const APPEARANCE_SETTINGS_KEY: &str = "appearance";
const PASSWORD_POLICY_SETTINGS_KEY: &str = "password_policy";
const INITIAL_ADMIN_USER_ID: &str = "00000000-0000-7000-8000-000000000000";
type CodexMergeSourceRow = (String, String, String, Option<String>, Option<String>);

#[derive(Debug, Clone)]
struct CsvImportPlanRow {
    line: usize,
    action: CsvRowAction,
    faq_management_id: Option<String>,
    article_id: String,
    category_id: String,
    title: String,
    summary: String,
    body_plain_text: String,
    body_doc_json: String,
    body_will_be_replaced: bool,
    status: String,
    importance: i64,
    new_badge_until: Option<String>,
    updated_badge_until: Option<String>,
    is_hidden: bool,
    stale_update_will_overwrite: bool,
    messages: Vec<String>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum CsvRowAction {
    Create,
    Update,
    Unchanged,
    Error,
}

#[derive(Debug)]
struct ExistingCsvArticle {
    article_id: String,
    category_id: String,
    title: String,
    summary: String,
    body_plain_text: String,
    body_doc_json: String,
    status: String,
    importance: i64,
    new_badge_until: Option<String>,
    updated_badge_until: Option<String>,
    is_hidden: bool,
    updated_at: String,
    has_attachments: bool,
    is_merge_target: bool,
}

#[derive(Debug)]
struct SearchCandidate {
    item: ArticleListItem,
    normalized_title: String,
    normalized_summary: String,
    body: String,
    symptoms: String,
    causes: String,
    targets: String,
    error_codes: String,
    normalized_tags: String,
    search_terms: String,
    procedures: String,
    cautions: String,
}

#[derive(Debug, Clone)]
struct SearchVariant {
    value: String,
    synonym_from: Option<String>,
}

#[derive(Debug)]
struct SearchQueryGroup {
    display: String,
    variants: Vec<SearchVariant>,
}

#[derive(Debug)]
struct ScoredSearchCandidate {
    item: ArticleListItem,
    score: i64,
    matched_groups: usize,
}

impl CsvRowAction {
    fn as_str(self) -> &'static str {
        match self {
            Self::Create => "create",
            Self::Update => "update",
            Self::Unchanged => "unchanged",
            Self::Error => "error",
        }
    }
}

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

pub struct ArticleDetailsRecord<'a> {
    pub symptoms: &'a [String],
    pub causes: &'a [String],
    pub targets: &'a [String],
    pub error_codes: &'a [String],
    pub procedures: &'a [String],
    pub cautions: &'a [String],
    pub tags: &'a [String],
    pub search_terms: &'a [String],
    pub related_article_ids: &'a [String],
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
    pub fn open_existing_read_only(path: &Path) -> AppResult<Option<Self>> {
        if !path.is_file() {
            return Ok(None);
        }

        let connection = Connection::open_with_flags(path, OpenFlags::SQLITE_OPEN_READ_ONLY)
            .map_err(|_| AppError::database("移行前のFAQデータベースを開けませんでした。"))?;
        connection
            .busy_timeout(Duration::from_secs(5))
            .map_err(AppError::from)?;
        let database = Self { connection };
        database.quick_check()?;

        let required_tables: i64 = database
            .connection
            .query_row(
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('schema_migrations', 'categories', 'articles')",
                [],
                |row| row.get(0),
            )
            .map_err(AppError::from)?;
        if required_tables != 3 {
            return Err(AppError::database(
                "FAQデータベースの形式を確認できないため、更新を中止しました。",
            ));
        }

        let version = database.schema_version()?;
        if !(1..=CURRENT_SCHEMA_VERSION).contains(&version) {
            return Err(AppError::database(if version > CURRENT_SCHEMA_VERSION {
                "このFAQデータは、現在のアプリより新しい形式です。アプリを更新してください。"
            } else {
                "FAQデータベースの版情報を確認できないため、更新を中止しました。"
            }));
        }
        Ok(Some(database))
    }

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
        database.ensure_initial_admin()?;
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
        self.ensure_initial_admin()?;
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
        if version < 5 {
            self.connection
                .execute_batch(ARTICLE_MERGE_RELATIONS_MIGRATION)
                .map_err(|_| AppError::database("FAQ統合関係用データの更新に失敗しました。"))?;
        }
        if version < 6 {
            self.connection
                .execute_batch(USERS_AND_ARTICLE_AUDIT_MIGRATION)
                .map_err(|_| AppError::database("利用者・FAQ更新者データの更新に失敗しました。"))?;
        }
        if version < 7 {
            self.connection
                .execute_batch(MANAGEMENT_CODES_MIGRATION)
                .map_err(|_| AppError::database("FAQ・分類管理IDの更新に失敗しました。"))?;
        }
        Ok(())
    }

    fn ensure_initial_admin(&self) -> AppResult<()> {
        let user_count: i64 =
            self.connection
                .query_row("SELECT COUNT(*) FROM users", [], |row| row.get(0))?;
        if user_count == 0 {
            let now = Utc::now().to_rfc3339();
            let password_hash = crate::services::auth::hash_password("")?;
            self.connection.execute(
                r#"
                INSERT INTO users(
                    id, login_id, normalized_login_id, display_name, password_hash,
                    role, is_active, created_at, updated_at
                ) VALUES (?1, '0000', '0000', '初期管理者', ?2, 'admin', 1, ?3, ?3)
                "#,
                params![INITIAL_ADMIN_USER_ID, password_hash, now],
            )?;
        }
        self.connection.execute(
            "UPDATE articles SET created_by_user_id = COALESCE(created_by_user_id, ?1), updated_by_user_id = COALESCE(updated_by_user_id, ?1)",
            [INITIAL_ADMIN_USER_ID],
        )?;
        Ok(())
    }

    pub fn authenticate_user(
        &self,
        login_id: &str,
        password: &str,
    ) -> AppResult<AuthenticatedUser> {
        if login_id.chars().count() > 100 || password.chars().count() > 1024 {
            return Err(crate::services::auth::authentication_error());
        }
        let normalized = crate::services::auth::normalize_login_id(login_id);
        let record: Option<(String, String, String, String, bool)> = self.connection.query_row(
            "SELECT id, login_id, display_name, role, is_active FROM users WHERE normalized_login_id = ?1",
            [&normalized],
            |row| Ok((row.get(0)?, row.get(1)?, row.get(2)?, row.get(3)?, row.get(4)?)),
        ).optional()?;
        let Some((id, login_id, display_name, role, is_active)) = record else {
            return Err(crate::services::auth::authentication_error());
        };
        if !is_active {
            return Err(crate::services::auth::authentication_error());
        }
        let password_hash: String = self.connection.query_row(
            "SELECT password_hash FROM users WHERE id = ?1",
            [&id],
            |row| row.get(0),
        )?;
        if !crate::services::auth::verify_password(password, &password_hash) {
            return Err(crate::services::auth::authentication_error());
        }
        self.connection.execute(
            "UPDATE users SET last_login_at = ?2 WHERE id = ?1",
            params![id, Utc::now().to_rfc3339()],
        )?;
        Ok(AuthenticatedUser {
            id,
            login_id,
            display_name,
            role: parse_user_role(&role)?,
        })
    }

    pub fn list_users(&self) -> AppResult<Vec<UserSummary>> {
        let mut statement = self.connection.prepare(
            "SELECT id, login_id, display_name, role, is_active, created_at, updated_at, last_login_at FROM users ORDER BY created_at, login_id"
        )?;
        let rows = statement.query_map([], |row| {
            let role: String = row.get(3)?;
            Ok((
                row.get::<_, String>(0)?,
                row.get::<_, String>(1)?,
                row.get::<_, String>(2)?,
                role,
                row.get::<_, bool>(4)?,
                row.get::<_, String>(5)?,
                row.get::<_, String>(6)?,
                row.get::<_, Option<String>>(7)?,
            ))
        })?;
        rows.map(|row| {
            let (
                id,
                login_id,
                display_name,
                role,
                is_active,
                created_at,
                updated_at,
                last_login_at,
            ) = row?;
            Ok(UserSummary {
                id,
                login_id,
                display_name,
                role: parse_user_role(&role)?,
                is_active,
                created_at,
                updated_at,
                last_login_at,
            })
        })
        .collect()
    }

    pub fn create_user(
        &self,
        login_id: &str,
        display_name: &str,
        password: &str,
        role: UserRole,
    ) -> AppResult<UserSummary> {
        self.ensure_password_allowed(password)?;
        let login_id = validate_user_text(login_id, "ログインID")?;
        let display_name = validate_user_text(display_name, "表示名")?;
        let normalized = crate::services::auth::normalize_login_id(login_id);
        let duplicate: bool = self.connection.query_row(
            "SELECT EXISTS(SELECT 1 FROM users WHERE normalized_login_id = ?1)",
            [&normalized],
            |row| row.get(0),
        )?;
        if duplicate {
            return Err(AppError::new(
                "USR-001",
                "同じログインIDがすでに登録されています。",
                "別のログインIDを入力してください。",
            ));
        }
        let id = Uuid::now_v7().to_string();
        let now = Utc::now().to_rfc3339();
        let password_hash = crate::services::auth::hash_password(password)?;
        self.connection.execute(
            "INSERT INTO users(id, login_id, normalized_login_id, display_name, password_hash, role, is_active, created_at, updated_at) VALUES (?1, ?2, ?3, ?4, ?5, ?6, 1, ?7, ?7)",
            params![id, login_id, normalized, display_name, password_hash, role.as_str(), now],
        )?;
        Ok(UserSummary {
            id,
            login_id: login_id.to_owned(),
            display_name: display_name.to_owned(),
            role,
            is_active: true,
            created_at: now.clone(),
            updated_at: now,
            last_login_at: None,
        })
    }

    pub fn set_user_active(&self, id: &str, is_active: bool) -> AppResult<UserSummary> {
        if !is_active {
            let is_last_admin: bool = self.connection.query_row(
                "SELECT role = 'admin' AND (SELECT COUNT(*) FROM users WHERE role = 'admin' AND is_active = 1) <= 1 FROM users WHERE id = ?1",
                [id], |row| row.get(0)
            ).optional()?.ok_or_else(user_not_found)?;
            if is_last_admin {
                return Err(AppError::new(
                    "USR-002",
                    "最後の有効な管理者は利用停止にできません。",
                    "別の管理者を追加してから利用停止にしてください。",
                ));
            }
        }
        let changed = self.connection.execute(
            "UPDATE users SET is_active = ?2, updated_at = ?3 WHERE id = ?1",
            params![id, is_active, Utc::now().to_rfc3339()],
        )?;
        if changed == 0 {
            return Err(user_not_found());
        }
        self.get_user(id)
    }

    pub fn reset_user_password(&self, id: &str, password: &str) -> AppResult<UserSummary> {
        self.ensure_password_allowed(password)?;
        let password_hash = crate::services::auth::hash_password(password)?;
        let changed = self.connection.execute(
            "UPDATE users SET password_hash = ?2, updated_at = ?3 WHERE id = ?1",
            params![id, password_hash, Utc::now().to_rfc3339()],
        )?;
        if changed == 0 {
            return Err(user_not_found());
        }
        self.get_user(id)
    }

    fn get_user(&self, id: &str) -> AppResult<UserSummary> {
        self.list_users()?
            .into_iter()
            .find(|user| user.id == id)
            .ok_or_else(user_not_found)
    }

    pub fn export_json(&self, destination: &Path) -> AppResult<JsonExportResult> {
        let document = self.build_json_document()?;
        let counts = json_entity_counts(&document);
        let output = serde_json::to_vec_pretty(&document).map_err(|_| json_write_error())?;
        let temporary =
            destination.with_extension(format!("knowledge-export.json.{}.partial", Uuid::now_v7()));
        let write_result = (|| {
            let mut file = fs::OpenOptions::new()
                .write(true)
                .create_new(true)
                .open(&temporary)
                .map_err(|_| json_write_error())?;
            file.write_all(&output).map_err(|_| json_write_error())?;
            file.sync_all().map_err(|_| json_write_error())
        })();
        if let Err(error) = write_result {
            let _ = fs::remove_file(&temporary);
            return Err(error);
        }
        let previous = destination.with_extension(format!(
            "knowledge-export.json.{}.previous.partial",
            Uuid::now_v7()
        ));
        let had_previous = destination.exists();
        if had_previous {
            fs::rename(destination, &previous).map_err(|_| json_write_error())?;
        }
        if fs::rename(&temporary, destination).is_err() {
            let _ = fs::remove_file(&temporary);
            if had_previous {
                let _ = fs::rename(&previous, destination);
            }
            return Err(json_write_error());
        }
        if had_previous {
            let _ = fs::remove_file(&previous);
        }
        Ok(JsonExportResult {
            destination_path: destination.display().to_string(),
            counts,
        })
    }

    fn build_json_document(&self) -> AppResult<KnowledgeJsonDocument> {
        let categories = {
            let mut statement = self.connection.prepare(
                r#"
                SELECT id, management_code, parent_id, name, description, sort_order,
                       created_at, updated_at
                  FROM categories
                 ORDER BY depth, sort_order, id
                "#,
            )?;
            let rows = statement.query_map([], |row| {
                Ok(JsonCategoryData {
                    id: row.get(0)?,
                    management_code: row.get(1)?,
                    parent_id: row.get(2)?,
                    name: row.get(3)?,
                    description: row.get(4)?,
                    sort_order: row.get(5)?,
                    created_at: row.get(6)?,
                    updated_at: row.get(7)?,
                })
            })?;
            rows.collect::<Result<Vec<_>, _>>()?
        };
        let tags = {
            let mut statement = self
                .connection
                .prepare("SELECT id, name FROM tags ORDER BY normalized_name, id")?;
            let rows = statement.query_map([], |row| {
                Ok(JsonTagData {
                    id: row.get(0)?,
                    name: row.get(1)?,
                })
            })?;
            rows.collect::<Result<Vec<_>, _>>()?
        };
        let synonym_groups = self
            .list_synonym_groups()?
            .into_iter()
            .map(|group| JsonSynonymGroupData {
                id: group.id,
                display_name: group.display_name,
                terms: group.terms,
            })
            .collect();
        let article_ids = {
            let mut statement = self
                .connection
                .prepare("SELECT id FROM articles ORDER BY created_at, management_code, id")?;
            let rows = statement.query_map([], |row| row.get(0))?;
            rows.collect::<Result<Vec<String>, _>>()?
        };
        let mut articles = Vec::with_capacity(article_ids.len());
        for article_id in article_ids {
            let article = self.get_article(&article_id)?;
            let management_code: String = self.connection.query_row(
                "SELECT management_code FROM articles WHERE id = ?1",
                [&article_id],
                |row| row.get(0),
            )?;
            let tag_ids = {
                let mut statement = self.connection.prepare(
                    "SELECT tag_id FROM article_tags WHERE article_id = ?1 ORDER BY tag_id",
                )?;
                let rows = statement.query_map([&article_id], |row| row.get(0))?;
                rows.collect::<Result<Vec<String>, _>>()?
            };
            articles.push(JsonArticleData {
                id: article.id,
                management_code,
                category_id: article.category_id,
                title: article.title,
                summary: article.summary,
                body_doc: crate::services::rich_content::without_images(&article.body_doc)?,
                status: article.status,
                importance: article.importance,
                new_badge_until: article.new_badge_until,
                updated_badge_until: article.updated_badge_until,
                is_hidden: article.is_hidden,
                created_at: article.created_at,
                updated_at: article.updated_at,
                deleted_at: article.deleted_at,
                symptoms: article.symptoms,
                causes: article.causes,
                targets: article.targets,
                error_codes: article.error_codes,
                procedures: article.procedures,
                cautions: article.cautions,
                tag_ids,
                search_terms: article.search_terms,
            });
        }
        let relations = {
            let mut statement = self.connection.prepare(
                "SELECT source_article_id, target_article_id, sort_order FROM article_relations ORDER BY source_article_id, target_article_id",
            )?;
            let rows = statement.query_map([], |row| {
                Ok(JsonArticleRelationData {
                    source_article_id: row.get(0)?,
                    target_article_id: row.get(1)?,
                    sort_order: row.get(2)?,
                })
            })?;
            rows.collect::<Result<Vec<_>, _>>()?
        };
        let merge_relations = {
            let mut statement = self.connection.prepare(
                "SELECT source_article_id, target_article_id, source_updated_at, merged_at FROM article_merge_relations ORDER BY source_article_id",
            )?;
            let rows = statement.query_map([], |row| {
                Ok(JsonMergeRelationData {
                    source_article_id: row.get(0)?,
                    target_article_id: row.get(1)?,
                    source_updated_at: row.get(2)?,
                    merged_at: row.get(3)?,
                })
            })?;
            rows.collect::<Result<Vec<_>, _>>()?
        };
        Ok(KnowledgeJsonDocument {
            format_version: 1,
            exported_at: Utc::now().to_rfc3339(),
            categories,
            tags,
            synonym_groups,
            articles,
            relations,
            merge_relations,
        })
    }

    pub fn inspect_json(&self, source: &Path) -> AppResult<JsonImportPreview> {
        let (file_sha256, document) = read_json_document(source)?;
        if document.format_version != 1 {
            return Err(AppError::new(
                "JSON-003",
                format!(
                    "JSON形式版{}には対応していません。",
                    document.format_version
                ),
                "このバージョンのKnowledgeAppから書き出した形式版1のファイルを使用してください。",
            ));
        }
        let errors = self.validate_json_document(&document)?;
        let current = self.build_json_document()?;
        let (create_count, update_count, unchanged_count) = json_action_counts(&document, &current);
        Ok(JsonImportPreview {
            source_path: source.display().to_string(),
            file_sha256,
            counts: json_entity_counts(&document),
            create_count,
            update_count,
            unchanged_count,
            error_count: errors.len(),
            errors,
        })
    }

    fn validate_json_document(&self, document: &KnowledgeJsonDocument) -> AppResult<Vec<String>> {
        let mut errors = Vec::new();
        if chrono::DateTime::parse_from_rfc3339(&document.exported_at).is_err() {
            errors.push("exportedAtはRFC 3339形式の日時にしてください。".to_owned());
        }

        let category_ids = document
            .categories
            .iter()
            .map(|category| category.id.clone())
            .collect::<HashSet<_>>();
        let article_ids = document
            .articles
            .iter()
            .map(|article| article.id.clone())
            .collect::<HashSet<_>>();
        let tag_ids = document
            .tags
            .iter()
            .map(|tag| tag.id.clone())
            .collect::<HashSet<_>>();

        validate_unique_json_ids(
            document
                .categories
                .iter()
                .map(|category| category.id.as_str()),
            "分類",
            &mut errors,
        );
        validate_unique_json_ids(
            document.articles.iter().map(|article| article.id.as_str()),
            "FAQ",
            &mut errors,
        );
        validate_unique_json_ids(
            document.tags.iter().map(|tag| tag.id.as_str()),
            "タグ",
            &mut errors,
        );
        validate_unique_json_ids(
            document
                .synonym_groups
                .iter()
                .map(|group| group.id.as_str()),
            "同義語グループ",
            &mut errors,
        );

        let depths = json_category_depths(document, &mut errors);
        let mut sibling_names = HashSet::new();
        let mut management_codes = HashSet::new();
        for category in &document.categories {
            if Uuid::parse_str(&category.id).is_err() {
                errors.push(format!(
                    "分類「{}」のIDがUUIDではありません。",
                    category.name
                ));
            }
            if !valid_management_code(&category.management_code, "CAT-")
                || !management_codes.insert(category.management_code.to_ascii_uppercase())
            {
                errors.push(format!(
                    "分類「{}」の管理IDが不正または重複しています。",
                    category.name
                ));
            }
            if validate_category_name(&category.name).is_err()
                || validate_category_description(&category.description).is_err()
            {
                errors.push(format!(
                    "分類「{}」の名前または説明が不正です。",
                    category.name
                ));
            }
            if category.sort_order < 0 {
                errors.push(format!(
                    "分類「{}」の並び順は0以上にしてください。",
                    category.name
                ));
            }
            if category
                .parent_id
                .as_ref()
                .is_some_and(|parent| !category_ids.contains(parent))
            {
                errors.push(format!(
                    "分類「{}」の親分類がJSON内にありません。",
                    category.name
                ));
            }
            if chrono::DateTime::parse_from_rfc3339(&category.created_at).is_err()
                || chrono::DateTime::parse_from_rfc3339(&category.updated_at).is_err()
            {
                errors.push(format!("分類「{}」の日時が不正です。", category.name));
            }
            if !sibling_names.insert((category.parent_id.clone(), normalize(&category.name))) {
                errors.push(format!(
                    "同じ親の下に分類「{}」が重複しています。",
                    category.name
                ));
            }
            if depths.get(&category.id).copied().unwrap_or(6) > 5 {
                errors.push(format!("分類「{}」が5階層を超えています。", category.name));
            }
        }

        let mut normalized_tags = HashSet::new();
        for tag in &document.tags {
            if Uuid::parse_str(&tag.id).is_err()
                || tag.name.trim().is_empty()
                || tag.name.chars().count() > 100
                || tag.name.contains('\r')
                || tag.name.contains('\n')
                || !normalized_tags.insert(normalize(&tag.name))
            {
                errors.push(format!("タグ「{}」が不正または重複しています。", tag.name));
            }
        }

        let mut synonym_terms: HashMap<String, String> = HashMap::new();
        for group in &document.synonym_groups {
            if Uuid::parse_str(&group.id).is_err() {
                errors.push(format!(
                    "同義語グループ「{}」のIDがUUIDではありません。",
                    group.display_name
                ));
            }
            match validate_synonym_values(&group.display_name, &group.terms) {
                Ok((_, values)) => {
                    for (_, normalized) in values {
                        if let Some(other) = synonym_terms.insert(normalized, group.id.clone())
                            && other != group.id
                        {
                            errors.push(format!(
                                "同じ同義語が複数のグループに登録されています（{}）。",
                                group.display_name
                            ));
                        }
                    }
                }
                Err(error) => errors.push(format!(
                    "同義語グループ「{}」: {}",
                    group.display_name, error.message
                )),
            }
        }

        for article in &document.articles {
            if Uuid::parse_str(&article.id).is_err() {
                errors.push(format!(
                    "FAQ「{}」のIDがUUIDではありません。",
                    article.title
                ));
            }
            if !valid_management_code(&article.management_code, "FAQ-")
                || !management_codes.insert(article.management_code.to_ascii_uppercase())
            {
                errors.push(format!(
                    "FAQ「{}」の管理IDが不正または重複しています。",
                    article.title
                ));
            }
            if !category_ids.contains(&article.category_id) {
                errors.push(format!(
                    "FAQ「{}」の分類がJSON内にありません。",
                    article.title
                ));
            }
            if article.title.trim().is_empty()
                || article.title.chars().count() > 200
                || article.summary.chars().count() > 500
                || !matches!(article.status.as_str(), "draft" | "published" | "archived")
                || !(1..=3).contains(&article.importance)
            {
                errors.push(format!("FAQ「{}」の基本項目が不正です。", article.title));
            }
            let rich_content = crate::services::rich_content::validate_and_extract_with_attachments(
                &article.body_doc,
            );
            match rich_content {
                Ok(content) if content.attachments.is_empty() => {
                    if article.status == "published"
                        && (article.summary.trim().is_empty()
                            || content.plain_text.trim().is_empty())
                    {
                        errors.push(format!(
                            "公開FAQ「{}」には概要と画像以外の回答本文が必要です。",
                            article.title
                        ));
                    }
                }
                Ok(_) => errors.push(format!(
                    "FAQ「{}」に画像参照があります。JSONには画像を含められません。",
                    article.title
                )),
                Err(error) => errors.push(format!(
                    "FAQ「{}」の回答形式が安全ではありません（{}）。",
                    article.title, error.message
                )),
            }
            if validate_json_date(article.new_badge_until.as_deref()).is_err()
                || validate_json_date(article.updated_badge_until.as_deref()).is_err()
                || chrono::DateTime::parse_from_rfc3339(&article.created_at).is_err()
                || chrono::DateTime::parse_from_rfc3339(&article.updated_at).is_err()
                || article
                    .deleted_at
                    .as_deref()
                    .is_some_and(|date| chrono::DateTime::parse_from_rfc3339(date).is_err())
            {
                errors.push(format!(
                    "FAQ「{}」の日付または日時が不正です。",
                    article.title
                ));
            }
            for tag_id in &article.tag_ids {
                if !tag_ids.contains(tag_id) {
                    errors.push(format!(
                        "FAQ「{}」に未定義のタグIDがあります。",
                        article.title
                    ));
                }
            }
            if validate_json_article_details(article).is_err() {
                errors.push(format!("FAQ「{}」の検索情報が不正です。", article.title));
            }
        }

        let mut relation_keys = HashSet::new();
        for relation in &document.relations {
            let key = if relation.source_article_id < relation.target_article_id {
                (&relation.source_article_id, &relation.target_article_id)
            } else {
                (&relation.target_article_id, &relation.source_article_id)
            };
            if relation.source_article_id == relation.target_article_id
                || !article_ids.contains(&relation.source_article_id)
                || !article_ids.contains(&relation.target_article_id)
                || !relation_keys.insert((key.0.clone(), key.1.clone()))
            {
                errors.push("関連FAQに自己参照、未定義ID、または重複があります。".to_owned());
            }
        }
        let mut merge_sources = HashSet::new();
        for relation in &document.merge_relations {
            if relation.source_article_id == relation.target_article_id
                || !article_ids.contains(&relation.source_article_id)
                || !article_ids.contains(&relation.target_article_id)
                || !merge_sources.insert(relation.source_article_id.clone())
                || chrono::DateTime::parse_from_rfc3339(&relation.source_updated_at).is_err()
                || chrono::DateTime::parse_from_rfc3339(&relation.merged_at).is_err()
            {
                errors.push(
                    "統合関係に自己参照、未定義ID、重複、または不正日時があります。".to_owned(),
                );
            }
        }
        self.validate_json_database_collisions(document, &mut errors)?;
        errors.sort();
        errors.dedup();
        Ok(errors)
    }

    fn validate_json_database_collisions(
        &self,
        document: &KnowledgeJsonDocument,
        errors: &mut Vec<String>,
    ) -> AppResult<()> {
        let mut category_state: HashMap<String, (Option<String>, String, String)> = HashMap::new();
        {
            let mut statement = self.connection.prepare(
                "SELECT id, parent_id, normalized_name, management_code FROM categories",
            )?;
            let rows = statement.query_map([], |row| {
                Ok((row.get(0)?, row.get(1)?, row.get(2)?, row.get(3)?))
            })?;
            for row in rows {
                let (id, parent_id, normalized_name, management_code) = row?;
                category_state.insert(id, (parent_id, normalized_name, management_code));
            }
        }
        for category in &document.categories {
            if let Some((_, _, current_code)) = category_state.get(&category.id)
                && !current_code.eq_ignore_ascii_case(&category.management_code)
            {
                errors.push(format!(
                    "分類ID {} は別の管理IDで登録済みです。",
                    category.id
                ));
            }
            category_state.insert(
                category.id.clone(),
                (
                    category.parent_id.clone(),
                    normalize(&category.name),
                    category.management_code.to_ascii_uppercase(),
                ),
            );
        }
        let mut category_names = HashSet::new();
        let mut category_codes = HashSet::new();
        for (parent_id, normalized_name, management_code) in category_state.values() {
            if !category_names.insert((parent_id.clone(), normalized_name.clone())) {
                errors.push("取込後に同じ親の下で分類名が重複します。".to_owned());
            }
            if !category_codes.insert(management_code.to_ascii_uppercase()) {
                errors.push("分類管理IDが既存分類と衝突します。".to_owned());
            }
        }

        let mut article_codes: HashMap<String, String> = HashMap::new();
        {
            let mut statement = self
                .connection
                .prepare("SELECT id, management_code FROM articles")?;
            let rows = statement.query_map([], |row| Ok((row.get(0)?, row.get(1)?)))?;
            for row in rows {
                let (id, management_code) = row?;
                article_codes.insert(id, management_code);
            }
        }
        for article in &document.articles {
            if let Some(current_code) = article_codes.get(&article.id)
                && !current_code.eq_ignore_ascii_case(&article.management_code)
            {
                errors.push(format!(
                    "FAQ ID {} は別の管理IDで登録済みです。",
                    article.id
                ));
            }
            article_codes.insert(
                article.id.clone(),
                article.management_code.to_ascii_uppercase(),
            );
        }
        let mut unique_article_codes = HashSet::new();
        for management_code in article_codes.values() {
            if !unique_article_codes.insert(management_code.to_ascii_uppercase()) {
                errors.push("FAQ管理IDが既存FAQと衝突します。".to_owned());
            }
        }

        let mut tag_state: HashMap<String, String> = HashMap::new();
        {
            let mut statement = self
                .connection
                .prepare("SELECT id, normalized_name FROM tags")?;
            let rows = statement.query_map([], |row| Ok((row.get(0)?, row.get(1)?)))?;
            for row in rows {
                let (id, normalized_name) = row?;
                tag_state.insert(id, normalized_name);
            }
        }
        for tag in &document.tags {
            tag_state.insert(tag.id.clone(), normalize(&tag.name));
        }
        let mut tag_names = HashSet::new();
        for normalized_name in tag_state.values() {
            if !tag_names.insert(normalized_name.clone()) {
                errors.push("タグ名が既存タグと衝突します。".to_owned());
            }
        }

        let imported_synonym_ids = document
            .synonym_groups
            .iter()
            .map(|group| group.id.as_str())
            .collect::<HashSet<_>>();
        let mut term_owners: HashMap<String, String> = HashMap::new();
        {
            let mut statement = self
                .connection
                .prepare("SELECT group_id, normalized_term FROM synonyms")?;
            let rows = statement.query_map([], |row| Ok((row.get(0)?, row.get(1)?)))?;
            for row in rows {
                let (group_id, normalized_term): (String, String) = row?;
                if !imported_synonym_ids.contains(group_id.as_str()) {
                    term_owners.insert(normalized_term, group_id);
                }
            }
        }
        for group in &document.synonym_groups {
            if let Ok((_, values)) = validate_synonym_values(&group.display_name, &group.terms) {
                for (_, normalized) in values {
                    if term_owners.insert(normalized, group.id.clone()).is_some() {
                        errors.push("同義語が既存の別グループと衝突します。".to_owned());
                    }
                }
            }
        }
        Ok(())
    }

    pub fn import_json(
        &mut self,
        source: &Path,
        expected_file_sha256: &str,
        actor_user_id: &str,
        safety_backup_path: String,
    ) -> AppResult<JsonImportResult> {
        let preview = self.inspect_json(source)?;
        if preview.file_sha256 != expected_file_sha256 {
            return Err(AppError::new(
                "JSON-007",
                "確認後にJSONファイルが変更されています。",
                "JSONをもう一度プレビューしてから取り込んでください。",
            ));
        }
        if preview.error_count > 0 {
            return Err(AppError::new(
                "JSON-004",
                "エラーがあるためJSONを取り込めません。",
                "プレビューに表示された内容を修正し、もう一度選択してください。",
            ));
        }
        let (_, document) = read_json_document(source)?;
        let category_depths = json_category_depths(&document, &mut Vec::new());
        let transaction = self.connection.transaction()?;

        for category in &document.categories {
            let exists: bool = transaction.query_row(
                "SELECT EXISTS(SELECT 1 FROM categories WHERE id = ?1)",
                [&category.id],
                |row| row.get(0),
            )?;
            if exists {
                transaction.execute(
                    "UPDATE categories SET normalized_name = ?2 WHERE id = ?1",
                    params![category.id, format!("__json_import_{}", category.id)],
                )?;
            }
        }
        let mut categories = document.categories.iter().collect::<Vec<_>>();
        categories.sort_by_key(|category| category_depths.get(&category.id).copied().unwrap_or(6));
        for category in categories {
            let depth = category_depths.get(&category.id).copied().unwrap_or(1);
            transaction.execute(
                r#"
                INSERT INTO categories(
                    id, management_code, parent_id, name, normalized_name, description,
                    depth, sort_order, created_at, updated_at
                ) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10)
                ON CONFLICT(id) DO UPDATE SET
                    parent_id = excluded.parent_id, name = excluded.name,
                    normalized_name = excluded.normalized_name, description = excluded.description,
                    depth = excluded.depth, sort_order = excluded.sort_order,
                    updated_at = excluded.updated_at
                "#,
                params![
                    category.id,
                    category.management_code.to_ascii_uppercase(),
                    category.parent_id,
                    category.name.trim(),
                    normalize(&category.name),
                    category.description.trim(),
                    depth,
                    category.sort_order,
                    category.created_at,
                    category.updated_at,
                ],
            )?;
        }

        for tag in &document.tags {
            let exists: bool = transaction.query_row(
                "SELECT EXISTS(SELECT 1 FROM tags WHERE id = ?1)",
                [&tag.id],
                |row| row.get(0),
            )?;
            if exists {
                transaction.execute(
                    "UPDATE tags SET normalized_name = ?2 WHERE id = ?1",
                    params![tag.id, format!("__json_import_{}", tag.id)],
                )?;
            }
        }
        let now = Utc::now().to_rfc3339();
        for tag in &document.tags {
            transaction.execute(
                r#"
                INSERT INTO tags(id, name, normalized_name, created_at, updated_at)
                VALUES (?1, ?2, ?3, ?4, ?4)
                ON CONFLICT(id) DO UPDATE SET
                    name = excluded.name, normalized_name = excluded.normalized_name,
                    updated_at = excluded.updated_at
                "#,
                params![tag.id, tag.name.trim(), normalize(&tag.name), now],
            )?;
        }

        for group in &document.synonym_groups {
            let (display_name, values) =
                validate_synonym_values(&group.display_name, &group.terms)?;
            transaction.execute(
                r#"
                INSERT INTO synonym_groups(id, display_name, created_at, updated_at)
                VALUES (?1, ?2, ?3, ?3)
                ON CONFLICT(id) DO UPDATE SET
                    display_name = excluded.display_name, updated_at = excluded.updated_at
                "#,
                params![group.id, display_name, now],
            )?;
            transaction.execute("DELETE FROM synonyms WHERE group_id = ?1", [&group.id])?;
            for (term, normalized) in values {
                transaction.execute(
                    "INSERT INTO synonyms(id, group_id, term, normalized_term) VALUES (?1, ?2, ?3, ?4)",
                    params![Uuid::now_v7().to_string(), group.id, term, normalized],
                )?;
            }
        }

        let tag_names = document
            .tags
            .iter()
            .map(|tag| (tag.id.as_str(), tag.name.clone()))
            .collect::<HashMap<_, _>>();
        for article in &document.articles {
            let rich_content =
                crate::services::rich_content::validate_and_extract_with_attachments(
                    &article.body_doc,
                )?;
            if !rich_content.attachments.is_empty() {
                return Err(AppError::new(
                    "JSON-004",
                    "画像参照を含むJSONは取り込めません。",
                    "KnowledgeAppから画像を除外して再度書き出してください。",
                ));
            }
            let body_doc_json = serde_json::to_string(&article.body_doc).map_err(|_| {
                AppError::new(
                    "JSON-004",
                    "FAQ回答を保存形式へ変換できませんでした。",
                    "JSONの回答本文を確認してください。",
                )
            })?;
            let exists: bool = transaction.query_row(
                "SELECT EXISTS(SELECT 1 FROM articles WHERE id = ?1)",
                [&article.id],
                |row| row.get(0),
            )?;
            if exists {
                transaction.execute(
                    r#"
                    UPDATE articles
                       SET category_id = ?2, title = ?3, normalized_title = ?4, summary = ?5,
                           body_doc_json = ?6, body_format_version = 2, body_plain_text = ?7,
                           status = ?8, importance = ?9, new_badge_until = ?10,
                           updated_badge_until = ?11, is_hidden = ?12, updated_at = ?13,
                           deleted_at = ?14, updated_by_user_id = ?15
                     WHERE id = ?1
                    "#,
                    params![
                        article.id,
                        article.category_id,
                        article.title.trim(),
                        normalize(&article.title),
                        article.summary.trim(),
                        body_doc_json,
                        rich_content.plain_text,
                        article.status,
                        article.importance,
                        article.new_badge_until,
                        article.updated_badge_until,
                        article.is_hidden,
                        article.updated_at,
                        article.deleted_at,
                        actor_user_id,
                    ],
                )?;
            } else {
                transaction.execute(
                    r#"
                    INSERT INTO articles(
                        id, management_code, category_id, title, normalized_title, summary,
                        body_doc_json, body_format_version, body_plain_text, status, importance,
                        new_badge_until, updated_badge_until, is_hidden, created_at, updated_at,
                        deleted_at, created_by_user_id, updated_by_user_id
                    ) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, 2, ?8, ?9, ?10, ?11, ?12, ?13, ?14, ?15, ?16, ?17, ?17)
                    "#,
                    params![
                        article.id,
                        article.management_code.to_ascii_uppercase(),
                        article.category_id,
                        article.title.trim(),
                        normalize(&article.title),
                        article.summary.trim(),
                        body_doc_json,
                        rich_content.plain_text,
                        article.status,
                        article.importance,
                        article.new_badge_until,
                        article.updated_badge_until,
                        article.is_hidden,
                        article.created_at,
                        article.updated_at,
                        article.deleted_at,
                        actor_user_id,
                    ],
                )?;
            }
            let tags = article
                .tag_ids
                .iter()
                .filter_map(|id| tag_names.get(id.as_str()).cloned())
                .collect::<Vec<_>>();
            let details = ArticleDetailsRecord {
                symptoms: &article.symptoms,
                causes: &article.causes,
                targets: &article.targets,
                error_codes: &article.error_codes,
                procedures: &article.procedures,
                cautions: &article.cautions,
                tags: &tags,
                search_terms: &article.search_terms,
                related_article_ids: &[],
            };
            replace_article_details(&transaction, &article.id, &details)?;
            update_article_search_index(
                &transaction,
                &article.id,
                article.title.trim(),
                article.summary.trim(),
                &rich_content.plain_text,
                Some(&details),
            )?;
        }

        for relation in &document.relations {
            let (source, target) = if relation.source_article_id < relation.target_article_id {
                (&relation.source_article_id, &relation.target_article_id)
            } else {
                (&relation.target_article_id, &relation.source_article_id)
            };
            transaction.execute(
                "INSERT INTO article_relations(source_article_id, target_article_id, sort_order) VALUES (?1, ?2, ?3)",
                params![source, target, relation.sort_order],
            )?;
        }
        for article in &document.articles {
            transaction.execute(
                "DELETE FROM article_merge_relations WHERE source_article_id = ?1",
                [&article.id],
            )?;
        }
        for relation in &document.merge_relations {
            transaction.execute(
                "INSERT INTO article_merge_relations(source_article_id, target_article_id, source_updated_at, merged_at) VALUES (?1, ?2, ?3, ?4)",
                params![relation.source_article_id, relation.target_article_id, relation.source_updated_at, relation.merged_at],
            )?;
        }
        for (entity_type, table) in [("category", "categories"), ("article", "articles")] {
            transaction.execute(
                &format!(
                    "UPDATE management_code_sequences SET next_value = MAX(next_value, (SELECT COALESCE(MAX(CAST(SUBSTR(management_code, 5) AS INTEGER)), 0) + 1 FROM {table})) WHERE entity_type = ?1"
                ),
                [entity_type],
            )?;
        }
        transaction.commit()?;
        Ok(JsonImportResult {
            source_path: source.display().to_string(),
            safety_backup_path,
            created_count: preview.create_count,
            updated_count: preview.update_count,
            unchanged_count: preview.unchanged_count,
        })
    }

    pub fn export_faq_csv(&self, destination: &Path) -> AppResult<CsvExportResult> {
        let mut statement = self.connection.prepare(
            r#"
            WITH RECURSIVE category_paths(id, management_code, path) AS (
                SELECT id, management_code, name FROM categories WHERE parent_id IS NULL
                UNION ALL
                SELECT child.id, child.management_code, parent.path || ' > ' || child.name
                  FROM categories child
                  JOIN category_paths parent ON child.parent_id = parent.id
            )
            SELECT a.management_code, category_paths.management_code, category_paths.path, a.title, a.summary,
                   a.body_plain_text, a.status, a.importance, a.new_badge_until,
                   a.updated_badge_until, a.is_hidden, creator.display_name,
                   updater.display_name, a.created_at, a.updated_at
              FROM articles a
              JOIN category_paths ON category_paths.id = a.category_id
              JOIN users creator ON creator.id = a.created_by_user_id
              JOIN users updater ON updater.id = a.updated_by_user_id
             WHERE a.deleted_at IS NULL
             ORDER BY category_paths.path, a.title, a.management_code
            "#,
        )?;
        let rows = statement.query_map([], |row| {
            Ok((
                row.get::<_, String>(0)?,
                row.get::<_, String>(1)?,
                row.get::<_, String>(2)?,
                row.get::<_, String>(3)?,
                row.get::<_, String>(4)?,
                row.get::<_, String>(5)?,
                row.get::<_, String>(6)?,
                row.get::<_, i64>(7)?,
                row.get::<_, Option<String>>(8)?,
                row.get::<_, Option<String>>(9)?,
                row.get::<_, bool>(10)?,
                row.get::<_, String>(11)?,
                row.get::<_, String>(12)?,
                row.get::<_, String>(13)?,
                row.get::<_, String>(14)?,
            ))
        })?;

        let mut writer = csv::WriterBuilder::new()
            .terminator(csv::Terminator::CRLF)
            .from_writer(Vec::new());
        writer
            .write_record(crate::services::csv_transfer::HEADERS)
            .map_err(csv_write_error)?;
        let mut exported_count = 0;
        for row in rows {
            let (
                faq_management_id,
                category_management_id,
                category_path,
                title,
                summary,
                body,
                status,
                importance,
                new_badge_until,
                updated_badge_until,
                is_hidden,
                creator,
                updater,
                created_at,
                updated_at,
            ) = row?;
            writer
                .write_record([
                    crate::services::csv_transfer::FORMAT_VERSION.to_owned(),
                    faq_management_id,
                    category_management_id,
                    safe_excel_cell(&category_path),
                    safe_excel_cell(&title),
                    safe_excel_cell(&summary),
                    safe_excel_cell(&body),
                    crate::services::csv_transfer::body_hash(&body),
                    csv_status_label(&status).to_owned(),
                    importance.to_string(),
                    new_badge_until.unwrap_or_default(),
                    updated_badge_until.unwrap_or_default(),
                    if is_hidden { "TRUE" } else { "FALSE" }.to_owned(),
                    safe_excel_cell(&creator),
                    safe_excel_cell(&updater),
                    created_at,
                    updated_at,
                ])
                .map_err(csv_write_error)?;
            exported_count += 1;
        }
        let bytes = writer.into_inner().map_err(|_| csv_write_error(()))?;
        let mut output = Vec::with_capacity(bytes.len() + 3);
        output.extend_from_slice(&[0xEF, 0xBB, 0xBF]);
        output.extend_from_slice(&bytes);
        let temporary = destination.with_extension(format!("{}.partial", Uuid::now_v7()));
        fs::write(&temporary, output).map_err(|_| csv_write_error(()))?;
        let previous = destination.with_extension(format!("{}.previous.partial", Uuid::now_v7()));
        let had_previous = destination.exists();
        if had_previous {
            fs::rename(destination, &previous).map_err(|_| csv_write_error(()))?;
        }
        if fs::rename(&temporary, destination).is_err() {
            let _ = fs::remove_file(&temporary);
            if had_previous {
                let _ = fs::rename(&previous, destination);
            }
            return Err(csv_write_error(()));
        }
        if had_previous {
            let _ = fs::remove_file(&previous);
        }
        Ok(CsvExportResult {
            destination_path: destination.display().to_string(),
            exported_count,
        })
    }

    pub fn inspect_faq_csv(&self, source: &Path) -> AppResult<CsvImportPreview> {
        let (file_sha256, plans) = self.plan_faq_csv(source)?;
        Ok(csv_preview(source, file_sha256, &plans))
    }

    pub fn import_faq_csv(
        &mut self,
        source: &Path,
        expected_file_sha256: &str,
        actor_user_id: &str,
        safety_backup_path: String,
    ) -> AppResult<CsvImportResult> {
        let (file_sha256, plans) = self.plan_faq_csv(source)?;
        if file_sha256 != expected_file_sha256 {
            return Err(AppError::new(
                "CSV-007",
                "確認後にCSVファイルが変更されています。",
                "CSVをもう一度プレビューしてから取り込んでください。",
            ));
        }
        if plans.iter().any(|row| row.action == CsvRowAction::Error) {
            return Err(AppError::new(
                "CSV-004",
                "エラーがあるためCSVを取り込めません。",
                "プレビューに表示された行を修正し、もう一度選択してください。",
            ));
        }

        let transaction = self.connection.transaction()?;
        let now = Utc::now().to_rfc3339();
        let mut created_count = 0;
        let mut updated_count = 0;
        let mut unchanged_count = 0;
        for row in plans {
            match row.action {
                CsvRowAction::Unchanged => {
                    unchanged_count += 1;
                    continue;
                }
                CsvRowAction::Create => created_count += 1,
                CsvRowAction::Update => updated_count += 1,
                CsvRowAction::Error => unreachable!(),
            }
            if row.action == CsvRowAction::Create {
                transaction.execute(
                    r#"
                    INSERT INTO articles(
                        id, category_id, title, normalized_title, summary, body_doc_json,
                        body_format_version, body_plain_text, status, importance, created_at,
                        updated_at, new_badge_until, updated_badge_until, is_hidden,
                        created_by_user_id, updated_by_user_id
                    ) VALUES (?1, ?2, ?3, ?4, ?5, ?6, 2, ?7, ?8, ?9, ?10, ?10, ?11, ?12, ?13, ?14, ?14)
                    "#,
                    params![row.article_id, row.category_id, row.title, normalize(&row.title), row.summary,
                        row.body_doc_json, row.body_plain_text, row.status, row.importance, now,
                        row.new_badge_until, row.updated_badge_until, row.is_hidden, actor_user_id],
                )?;
            } else {
                transaction.execute(
                    r#"
                    UPDATE articles
                       SET category_id = ?2, title = ?3, normalized_title = ?4, summary = ?5,
                           body_doc_json = ?6, body_format_version = 2, body_plain_text = ?7,
                           status = ?8, importance = ?9, new_badge_until = ?10,
                           updated_badge_until = ?11, is_hidden = ?12, updated_at = ?13,
                           updated_by_user_id = ?14
                     WHERE id = ?1 AND deleted_at IS NULL
                    "#,
                    params![
                        row.article_id,
                        row.category_id,
                        row.title,
                        normalize(&row.title),
                        row.summary,
                        row.body_doc_json,
                        row.body_plain_text,
                        row.status,
                        row.importance,
                        row.new_badge_until,
                        row.updated_badge_until,
                        row.is_hidden,
                        now,
                        actor_user_id
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
                    row.article_id,
                    normalize(&row.title),
                    normalize(&row.summary),
                    normalize(&row.body_plain_text)
                ],
            )?;
            transaction.execute(
                "DELETE FROM article_search_fts WHERE article_id = ?1",
                [&row.article_id],
            )?;
            transaction.execute(
                "INSERT INTO article_search_fts(article_id, title, summary, body) VALUES (?1, ?2, ?3, ?4)",
                params![row.article_id, normalize(&row.title), normalize(&row.summary), normalize(&row.body_plain_text)],
            )?;
        }
        transaction.commit()?;
        Ok(CsvImportResult {
            source_path: source.display().to_string(),
            safety_backup_path,
            created_count,
            updated_count,
            unchanged_count,
        })
    }

    fn plan_faq_csv(&self, source: &Path) -> AppResult<(String, Vec<CsvImportPlanRow>)> {
        let bytes =
            fs::read(source).map_err(|_| csv_read_error("CSVファイルを読み込めませんでした。"))?;
        if bytes.len() > 50 * 1024 * 1024 {
            return Err(csv_read_error("CSVファイルが50MBを超えています。"));
        }
        let mut hasher = Sha256::new();
        hasher.update(&bytes);
        let file_sha256 = hasher
            .finalize()
            .iter()
            .map(|byte| format!("{byte:02x}"))
            .collect();
        let content = bytes.strip_prefix(&[0xEF, 0xBB, 0xBF]).unwrap_or(&bytes);
        let mut reader = csv::ReaderBuilder::new()
            .flexible(false)
            .from_reader(content);
        let headers = reader
            .headers()
            .map_err(|_| csv_read_error("CSVの見出し行を読み込めませんでした。"))?;
        if headers.iter().ne(crate::services::csv_transfer::HEADERS) {
            return Err(AppError::new(
                "CSV-002",
                "CSVの見出しがKnowledgeApp形式と一致しません。",
                "KnowledgeAppから書き出したCSVを使用し、列名や列順は変更しないでください。",
            ));
        }
        let category_maps = self.category_csv_maps()?;
        let mut seen_ids = HashSet::new();
        let mut plans = Vec::new();
        for (index, record) in reader.records().enumerate() {
            let line = index + 2;
            match record {
                Ok(record) => plans.push(self.plan_csv_record(
                    line,
                    &record,
                    &category_maps,
                    &mut seen_ids,
                )?),
                Err(_) => plans.push(csv_error_plan(
                    line,
                    "CSVの列数または引用符が正しくありません。",
                )),
            }
        }
        Ok((file_sha256, plans))
    }

    fn category_csv_maps(&self) -> AppResult<(HashMap<String, String>, HashMap<String, String>)> {
        let mut statement = self.connection.prepare(
            r#"WITH RECURSIVE paths(id, management_code, path) AS (
                SELECT id, management_code, name FROM categories WHERE parent_id IS NULL
                UNION ALL SELECT c.id, c.management_code, p.path || ' > ' || c.name FROM categories c JOIN paths p ON c.parent_id = p.id
            ) SELECT id, management_code, path FROM paths"#,
        )?;
        let rows = statement.query_map([], |row| {
            Ok((
                row.get::<_, String>(0)?,
                row.get::<_, String>(1)?,
                row.get::<_, String>(2)?,
            ))
        })?;
        let mut by_management_id = HashMap::new();
        let mut by_path = HashMap::new();
        for row in rows {
            let (id, management_id, path) = row?;
            by_path.insert(path, id.clone());
            by_management_id.insert(management_id, id);
        }
        Ok((by_management_id, by_path))
    }

    fn plan_csv_record(
        &self,
        line: usize,
        record: &csv::StringRecord,
        categories: &(HashMap<String, String>, HashMap<String, String>),
        seen_ids: &mut HashSet<String>,
    ) -> AppResult<CsvImportPlanRow> {
        if record.get(0) != Some(crate::services::csv_transfer::FORMAT_VERSION) {
            return Ok(csv_error_plan(line, "対応していない形式バージョンです。"));
        }
        let faq_management_id = record
            .get(1)
            .unwrap_or_default()
            .trim()
            .to_ascii_uppercase();
        if !faq_management_id.is_empty() && !seen_ids.insert(faq_management_id.clone()) {
            return Ok(csv_error_plan(line, "同じFAQ管理IDがCSV内に複数あります。"));
        }
        let category_management_id = record
            .get(2)
            .unwrap_or_default()
            .trim()
            .to_ascii_uppercase();
        let category_path_cell = unsafed_excel_cell(record.get(3).unwrap_or_default()).trim();
        let category_id = if !category_path_cell.is_empty() {
            categories.1.get(category_path_cell).cloned()
        } else {
            categories.0.get(&category_management_id).cloned()
        };
        let Some(category_id) = category_id else {
            return Ok(csv_error_plan(
                line,
                "分類管理IDまたは分類パスが現在の分類一覧にありません。",
            ));
        };
        let title = unsafed_excel_cell(record.get(4).unwrap_or_default())
            .trim()
            .to_owned();
        let summary = unsafed_excel_cell(record.get(5).unwrap_or_default())
            .trim()
            .to_owned();
        let body_plain_text = crate::services::csv_transfer::normalize_newlines(
            unsafed_excel_cell(record.get(6).unwrap_or_default()),
        );
        if title.is_empty() || title.chars().count() > 200 {
            return Ok(csv_error_plan(
                line,
                "タイトルは1～200文字で入力してください。",
            ));
        }
        if summary.chars().count() > 500 {
            return Ok(csv_error_plan(
                line,
                "概要は500文字以内で入力してください。",
            ));
        }
        let status = match record.get(8).unwrap_or_default().trim() {
            "下書き" | "draft" => "draft",
            "公開" | "published" => "published",
            "廃止" | "archived" => "archived",
            _ => {
                return Ok(csv_error_plan(
                    line,
                    "状態は「下書き」「公開」「廃止」のいずれかにしてください。",
                ));
            }
        }
        .to_owned();
        let importance = match record.get(9).unwrap_or_default().trim().parse::<i64>() {
            Ok(value @ 1..=3) => value,
            _ => return Ok(csv_error_plan(line, "重要度は1～3で入力してください。")),
        };
        let new_badge_until = match csv_optional_date(record.get(10).unwrap_or_default()) {
            Ok(value) => value,
            Err(message) => return Ok(csv_error_plan(line, message)),
        };
        let updated_badge_until = match csv_optional_date(record.get(11).unwrap_or_default()) {
            Ok(value) => value,
            Err(message) => return Ok(csv_error_plan(line, message)),
        };
        let is_hidden = match record
            .get(12)
            .unwrap_or_default()
            .trim()
            .to_ascii_uppercase()
            .as_str()
        {
            "TRUE" | "1" | "はい" => true,
            "FALSE" | "0" | "いいえ" | "" => false,
            _ => {
                return Ok(csv_error_plan(
                    line,
                    "非表示はTRUEまたはFALSEで入力してください。",
                ));
            }
        };

        let existing = if faq_management_id.is_empty() {
            None
        } else {
            self.existing_csv_article(&faq_management_id)?
        };
        if !faq_management_id.is_empty() && existing.is_none() {
            return Ok(csv_error_plan(
                line,
                "FAQ管理IDに一致する登録中のFAQがありません。",
            ));
        }
        let exported_body_hash = record.get(7).unwrap_or_default().trim();
        let body_will_be_replaced = existing.is_none()
            || crate::services::csv_transfer::body_hash(&body_plain_text) != exported_body_hash;
        if status == "published" && body_will_be_replaced && body_plain_text.trim().is_empty() {
            return Ok(csv_error_plan(
                line,
                "公開FAQの回答本文は空欄にできません。",
            ));
        }
        if let Some(existing) = &existing {
            if status == "published"
                && !body_will_be_replaced
                && existing.body_plain_text.trim().is_empty()
                && !existing.has_attachments
            {
                return Ok(csv_error_plan(
                    line,
                    "公開FAQの回答本文は空欄にできません。",
                ));
            }
            if existing.is_merge_target && (status != "published" || is_hidden) {
                return Ok(csv_error_plan(
                    line,
                    "統合先FAQは公開かつ非表示OFFを維持してください。",
                ));
            }
        }
        let (article_id, body_doc_json, effective_body, action, stale) = match existing {
            None => (
                Uuid::now_v7().to_string(),
                serde_json::to_string(&crate::services::csv_transfer::plain_text_to_document(
                    &body_plain_text,
                ))
                .map_err(|_| csv_read_error("回答本文を変換できませんでした。"))?,
                body_plain_text.clone(),
                CsvRowAction::Create,
                false,
            ),
            Some(existing) => {
                let doc = if body_will_be_replaced {
                    serde_json::to_string(&crate::services::csv_transfer::plain_text_to_document(
                        &body_plain_text,
                    ))
                    .map_err(|_| csv_read_error("回答本文を変換できませんでした。"))?
                } else {
                    existing.body_doc_json.clone()
                };
                let effective_body = if body_will_be_replaced {
                    body_plain_text.clone()
                } else {
                    existing.body_plain_text.clone()
                };
                let changed = category_id != existing.category_id
                    || title != existing.title
                    || summary != existing.summary
                    || effective_body != existing.body_plain_text
                    || status != existing.status
                    || importance != existing.importance
                    || new_badge_until != existing.new_badge_until
                    || updated_badge_until != existing.updated_badge_until
                    || is_hidden != existing.is_hidden;
                let stale =
                    changed && record.get(16).unwrap_or_default().trim() != existing.updated_at;
                (
                    existing.article_id,
                    doc,
                    effective_body,
                    if changed {
                        CsvRowAction::Update
                    } else {
                        CsvRowAction::Unchanged
                    },
                    stale,
                )
            }
        };
        let mut messages = Vec::new();
        if body_will_be_replaced {
            messages.push(
                "回答本文をプレーンテキスト段落へ置換します（表・画像・書式は本文から外れます）。"
                    .to_owned(),
            );
        }
        if stale {
            messages.push(
                "書き出し後に更新されていますが、指定どおりCSVの内容で上書きします。".to_owned(),
            );
        }
        Ok(CsvImportPlanRow {
            line,
            action,
            faq_management_id: (!faq_management_id.is_empty()).then_some(faq_management_id),
            article_id,
            category_id,
            title,
            summary,
            body_plain_text: effective_body,
            body_doc_json,
            body_will_be_replaced,
            status,
            importance,
            new_badge_until,
            updated_badge_until,
            is_hidden,
            stale_update_will_overwrite: stale,
            messages,
        })
    }

    fn existing_csv_article(&self, management_id: &str) -> AppResult<Option<ExistingCsvArticle>> {
        self.connection.query_row(
            r#"SELECT id, category_id, title, summary, body_plain_text, body_doc_json, status, importance,
                      new_badge_until, updated_badge_until, is_hidden, updated_at,
                      EXISTS(SELECT 1 FROM article_attachments WHERE article_id = articles.id),
                      EXISTS(SELECT 1 FROM article_merge_relations WHERE target_article_id = articles.id)
                 FROM articles WHERE management_code = ?1 AND deleted_at IS NULL"#,
            [management_id],
            |row| Ok(ExistingCsvArticle {
                article_id: row.get(0)?, category_id: row.get(1)?, title: row.get(2)?, summary: row.get(3)?,
                body_plain_text: row.get(4)?, body_doc_json: row.get(5)?, status: row.get(6)?,
                importance: row.get(7)?, new_badge_until: row.get(8)?, updated_badge_until: row.get(9)?,
                is_hidden: row.get(10)?, updated_at: row.get(11)?, has_attachments: row.get(12)?,
                is_merge_target: row.get(13)?,
            }),
        ).optional().map_err(AppError::from)
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

    pub fn get_password_policy(&self) -> AppResult<PasswordPolicySettings> {
        let stored: Option<String> = self
            .connection
            .query_row(
                "SELECT value_json FROM app_settings WHERE key = ?1",
                [PASSWORD_POLICY_SETTINGS_KEY],
                |row| row.get(0),
            )
            .optional()?;

        let Some(stored) = stored else {
            return Ok(PasswordPolicySettings::default());
        };

        serde_json::from_str(&stored).map_err(|_| {
            AppError::new(
                "SET-003",
                "パスワード設定を読み込めませんでした。",
                "設定画面で空パスワードの許可設定を選び直して保存してください。",
            )
        })
    }

    pub fn save_password_policy(&self, settings: &PasswordPolicySettings) -> AppResult<()> {
        let value_json = serde_json::to_string(settings).map_err(|_| {
            AppError::new(
                "SET-004",
                "パスワード設定を保存できませんでした。",
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
            params![
                PASSWORD_POLICY_SETTINGS_KEY,
                value_json,
                Utc::now().to_rfc3339()
            ],
        )?;
        Ok(())
    }

    fn ensure_password_allowed(&self, password: &str) -> AppResult<()> {
        if password.is_empty() && !self.get_password_policy()?.allow_empty_passwords {
            return Err(AppError::new(
                "USR-004",
                "空欄のパスワードは現在許可されていません。",
                "1文字以上のパスワードを入力してください。既存利用者のパスワードは変更されません。",
            ));
        }
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

    pub fn reorder_category(
        &mut self,
        id: &str,
        direction: CategoryMoveDirection,
    ) -> AppResult<Vec<Category>> {
        let transaction = self.connection.transaction()?;
        let parent_id: Option<String> = transaction
            .query_row(
                "SELECT parent_id FROM categories WHERE id = ?1",
                [id],
                |row| row.get(0),
            )
            .optional()?
            .ok_or_else(category_not_found)?;
        let siblings = {
            let mut statement = transaction.prepare(
                "SELECT id FROM categories WHERE parent_id IS ?1 ORDER BY sort_order, id",
            )?;
            let rows = statement.query_map([parent_id.as_deref()], |row| row.get(0))?;
            rows.collect::<Result<Vec<String>, _>>()?
        };
        let position = siblings
            .iter()
            .position(|sibling_id| sibling_id == id)
            .ok_or_else(category_not_found)?;
        let target_position = match direction {
            CategoryMoveDirection::Up => position.checked_sub(1),
            CategoryMoveDirection::Down => (position + 1 < siblings.len()).then_some(position + 1),
        };
        let Some(target_position) = target_position else {
            transaction.commit()?;
            return self.list_categories();
        };
        let now = Utc::now().to_rfc3339();
        for (index, sibling_id) in siblings.iter().enumerate() {
            let next_order = if index == position {
                target_position
            } else if index == target_position {
                position
            } else {
                index
            };
            transaction.execute(
                "UPDATE categories SET sort_order = ?2, updated_at = ?3 WHERE id = ?1",
                params![sibling_id, next_order as i64, now],
            )?;
        }
        transaction.commit()?;
        self.list_categories()
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

    #[cfg(test)]
    pub fn save_article(&mut self, article: ArticleRecord<'_>) -> AppResult<Article> {
        self.save_article_as(article, INITIAL_ADMIN_USER_ID)
    }

    #[cfg(test)]
    pub fn save_article_as(
        &mut self,
        article: ArticleRecord<'_>,
        actor_user_id: &str,
    ) -> AppResult<Article> {
        self.save_article_internal(article, None, actor_user_id)
    }

    pub fn save_article_with_details_as(
        &mut self,
        article: ArticleRecord<'_>,
        details: &ArticleDetailsRecord<'_>,
        actor_user_id: &str,
    ) -> AppResult<Article> {
        self.save_article_internal(article, Some(details), actor_user_id)
    }

    fn save_article_internal(
        &mut self,
        article: ArticleRecord<'_>,
        details: Option<&ArticleDetailsRecord<'_>>,
        actor_user_id: &str,
    ) -> AppResult<Article> {
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

        if !article.is_new && (article.status != "published" || article.is_hidden) {
            let has_merge_sources: bool = transaction.query_row(
                "SELECT EXISTS(SELECT 1 FROM article_merge_relations WHERE target_article_id = ?1)",
                [article.id],
                |row| row.get(0),
            )?;
            if has_merge_sources {
                return Err(merge_target_visibility_error());
            }
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
                       is_hidden = ?12, updated_at = ?13, updated_by_user_id = ?14
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
                    now,
                    actor_user_id
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
                    , new_badge_until, updated_badge_until, is_hidden,
                    created_by_user_id, updated_by_user_id
                ) VALUES (?1, ?2, ?3, ?4, ?5, ?6, 2, ?7, ?8, ?9, ?10, ?10, ?11, ?12, ?13, ?14, ?14)
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
                    article.is_hidden,
                    actor_user_id
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

        if let Some(details) = details {
            replace_article_details(&transaction, &id, details)?;
        }
        update_article_search_index(
            &transaction,
            &id,
            article.title,
            article.summary,
            article.body_plain_text,
            details,
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

    pub fn get_codex_merge_publication_context(
        &self,
        target_article_id: &str,
    ) -> AppResult<Option<CodexMergePublicationContext>> {
        let payload: Option<String> = self
            .connection
            .query_row(
                r#"
                SELECT payload_json
                  FROM codex_proposal_history
                 WHERE accepted_article_id = ?1
                   AND proposal_kind = 'merge'
                   AND status = 'accepted'
                 ORDER BY history_id DESC
                 LIMIT 1
                "#,
                [target_article_id],
                |row| row.get(0),
            )
            .optional()?;
        let Some(payload) = payload else {
            return Ok(None);
        };
        let proposal = parse_stored_proposal(payload)?;
        let mut source_articles = Vec::with_capacity(proposal.source_articles.len());
        let mut can_mark_merged = !proposal.source_articles.is_empty();
        let mut all_sources_merged = !proposal.source_articles.is_empty();
        for source in proposal.source_articles {
            let current: Option<CodexMergeSourceRow> = self
                .connection
                .query_row(
                    r#"
                    SELECT article.title, article.status, article.updated_at, article.deleted_at,
                           merge_relation.target_article_id
                      FROM articles article
                      LEFT JOIN article_merge_relations merge_relation
                        ON merge_relation.source_article_id = article.id
                     WHERE article.id = ?1
                    "#,
                    [&source.article_id],
                    |row| {
                        Ok((
                            row.get(0)?,
                            row.get(1)?,
                            row.get(2)?,
                            row.get(3)?,
                            row.get(4)?,
                        ))
                    },
                )
                .optional()?;
            let (title, status, current_updated_at, deleted_at, merge_target_id) = match current {
                Some(current) => (
                    current.0,
                    Some(current.1),
                    Some(current.2),
                    current.3,
                    current.4,
                ),
                None => (source.article_id.clone(), None, None, None, None),
            };
            let is_current = current_updated_at.as_deref()
                == Some(source.source_updated_at.as_str())
                && deleted_at.is_none();
            let is_merged = merge_target_id.as_deref() == Some(target_article_id);
            let has_conflicting_merge = merge_target_id.is_some() && !is_merged;
            can_mark_merged &= is_merged || (is_current && !has_conflicting_merge);
            all_sources_merged &= is_merged;
            source_articles.push(CodexMergeSourcePreview {
                article_id: source.article_id,
                title,
                status,
                source_updated_at: source.source_updated_at,
                current_updated_at,
                deleted_at,
                is_current,
                is_merged,
            });
        }
        Ok(Some(CodexMergePublicationContext {
            target_article_id: target_article_id.to_owned(),
            source_articles,
            can_mark_merged: can_mark_merged && !all_sources_merged,
            all_sources_merged,
        }))
    }

    pub fn requires_new_badge_for_merge_publication(
        &self,
        target_article_id: &str,
    ) -> AppResult<bool> {
        self.connection
            .query_row(
                r#"
                SELECT EXISTS(
                    SELECT 1
                      FROM articles article
                     WHERE article.id = ?1
                       AND article.deleted_at IS NULL
                       AND article.status <> 'published'
                       AND EXISTS (
                           SELECT 1
                             FROM codex_proposal_history history
                            WHERE history.accepted_article_id = article.id
                              AND history.proposal_kind = 'merge'
                              AND history.status = 'accepted'
                       )
                )
                "#,
                [target_article_id],
                |row| row.get(0),
            )
            .map_err(AppError::from)
    }

    pub fn mark_codex_merge_sources(
        &mut self,
        target_article_id: &str,
    ) -> AppResult<MarkCodexMergeSourcesResult> {
        let transaction = self.connection.transaction()?;
        let target: Option<(String, Option<String>, Option<String>, bool)> = transaction
            .query_row(
                "SELECT status, new_badge_until, deleted_at, is_hidden FROM articles WHERE id = ?1",
                [target_article_id],
                |row| Ok((row.get(0)?, row.get(1)?, row.get(2)?, row.get(3)?)),
            )
            .optional()?;
        let Some((target_status, new_badge_until, target_deleted_at, target_is_hidden)) = target
        else {
            return Err(article_not_found());
        };
        if target_deleted_at.is_some() || target_status != "published" || target_is_hidden {
            return Err(AppError::new(
                "CDX-016",
                "統合元FAQを統合済みにする前に、統合FAQを公開して表示してください。",
                "統合FAQの内容を確認し、公開かつ非表示OFFで保存してからもう一度お試しください。",
            ));
        }
        if new_badge_until.is_none() {
            return Err(AppError::new(
                "CDX-016",
                "統合FAQに新着フラグの表示終了日がありません。",
                "統合FAQを編集し、新着フラグと表示終了日を設定してください。",
            ));
        }
        let payload: Option<String> = transaction
            .query_row(
                r#"
                SELECT payload_json
                  FROM codex_proposal_history
                 WHERE accepted_article_id = ?1
                   AND proposal_kind = 'merge'
                   AND status = 'accepted'
                 ORDER BY history_id DESC
                 LIMIT 1
                "#,
                [target_article_id],
                |row| row.get(0),
            )
            .optional()?;
        let proposal = parse_stored_proposal(payload.ok_or_else(|| {
            AppError::new(
                "CDX-016",
                "このFAQの統合元情報が見つかりません。",
                "Codex提案履歴を確認し、元FAQはFAQ管理画面から整理してください。",
            )
        })?)?;
        let now = Utc::now().to_rfc3339();
        let mut marked_count = 0;
        for source in &proposal.source_articles {
            let current: Option<(String, Option<String>, Option<String>)> = transaction
                .query_row(
                    r#"
                    SELECT article.updated_at, article.deleted_at,
                           merge_relation.target_article_id
                      FROM articles article
                      LEFT JOIN article_merge_relations merge_relation
                        ON merge_relation.source_article_id = article.id
                     WHERE article.id = ?1
                    "#,
                    [&source.article_id],
                    |row| Ok((row.get(0)?, row.get(1)?, row.get(2)?)),
                )
                .optional()?;
            let Some((updated_at, deleted_at, merge_target_id)) = current else {
                return Err(AppError::new(
                    "CDX-012",
                    "統合元FAQが見つかりません。",
                    "元FAQは変更せず、FAQ管理画面で現在の状態を確認してください。",
                ));
            };
            if merge_target_id.as_deref() == Some(target_article_id) {
                continue;
            }
            if merge_target_id.is_some() {
                return Err(AppError::new(
                    "CDX-017",
                    "統合元FAQの一部が別のFAQへ統合済みです。",
                    "FAQ管理画面で統合先を確認し、対象を整理してください。",
                ));
            }
            if deleted_at.is_some() || updated_at != source.source_updated_at {
                return Err(AppError::new(
                    "CDX-012",
                    "統合案の承認後に、統合元FAQが変更または削除されています。",
                    "現在の内容を誤って非表示にしないため、自動整理を中止しました。FAQ管理画面で確認してください。",
                ));
            }
            transaction.execute(
                r#"
                INSERT INTO article_merge_relations(
                    source_article_id, target_article_id, source_updated_at, merged_at
                ) VALUES (?1, ?2, ?3, ?4)
                "#,
                params![
                    source.article_id,
                    target_article_id,
                    source.source_updated_at,
                    now,
                ],
            )?;
            marked_count += 1;
        }
        transaction.commit()?;
        Ok(MarkCodexMergeSourcesResult {
            target_article_id: target_article_id.to_owned(),
            marked_count,
        })
    }

    pub fn clear_article_merge(&mut self, source_article_id: &str) -> AppResult<Article> {
        let transaction = self.connection.transaction()?;
        let changed = transaction.execute(
            "DELETE FROM article_merge_relations WHERE source_article_id = ?1",
            [source_article_id],
        )?;
        if changed == 0 {
            return Err(AppError::new(
                "CDX-018",
                "このFAQは統合済みではありません。",
                "FAQの詳細を更新して、現在の状態を確認してください。",
            ));
        }
        transaction.commit()?;
        self.get_article(source_article_id)
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

    #[cfg(test)]
    pub fn accept_codex_proposal(
        &mut self,
        record: CodexProposalArticleRecord<'_>,
    ) -> AppResult<(Article, Option<Category>)> {
        self.accept_codex_proposal_as(record, INITIAL_ADMIN_USER_ID)
    }

    pub fn accept_codex_proposal_as(
        &mut self,
        record: CodexProposalArticleRecord<'_>,
        actor_user_id: &str,
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
                new_badge_until, updated_badge_until, is_hidden,
                created_by_user_id, updated_by_user_id
            ) VALUES (?1, ?2, ?3, ?4, ?5, ?6, 2, ?7, 'draft', ?8, ?9, ?9, NULL, NULL, 0, ?10, ?10)
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
                actor_user_id,
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

    #[cfg(test)]
    pub fn accept_codex_revision(
        &mut self,
        record: CodexProposalRevisionRecord<'_>,
    ) -> AppResult<Article> {
        self.accept_codex_revision_as(record, INITIAL_ADMIN_USER_ID)
    }

    pub fn accept_codex_revision_as(
        &mut self,
        record: CodexProposalRevisionRecord<'_>,
        actor_user_id: &str,
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
                    updated_at = ?8, updated_by_user_id = ?9
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
                actor_user_id,
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
                       a.deleted_at, a.new_badge_until, a.updated_badge_until, a.is_hidden,
                       merge_relation.target_article_id, merge_target.title, merge_relation.merged_at,
                       a.created_by_user_id, creator.display_name,
                       a.updated_by_user_id, updater.display_name
                  FROM articles a
                  JOIN categories c ON c.id = a.category_id
                  LEFT JOIN article_merge_relations merge_relation
                    ON merge_relation.source_article_id = a.id
                   LEFT JOIN articles merge_target
                     ON merge_target.id = merge_relation.target_article_id
                  JOIN users creator ON creator.id = a.created_by_user_id
                  JOIN users updater ON updater.id = a.updated_by_user_id
                 WHERE a.id = ?1
                "#,
                [id],
                article_from_row,
            )
            .optional()?
            .ok_or_else(article_not_found)?;
        article.attachments = self.list_article_attachments(id)?;
        article.symptoms = self.list_article_values("article_symptoms", id)?;
        article.causes = self.list_article_values("article_causes", id)?;
        article.targets = self.list_article_values("article_targets", id)?;
        article.error_codes = self.list_article_values("article_error_codes", id)?;
        article.search_terms = self.list_article_values("article_search_terms", id)?;
        article.tags = self.list_article_tags(id)?;
        let (procedures, cautions): (String, String) = self.connection.query_row(
            "SELECT procedure_text, caution_text FROM articles WHERE id = ?1",
            [id],
            |row| Ok((row.get(0)?, row.get(1)?)),
        )?;
        article.procedures = split_stored_values(&procedures);
        article.cautions = split_stored_values(&cautions);
        article.related_articles = self.list_related_articles(id)?;
        Ok(article)
    }

    fn list_article_values(&self, table: &str, article_id: &str) -> AppResult<Vec<String>> {
        let table = match table {
            "article_symptoms" => "article_symptoms",
            "article_causes" => "article_causes",
            "article_targets" => "article_targets",
            "article_error_codes" => "article_error_codes",
            "article_search_terms" => "article_search_terms",
            _ => return Err(AppError::database("FAQ検索情報の種類が正しくありません。")),
        };
        let sql =
            format!("SELECT value FROM {table} WHERE article_id = ?1 ORDER BY sort_order, id");
        let mut statement = self.connection.prepare(&sql)?;
        let rows = statement.query_map([article_id], |row| row.get(0))?;
        rows.collect::<Result<Vec<_>, _>>().map_err(AppError::from)
    }

    fn list_article_tags(&self, article_id: &str) -> AppResult<Vec<String>> {
        let mut statement = self.connection.prepare(
            r#"
            SELECT tag.name
              FROM article_tags article_tag
              JOIN tags tag ON tag.id = article_tag.tag_id
             WHERE article_tag.article_id = ?1
             ORDER BY tag.normalized_name, tag.id
            "#,
        )?;
        let rows = statement.query_map([article_id], |row| row.get(0))?;
        rows.collect::<Result<Vec<_>, _>>().map_err(AppError::from)
    }

    fn list_related_articles(&self, article_id: &str) -> AppResult<Vec<RelatedArticleSummary>> {
        let mut statement = self.connection.prepare(
            r#"
            SELECT related.id, related.title, related.status, related.deleted_at,
                   EXISTS(
                       SELECT 1 FROM article_merge_relations merge_relation
                        WHERE merge_relation.source_article_id = related.id
                   )
              FROM article_relations relation
              JOIN articles related
                ON related.id = CASE
                    WHEN relation.source_article_id = ?1 THEN relation.target_article_id
                    ELSE relation.source_article_id
                END
             WHERE relation.source_article_id = ?1 OR relation.target_article_id = ?1
             ORDER BY related.deleted_at IS NOT NULL, related.title, related.id
            "#,
        )?;
        let rows = statement.query_map([article_id], |row| {
            Ok(RelatedArticleSummary {
                id: row.get(0)?,
                title: row.get(1)?,
                status: row.get(2)?,
                deleted_at: row.get(3)?,
                is_merged: row.get(4)?,
            })
        })?;
        rows.collect::<Result<Vec<_>, _>>().map_err(AppError::from)
    }

    pub fn search_related_article_candidates(
        &self,
        article_id: Option<&str>,
        query: &str,
    ) -> AppResult<Vec<RelatedArticleCandidate>> {
        let normalized_query = normalize(query.trim());
        let like_query = format!("%{}%", escape_like(&normalized_query));
        let mut statement = self.connection.prepare(
            r#"
            SELECT candidate.id, candidate.title, candidate.status,
                   EXISTS(
                       SELECT 1 FROM article_relations relation
                        WHERE ?1 IS NOT NULL
                          AND ((relation.source_article_id = ?1 AND relation.target_article_id = candidate.id)
                            OR (relation.target_article_id = ?1 AND relation.source_article_id = candidate.id))
                   )
              FROM articles candidate
             WHERE candidate.deleted_at IS NULL
               AND (?1 IS NULL OR candidate.id <> ?1)
               AND (?2 = '' OR candidate.normalized_title LIKE ?3 ESCAPE '\')
             ORDER BY candidate.updated_at DESC, candidate.id DESC
             LIMIT 50
            "#,
        )?;
        let rows =
            statement.query_map(params![article_id, normalized_query, like_query], |row| {
                Ok(RelatedArticleCandidate {
                    id: row.get(0)?,
                    title: row.get(1)?,
                    status: row.get(2)?,
                    is_related: row.get(3)?,
                })
            })?;
        rows.collect::<Result<Vec<_>, _>>().map_err(AppError::from)
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

    pub fn search_articles(&self, input: &SearchArticlesInput) -> AppResult<SearchArticlePage> {
        const PAGE_SIZE: i64 = 50;
        let groups = self.expanded_search_query(&input.query)?;
        let fts_candidates = self.fts_search_candidates(&groups)?;
        let restrict_to_fts = !groups.is_empty()
            && groups.iter().all(|group| {
                group
                    .variants
                    .iter()
                    .all(|variant| variant.value.chars().count() >= 3)
            });
        let normalized_query = normalize(input.query.trim());
        let mut scored = Vec::new();
        for candidate in self.load_search_candidates(input)? {
            if restrict_to_fts
                && !fts_candidates.contains(&candidate.item.id)
                && candidate.procedures.is_empty()
                && candidate.cautions.is_empty()
            {
                continue;
            }
            if groups.is_empty() {
                scored.push(ScoredSearchCandidate {
                    item: candidate.item,
                    score: 0,
                    matched_groups: 0,
                });
            } else if let Some(result) =
                score_search_candidate(candidate, &groups, &normalized_query)
            {
                scored.push(result);
            }
        }
        scored.sort_by(|left, right| {
            let relevance = if groups.is_empty() {
                Ordering::Equal
            } else {
                right
                    .score
                    .cmp(&left.score)
                    .then_with(|| right.matched_groups.cmp(&left.matched_groups))
            };
            relevance
                .then_with(|| compare_search_sort(&left.item, &right.item, input.sort))
                .then_with(|| right.item.updated_at.cmp(&left.item.updated_at))
                .then_with(|| right.item.id.cmp(&left.item.id))
        });
        let total = scored.len() as i64;
        let total_pages = ((total + PAGE_SIZE - 1) / PAGE_SIZE).max(1);
        let page = input.page.max(1).min(total_pages);
        let offset = ((page - 1) * PAGE_SIZE) as usize;
        let items = scored
            .into_iter()
            .skip(offset)
            .take(PAGE_SIZE as usize)
            .map(|result| result.item)
            .collect();
        Ok(SearchArticlePage {
            items,
            total,
            page,
            page_size: PAGE_SIZE,
        })
    }

    pub fn record_search_log(
        &self,
        query: &str,
        category_id: Option<&str>,
        scope: SearchScope,
        result_count: i64,
    ) -> AppResult<String> {
        if result_count < 0 {
            return Err(AppError::database("検索結果件数が正しくありません。"));
        }
        if let Some(category_id) = category_id {
            let exists: bool = self.connection.query_row(
                "SELECT EXISTS(SELECT 1 FROM categories WHERE id = ?1)",
                [category_id],
                |row| row.get(0),
            )?;
            if !exists {
                return Err(category_not_found());
            }
        }
        let id = Uuid::now_v7().to_string();
        self.connection.execute(
            r#"
            INSERT INTO search_logs(id, query_text, normalized_query, scope, category_id, result_count, created_at)
            VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)
            "#,
            params![
                id,
                query.trim(),
                normalize(query),
                scope.as_str(),
                category_id,
                result_count,
                Utc::now().to_rfc3339(),
            ],
        )?;
        Ok(id)
    }

    pub fn record_article_view(
        &self,
        article_id: &str,
        source_search_log_id: Option<&str>,
    ) -> AppResult<()> {
        let article_exists: bool = self.connection.query_row(
            "SELECT EXISTS(SELECT 1 FROM articles WHERE id = ?1)",
            [article_id],
            |row| row.get(0),
        )?;
        if !article_exists {
            return Err(article_not_found());
        }
        if let Some(search_log_id) = source_search_log_id {
            let log_exists: bool = self.connection.query_row(
                "SELECT EXISTS(SELECT 1 FROM search_logs WHERE id = ?1)",
                [search_log_id],
                |row| row.get(0),
            )?;
            if !log_exists {
                return Err(AppError::new(
                    "LOG-001",
                    "検索履歴との関連を確認できませんでした。",
                    "FAQ一覧へ戻り、検索結果からもう一度FAQを開いてください。",
                ));
            }
        }
        self.connection.execute(
            "INSERT INTO view_logs(id, article_id, source_search_log_id, viewed_at) VALUES (?1, ?2, ?3, ?4)",
            params![
                Uuid::now_v7().to_string(),
                article_id,
                source_search_log_id,
                Utc::now().to_rfc3339(),
            ],
        )?;
        Ok(())
    }

    pub fn list_synonym_groups(&self) -> AppResult<Vec<SynonymGroup>> {
        let mut statement = self.connection.prepare(
            "SELECT id, display_name, updated_at FROM synonym_groups ORDER BY display_name, id",
        )?;
        let rows = statement.query_map([], |row| {
            Ok((
                row.get::<_, String>(0)?,
                row.get::<_, String>(1)?,
                row.get::<_, String>(2)?,
            ))
        })?;
        let mut groups = Vec::new();
        for row in rows {
            let (id, display_name, updated_at) = row?;
            let normalized_display = normalize(&display_name);
            let mut term_statement = self.connection.prepare(
                "SELECT term, normalized_term FROM synonyms WHERE group_id = ?1 ORDER BY normalized_term, id",
            )?;
            let terms = term_statement
                .query_map([&id], |row| {
                    Ok((row.get::<_, String>(0)?, row.get::<_, String>(1)?))
                })?
                .collect::<Result<Vec<_>, _>>()?
                .into_iter()
                .filter_map(|(term, normalized)| (normalized != normalized_display).then_some(term))
                .collect();
            groups.push(SynonymGroup {
                id,
                display_name,
                terms,
                updated_at,
            });
        }
        Ok(groups)
    }

    pub fn save_synonym_group(
        &mut self,
        id: Option<&str>,
        display_name: &str,
        terms: &[String],
        allow_conflicts: bool,
    ) -> AppResult<SynonymGroup> {
        let (display_name, values) = validate_synonym_values(display_name, terms)?;
        let current_id = id.unwrap_or_default();
        let normalized_values = values
            .iter()
            .map(|(_, normalized)| normalized.as_str())
            .collect::<HashSet<_>>();
        let mut statement = self.connection.prepare(
            r#"
            SELECT synonym.normalized_term, synonym_group.display_name
              FROM synonyms synonym
              JOIN synonym_groups synonym_group ON synonym_group.id = synonym.group_id
             WHERE synonym_group.id <> ?1
             ORDER BY synonym_group.display_name, synonym.normalized_term
            "#,
        )?;
        let conflicts = statement
            .query_map([current_id], |row| {
                Ok((row.get::<_, String>(0)?, row.get::<_, String>(1)?))
            })?
            .collect::<Result<Vec<_>, _>>()?
            .into_iter()
            .filter(|(term, _)| normalized_values.contains(term.as_str()))
            .collect::<Vec<_>>();
        drop(statement);
        if !allow_conflicts && !conflicts.is_empty() {
            let names = conflicts
                .iter()
                .map(|(_, name)| name.as_str())
                .collect::<HashSet<_>>()
                .into_iter()
                .collect::<Vec<_>>()
                .join("、");
            return Err(AppError::new(
                "SYN-003",
                format!("同じ語が別の同義語グループ（{names}）にも登録されています。"),
                "意図した重複であれば確認後に保存し、そうでなければ重複する語を外してください。",
            ));
        }

        let group_id = id
            .map(str::to_owned)
            .unwrap_or_else(|| Uuid::now_v7().to_string());
        let transaction = self.connection.transaction()?;
        let now = Utc::now().to_rfc3339();
        if id.is_some() {
            let updated = transaction.execute(
                "UPDATE synonym_groups SET display_name = ?2, updated_at = ?3 WHERE id = ?1",
                params![group_id, display_name, now],
            )?;
            if updated == 0 {
                return Err(synonym_group_not_found());
            }
        } else {
            transaction.execute(
                "INSERT INTO synonym_groups(id, display_name, created_at, updated_at) VALUES (?1, ?2, ?3, ?3)",
                params![group_id, display_name, now],
            )?;
        }
        transaction.execute("DELETE FROM synonyms WHERE group_id = ?1", [&group_id])?;
        for (term, normalized_term) in values {
            transaction.execute(
                "INSERT INTO synonyms(id, group_id, term, normalized_term) VALUES (?1, ?2, ?3, ?4)",
                params![Uuid::now_v7().to_string(), group_id, term, normalized_term],
            )?;
        }
        transaction.commit()?;
        self.list_synonym_groups()?
            .into_iter()
            .find(|group| group.id == group_id)
            .ok_or_else(synonym_group_not_found)
    }

    pub fn delete_synonym_group(&mut self, id: &str) -> AppResult<()> {
        let transaction = self.connection.transaction()?;
        let deleted = transaction.execute("DELETE FROM synonym_groups WHERE id = ?1", [id])?;
        if deleted == 0 {
            return Err(synonym_group_not_found());
        }
        transaction.commit()?;
        Ok(())
    }

    pub fn list_search_logs(&self, input: &ListSearchLogsInput) -> AppResult<SearchLogPage> {
        const PAGE_SIZE: i64 = 50;
        let (start_at, end_before) =
            history_date_bounds(input.start_date.as_deref(), input.end_date.as_deref())?;
        let normalized_query = normalize(input.query.trim());
        let like_query = format!("%{}%", escape_like(&normalized_query));
        let total: i64 = self.connection.query_row(
            r#"
            SELECT COUNT(*) FROM search_logs
             WHERE (?1 = '' OR normalized_query LIKE ?2 ESCAPE '\')
               AND (?3 IS NULL OR created_at >= ?3)
               AND (?4 IS NULL OR created_at < ?4)
               AND (?5 = 0 OR result_count = 0)
            "#,
            params![
                normalized_query,
                like_query,
                start_at.as_deref(),
                end_before.as_deref(),
                input.zero_results_only,
            ],
            |row| row.get(0),
        )?;
        let total_pages = ((total + PAGE_SIZE - 1) / PAGE_SIZE).max(1);
        let page = input.page.max(1).min(total_pages);
        let offset = (page - 1) * PAGE_SIZE;
        let mut statement = self.connection.prepare(
            r#"
            SELECT search_log.id, search_log.query_text, search_log.normalized_query,
                   search_log.scope, category.name, search_log.result_count, search_log.created_at
              FROM search_logs search_log
              LEFT JOIN categories category ON category.id = search_log.category_id
             WHERE (?1 = '' OR search_log.normalized_query LIKE ?2 ESCAPE '\')
               AND (?3 IS NULL OR search_log.created_at >= ?3)
               AND (?4 IS NULL OR search_log.created_at < ?4)
               AND (?5 = 0 OR search_log.result_count = 0)
             ORDER BY search_log.created_at DESC, search_log.id DESC
             LIMIT ?6 OFFSET ?7
            "#,
        )?;
        let rows = statement.query_map(
            params![
                normalized_query,
                like_query,
                start_at.as_deref(),
                end_before.as_deref(),
                input.zero_results_only,
                PAGE_SIZE,
                offset,
            ],
            |row| {
                Ok(SearchLogItem {
                    id: row.get(0)?,
                    query_text: row.get(1)?,
                    normalized_query: row.get(2)?,
                    scope: row.get(3)?,
                    category_name: row.get(4)?,
                    result_count: row.get(5)?,
                    created_at: row.get(6)?,
                })
            },
        )?;
        Ok(SearchLogPage {
            items: rows.collect::<Result<Vec<_>, _>>()?,
            total,
            page,
            page_size: PAGE_SIZE,
        })
    }

    pub fn list_view_logs(&self, input: &ListViewLogsInput) -> AppResult<ViewLogPage> {
        const PAGE_SIZE: i64 = 50;
        let (start_at, end_before) =
            history_date_bounds(input.start_date.as_deref(), input.end_date.as_deref())?;
        let normalized_query = normalize(input.query.trim());
        let like_query = format!("%{}%", escape_like(&normalized_query));
        let total: i64 = self.connection.query_row(
            r#"
            SELECT COUNT(*)
              FROM view_logs view_log
              JOIN articles article ON article.id = view_log.article_id
              LEFT JOIN search_logs search_log ON search_log.id = view_log.source_search_log_id
             WHERE (?1 = '' OR article.normalized_title LIKE ?2 ESCAPE '\'
                    OR search_log.normalized_query LIKE ?2 ESCAPE '\')
               AND (?3 IS NULL OR view_log.viewed_at >= ?3)
               AND (?4 IS NULL OR view_log.viewed_at < ?4)
            "#,
            params![
                normalized_query,
                like_query,
                start_at.as_deref(),
                end_before.as_deref(),
            ],
            |row| row.get(0),
        )?;
        let total_pages = ((total + PAGE_SIZE - 1) / PAGE_SIZE).max(1);
        let page = input.page.max(1).min(total_pages);
        let offset = (page - 1) * PAGE_SIZE;
        let mut statement = self.connection.prepare(
            r#"
            SELECT view_log.id, article.id, article.title, search_log.query_text, view_log.viewed_at
              FROM view_logs view_log
              JOIN articles article ON article.id = view_log.article_id
              LEFT JOIN search_logs search_log ON search_log.id = view_log.source_search_log_id
             WHERE (?1 = '' OR article.normalized_title LIKE ?2 ESCAPE '\'
                    OR search_log.normalized_query LIKE ?2 ESCAPE '\')
               AND (?3 IS NULL OR view_log.viewed_at >= ?3)
               AND (?4 IS NULL OR view_log.viewed_at < ?4)
             ORDER BY view_log.viewed_at DESC, view_log.id DESC
             LIMIT ?5 OFFSET ?6
            "#,
        )?;
        let rows = statement.query_map(
            params![
                normalized_query,
                like_query,
                start_at.as_deref(),
                end_before.as_deref(),
                PAGE_SIZE,
                offset,
            ],
            |row| {
                Ok(ViewLogItem {
                    id: row.get(0)?,
                    article_id: row.get(1)?,
                    article_title: row.get(2)?,
                    source_query_text: row.get(3)?,
                    viewed_at: row.get(4)?,
                })
            },
        )?;
        Ok(ViewLogPage {
            items: rows.collect::<Result<Vec<_>, _>>()?,
            total,
            page,
            page_size: PAGE_SIZE,
        })
    }

    pub fn delete_history(
        &mut self,
        target: HistoryTarget,
        start_date: Option<&str>,
        end_date: Option<&str>,
        delete_all: bool,
    ) -> AppResult<i64> {
        if !delete_all && start_date.is_none() && end_date.is_none() {
            return Err(AppError::new(
                "LOG-002",
                "履歴を削除する期間が指定されていません。",
                "開始日または終了日を指定するか、全履歴削除を選んでください。",
            ));
        }
        let (start_at, end_before) = history_date_bounds(start_date, end_date)?;
        let table = match target {
            HistoryTarget::Search => "search_logs",
            HistoryTarget::View => "view_logs",
        };
        let timestamp = match target {
            HistoryTarget::Search => "created_at",
            HistoryTarget::View => "viewed_at",
        };
        let transaction = self.connection.transaction()?;
        let deleted = if delete_all {
            transaction.execute(&format!("DELETE FROM {table}"), [])?
        } else {
            transaction.execute(
                &format!(
                    "DELETE FROM {table} WHERE (?1 IS NULL OR {timestamp} >= ?1) AND (?2 IS NULL OR {timestamp} < ?2)"
                ),
                params![start_at.as_deref(), end_before.as_deref()],
            )?
        };
        transaction.commit()?;
        Ok(deleted as i64)
    }

    fn load_search_candidates(
        &self,
        input: &SearchArticlesInput,
    ) -> AppResult<Vec<SearchCandidate>> {
        let mut statement = self.connection.prepare(
            r#"
            WITH RECURSIVE selected_categories(id) AS (
                SELECT id FROM categories WHERE id = ?1
                UNION ALL
                SELECT c.id FROM categories c
                JOIN selected_categories parent ON c.parent_id = parent.id
            )
            SELECT a.id, a.category_id, c.name, a.title, a.summary,
                   a.status, a.importance, a.new_badge_until, a.updated_badge_until,
                   a.is_hidden, a.updated_at,
                   search_doc.title, search_doc.summary, search_doc.body,
                   search_doc.symptoms, search_doc.causes, search_doc.targets,
                   search_doc.error_codes, search_doc.tags, search_doc.search_terms,
                   a.procedure_text, a.caution_text,
                   COALESCE((
                       SELECT GROUP_CONCAT(ordered_tags.name, char(10))
                         FROM (
                             SELECT tag.name
                               FROM article_tags article_tag
                               JOIN tags tag ON tag.id = article_tag.tag_id
                              WHERE article_tag.article_id = a.id
                              ORDER BY tag.normalized_name, tag.id
                         ) ordered_tags
                   ), '')
              FROM articles a
              JOIN categories c ON c.id = a.category_id
              JOIN article_search_documents search_doc ON search_doc.article_id = a.id
             WHERE a.deleted_at IS NULL
               AND a.is_hidden = 0
               AND NOT EXISTS (
                   SELECT 1 FROM article_merge_relations merge_relation
                    WHERE merge_relation.source_article_id = a.id
               )
               AND (?3 = 1 OR a.status = 'published')
               AND (
                   ?2 = 'all'
                   OR (?2 = 'descendants' AND (?1 IS NULL OR a.category_id IN (SELECT id FROM selected_categories)))
                   OR (?2 = 'current' AND ?1 IS NOT NULL AND a.category_id = ?1)
               )
            "#,
        )?;
        let rows = statement.query_map(
            params![
                input.category_id.as_deref(),
                input.scope.as_str(),
                input.include_drafts,
            ],
            |row| {
                let tags_text: String = row.get(22)?;
                Ok(SearchCandidate {
                    item: ArticleListItem {
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
                        tags: split_stored_values(&tags_text),
                        match_reasons: Vec::new(),
                    },
                    normalized_title: row.get(11)?,
                    normalized_summary: row.get(12)?,
                    body: row.get(13)?,
                    symptoms: row.get(14)?,
                    causes: row.get(15)?,
                    targets: row.get(16)?,
                    error_codes: row.get(17)?,
                    normalized_tags: row.get(18)?,
                    search_terms: row.get(19)?,
                    procedures: normalize(&row.get::<_, String>(20)?),
                    cautions: normalize(&row.get::<_, String>(21)?),
                })
            },
        )?;
        rows.collect::<Result<Vec<_>, _>>().map_err(AppError::from)
    }

    fn expanded_search_query(&self, query: &str) -> AppResult<Vec<SearchQueryGroup>> {
        let mut groups = search_query_groups(query);
        if groups.is_empty() {
            return Ok(groups);
        }
        let mut statement = self.connection.prepare(
            "SELECT group_id, normalized_term FROM synonyms ORDER BY group_id, normalized_term",
        )?;
        let rows = statement.query_map([], |row| {
            Ok((row.get::<_, String>(0)?, row.get::<_, String>(1)?))
        })?;
        let mut synonym_groups: HashMap<String, Vec<String>> = HashMap::new();
        for row in rows {
            let (group_id, term) = row?;
            synonym_groups.entry(group_id).or_default().push(term);
        }
        for group in &mut groups {
            let existing = group
                .variants
                .iter()
                .map(|variant| variant.value.clone())
                .collect::<HashSet<_>>();
            let mut additions = Vec::new();
            for terms in synonym_groups.values() {
                let Some(matched) = terms
                    .iter()
                    .find(|term| existing.contains(*term) || group.display.contains(term.as_str()))
                else {
                    continue;
                };
                for term in terms {
                    if !existing.contains(term) {
                        additions.push(SearchVariant {
                            value: term.clone(),
                            synonym_from: Some(matched.clone()),
                        });
                    }
                }
            }
            group.variants.extend(additions);
        }
        Ok(groups)
    }

    fn fts_search_candidates(&self, groups: &[SearchQueryGroup]) -> AppResult<HashSet<String>> {
        let mut terms = groups
            .iter()
            .flat_map(|group| group.variants.iter())
            .map(|variant| variant.value.as_str())
            .filter(|value| value.chars().count() >= 3)
            .map(str::to_owned)
            .collect::<HashSet<_>>()
            .into_iter()
            .collect::<Vec<_>>();
        terms.sort();
        terms.truncate(64);
        if terms.is_empty() {
            return Ok(HashSet::new());
        }
        let match_query = terms
            .into_iter()
            .map(|term| format!("\"{}\"", term.replace('"', "\"\"")))
            .collect::<Vec<_>>()
            .join(" OR ");
        let mut statement = self.connection.prepare(
            "SELECT article_id FROM article_search_fts WHERE article_search_fts MATCH ?1",
        )?;
        let rows = statement.query_map([match_query], |row| row.get(0))?;
        rows.collect::<Result<HashSet<_>, _>>()
            .map_err(AppError::from)
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
                       a.is_hidden, a.updated_at, a.deleted_at,
                       merge_relation.target_article_id, merge_target.title AS merge_target_title,
                       merge_relation.merged_at,
                       creator.display_name AS created_by_display_name,
                       updater.display_name AS updated_by_display_name
                  FROM articles a
                  JOIN categories c ON c.id = a.category_id
                  JOIN article_search_documents search_doc ON search_doc.article_id = a.id
                  LEFT JOIN article_merge_relations merge_relation
                    ON merge_relation.source_article_id = a.id
                  LEFT JOIN articles merge_target
                    ON merge_target.id = merge_relation.target_article_id
                  JOIN users creator ON creator.id = a.created_by_user_id
                  JOIN users updater ON updater.id = a.updated_by_user_id
                 WHERE ((?4 = 1 AND a.deleted_at IS NOT NULL) OR (?4 = 0 AND a.deleted_at IS NULL))
                   AND (?2 IS NULL OR a.category_id IN (SELECT id FROM selected_categories))
                   AND (?3 IS NULL OR a.status = ?3)
                   AND (?1 = '' OR search_doc.title LIKE ?5 ESCAPE '\'
                        OR search_doc.summary LIKE ?5 ESCAPE '\'
                        OR search_doc.body LIKE ?5 ESCAPE '\')
            )
            SELECT id, category_id, category_name, title, summary, status, importance,
                   new_badge_until, updated_badge_until, is_hidden, updated_at, deleted_at,
                    target_article_id, merge_target_title, merged_at,
                    created_by_display_name, updated_by_display_name,
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
                        created_by_display_name: row.get(15)?,
                        updated_by_display_name: row.get(16)?,
                        deleted_at: row.get(11)?,
                        merge_info: article_merge_info_from_columns(row, 12, 13, 14)?,
                    },
                    row.get::<_, i64>(17)?,
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

    #[cfg(test)]
    pub fn delete_article(&mut self, id: &str) -> AppResult<Article> {
        self.delete_article_as(id, INITIAL_ADMIN_USER_ID)
    }

    pub fn delete_article_as(&mut self, id: &str, actor_user_id: &str) -> AppResult<Article> {
        let transaction = self.connection.transaction()?;
        let has_merge_sources: bool = transaction.query_row(
            "SELECT EXISTS(SELECT 1 FROM article_merge_relations WHERE target_article_id = ?1)",
            [id],
            |row| row.get(0),
        )?;
        if has_merge_sources {
            return Err(merge_target_visibility_error());
        }
        let now = Utc::now().to_rfc3339();
        let updated = transaction.execute(
            "UPDATE articles SET deleted_at = ?2, updated_at = ?2, updated_by_user_id = ?3 WHERE id = ?1 AND deleted_at IS NULL",
            params![id, now, actor_user_id],
        )?;
        if updated == 0 {
            return Err(article_not_found());
        }
        transaction.execute("DELETE FROM article_search_fts WHERE article_id = ?1", [id])?;
        transaction.commit()?;
        self.get_article(id)
    }

    #[cfg(test)]
    pub fn restore_article(&mut self, id: &str) -> AppResult<Article> {
        self.restore_article_as(id, INITIAL_ADMIN_USER_ID)
    }

    pub fn restore_article_as(&mut self, id: &str, actor_user_id: &str) -> AppResult<Article> {
        let transaction = self.connection.transaction()?;
        let now = Utc::now().to_rfc3339();
        let updated = transaction.execute(
            "UPDATE articles SET deleted_at = NULL, updated_at = ?2, updated_by_user_id = ?3 WHERE id = ?1 AND deleted_at IS NOT NULL",
            params![id, now, actor_user_id],
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

fn read_json_document(source: &Path) -> AppResult<(String, KnowledgeJsonDocument)> {
    let bytes =
        fs::read(source).map_err(|_| json_read_error("JSONファイルを読み込めませんでした。"))?;
    if bytes.len() > 100 * 1024 * 1024 {
        return Err(json_read_error("JSONファイルが100MBを超えています。"));
    }
    let mut hasher = Sha256::new();
    hasher.update(&bytes);
    let file_sha256 = hasher
        .finalize()
        .iter()
        .map(|byte| format!("{byte:02x}"))
        .collect();
    let document = serde_json::from_slice(&bytes).map_err(|_| {
        json_read_error("JSONの構造、文字コード、または必須項目が正しくありません。")
    })?;
    Ok((file_sha256, document))
}

fn json_entity_counts(document: &KnowledgeJsonDocument) -> JsonEntityCounts {
    JsonEntityCounts {
        categories: document.categories.len(),
        articles: document.articles.len(),
        tags: document.tags.len(),
        synonym_groups: document.synonym_groups.len(),
        relations: document.relations.len(),
        merge_relations: document.merge_relations.len(),
    }
}

fn json_action_counts(
    incoming: &KnowledgeJsonDocument,
    current: &KnowledgeJsonDocument,
) -> (usize, usize, usize) {
    fn count<T: PartialEq>(
        incoming: &[T],
        current: &[T],
        key: impl Fn(&T) -> String,
    ) -> (usize, usize, usize) {
        let current = current
            .iter()
            .map(|item| (key(item), item))
            .collect::<HashMap<_, _>>();
        incoming.iter().fold((0, 0, 0), |mut counts, item| {
            match current.get(&key(item)) {
                None => counts.0 += 1,
                Some(existing) if *existing == item => counts.2 += 1,
                Some(_) => counts.1 += 1,
            }
            counts
        })
    }
    let groups = [
        count(&incoming.categories, &current.categories, |item| {
            item.id.clone()
        }),
        count(&incoming.articles, &current.articles, |item| {
            item.id.clone()
        }),
        count(&incoming.tags, &current.tags, |item| item.id.clone()),
        count(&incoming.synonym_groups, &current.synonym_groups, |item| {
            item.id.clone()
        }),
        count(&incoming.relations, &current.relations, |item| {
            format!("{}:{}", item.source_article_id, item.target_article_id)
        }),
        count(
            &incoming.merge_relations,
            &current.merge_relations,
            |item| item.source_article_id.clone(),
        ),
    ];
    groups.into_iter().fold((0, 0, 0), |acc, value| {
        (acc.0 + value.0, acc.1 + value.1, acc.2 + value.2)
    })
}

fn validate_unique_json_ids<'a>(
    ids: impl Iterator<Item = &'a str>,
    label: &str,
    errors: &mut Vec<String>,
) {
    let mut unique = HashSet::new();
    for id in ids {
        if !unique.insert(id) {
            errors.push(format!("同じ{label}IDがJSON内に複数あります（{id}）。"));
        }
    }
}

fn json_category_depths(
    document: &KnowledgeJsonDocument,
    errors: &mut Vec<String>,
) -> HashMap<String, i64> {
    let parents = document
        .categories
        .iter()
        .map(|category| (category.id.as_str(), category.parent_id.as_deref()))
        .collect::<HashMap<_, _>>();
    let mut depths = HashMap::new();
    for category in &document.categories {
        let mut depth = 1_i64;
        let mut current = category.parent_id.as_deref();
        let mut visited = HashSet::from([category.id.as_str()]);
        while let Some(parent_id) = current {
            if !visited.insert(parent_id) {
                errors.push(format!("分類「{}」の階層が循環しています。", category.name));
                depth = 6;
                break;
            }
            depth += 1;
            current = parents.get(parent_id).copied().flatten();
            if depth > 5 {
                break;
            }
        }
        depths.insert(category.id.clone(), depth);
    }
    depths
}

fn valid_management_code(value: &str, prefix: &str) -> bool {
    let upper = value.to_ascii_uppercase();
    let Some(number) = upper.strip_prefix(prefix) else {
        return false;
    };
    number.len() >= 5 && number.chars().all(|character| character.is_ascii_digit())
}

fn validate_json_date(value: Option<&str>) -> Result<(), ()> {
    value
        .map(|value| NaiveDate::parse_from_str(value, "%Y-%m-%d").map(|_| ()))
        .transpose()
        .map(|_| ())
        .map_err(|_| ())
}

fn validate_json_article_details(article: &JsonArticleData) -> AppResult<()> {
    validate_detail_values(&article.symptoms, "症状", 200)?;
    validate_detail_values(&article.causes, "想定原因", 200)?;
    validate_detail_values(&article.targets, "対象", 200)?;
    validate_detail_values(&article.error_codes, "エラーコード", 100)?;
    validate_detail_values(&article.procedures, "対応手順", 500)?;
    validate_detail_values(&article.cautions, "注意事項", 500)?;
    validate_detail_values(&article.search_terms, "検索用語", 200)?;
    if article.tag_ids.len() > 50
        || article
            .tag_ids
            .iter()
            .any(|id| Uuid::parse_str(id).is_err())
        || article.tag_ids.iter().collect::<HashSet<_>>().len() != article.tag_ids.len()
    {
        return Err(article_detail_error(
            "タグIDが不正または重複しています。",
            "タグを確認してください。",
        ));
    }
    Ok(())
}

fn json_read_error(message: &str) -> AppError {
    AppError::new(
        "JSON-002",
        message,
        "KnowledgeAppの「.knowledge-export.json」ファイルを選び直してください。",
    )
}

fn json_write_error() -> AppError {
    AppError::new(
        "JSON-005",
        "JSONファイルを書き出せませんでした。",
        "保存先の空き容量と書き込み権限を確認し、もう一度お試しください。",
    )
}

fn parse_stored_proposal(payload: String) -> AppResult<CodexFaqProposal> {
    serde_json::from_str(&payload)
        .map_err(|_| AppError::database("保存済みのCodex提案履歴を読み取れませんでした。"))
}

fn csv_status_label(status: &str) -> &'static str {
    match status {
        "draft" => "下書き",
        "published" => "公開",
        "archived" => "廃止",
        _ => "下書き",
    }
}

fn safe_excel_cell(value: &str) -> String {
    if value.starts_with(['=', '+', '-', '@']) {
        format!("'{value}")
    } else {
        value.to_owned()
    }
}

fn unsafed_excel_cell(value: &str) -> &str {
    match value.strip_prefix('\'') {
        Some(rest) if rest.starts_with(['=', '+', '-', '@']) => rest,
        _ => value,
    }
}

fn csv_optional_date(value: &str) -> Result<Option<String>, &'static str> {
    let value = value.trim();
    if value.is_empty() {
        return Ok(None);
    }
    NaiveDate::parse_from_str(value, "%Y-%m-%d")
        .map(|_| Some(value.to_owned()))
        .map_err(|_| "日付はYYYY-MM-DD形式で入力してください。")
}

fn csv_error_plan(line: usize, message: &str) -> CsvImportPlanRow {
    CsvImportPlanRow {
        line,
        action: CsvRowAction::Error,
        faq_management_id: None,
        article_id: String::new(),
        category_id: String::new(),
        title: String::new(),
        summary: String::new(),
        body_plain_text: String::new(),
        body_doc_json: String::new(),
        body_will_be_replaced: false,
        status: "draft".to_owned(),
        importance: 1,
        new_badge_until: None,
        updated_badge_until: None,
        is_hidden: false,
        stale_update_will_overwrite: false,
        messages: vec![message.to_owned()],
    }
}

fn csv_preview(source: &Path, file_sha256: String, plans: &[CsvImportPlanRow]) -> CsvImportPreview {
    CsvImportPreview {
        source_path: source.display().to_string(),
        file_sha256,
        total_rows: plans.len(),
        create_count: plans
            .iter()
            .filter(|row| row.action == CsvRowAction::Create)
            .count(),
        update_count: plans
            .iter()
            .filter(|row| row.action == CsvRowAction::Update)
            .count(),
        unchanged_count: plans
            .iter()
            .filter(|row| row.action == CsvRowAction::Unchanged)
            .count(),
        body_replacement_count: plans.iter().filter(|row| row.body_will_be_replaced).count(),
        stale_overwrite_count: plans
            .iter()
            .filter(|row| row.stale_update_will_overwrite)
            .count(),
        error_count: plans
            .iter()
            .filter(|row| row.action == CsvRowAction::Error)
            .count(),
        rows: plans
            .iter()
            .map(|row| CsvImportPreviewRow {
                line: row.line,
                action: row.action.as_str().to_owned(),
                faq_management_id: row.faq_management_id.clone(),
                title: row.title.clone(),
                body_will_be_replaced: row.body_will_be_replaced,
                stale_update_will_overwrite: row.stale_update_will_overwrite,
                messages: row.messages.clone(),
            })
            .collect(),
    }
}

fn csv_write_error<T>(_: T) -> AppError {
    AppError::new(
        "CSV-001",
        "CSVファイルを書き出せませんでした。",
        "保存先の空き容量と書込み権限を確認し、Excelで開いている場合は閉じてください。",
    )
}

fn csv_read_error(message: &str) -> AppError {
    AppError::new(
        "CSV-003",
        message,
        "KnowledgeAppから書き出したUTF-8のCSVを選択してください。",
    )
}

fn parse_user_role(value: &str) -> AppResult<UserRole> {
    match value {
        "admin" => Ok(UserRole::Admin),
        "user" => Ok(UserRole::User),
        _ => Err(AppError::database("利用者の権限データが正しくありません。")),
    }
}

fn validate_user_text<'a>(value: &'a str, label: &str) -> AppResult<&'a str> {
    let value = value.trim();
    if value.is_empty() || value.chars().count() > 100 {
        return Err(AppError::new(
            "USR-001",
            format!("{label}は1～100文字で入力してください。"),
            "入力内容を確認してください。",
        ));
    }
    Ok(value)
}

fn user_not_found() -> AppError {
    AppError::new(
        "USR-001",
        "指定した利用者が見つかりません。",
        "利用者一覧を更新して、もう一度選択してください。",
    )
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
        merge_info: article_merge_info_from_columns(row, 15, 16, 17)?,
        created_by_user_id: row.get(18)?,
        created_by_display_name: row.get(19)?,
        updated_by_user_id: row.get(20)?,
        updated_by_display_name: row.get(21)?,
        attachments: Vec::new(),
        symptoms: Vec::new(),
        causes: Vec::new(),
        targets: Vec::new(),
        error_codes: Vec::new(),
        procedures: Vec::new(),
        cautions: Vec::new(),
        tags: Vec::new(),
        search_terms: Vec::new(),
        related_articles: Vec::new(),
    })
}

fn article_merge_info_from_columns(
    row: &rusqlite::Row<'_>,
    target_id_index: usize,
    target_title_index: usize,
    merged_at_index: usize,
) -> rusqlite::Result<Option<ArticleMergeInfo>> {
    let target_article_id: Option<String> = row.get(target_id_index)?;
    let Some(target_article_id) = target_article_id else {
        return Ok(None);
    };
    Ok(Some(ArticleMergeInfo {
        target_article_id,
        target_article_title: row.get(target_title_index)?,
        merged_at: row.get(merged_at_index)?,
    }))
}

fn replace_article_details(
    transaction: &Transaction<'_>,
    article_id: &str,
    details: &ArticleDetailsRecord<'_>,
) -> AppResult<()> {
    let symptoms = validate_detail_values(details.symptoms, "症状", 200)?;
    let causes = validate_detail_values(details.causes, "想定原因", 200)?;
    let targets = validate_detail_values(details.targets, "対象", 200)?;
    let error_codes = validate_detail_values(details.error_codes, "エラーコード", 100)?;
    let search_terms = validate_detail_values(details.search_terms, "検索用語", 200)?;
    let procedures = validate_detail_values(details.procedures, "対応手順", 500)?;
    let cautions = validate_detail_values(details.cautions, "注意事項", 500)?;
    let tags = validate_detail_values(details.tags, "タグ", 100)?;

    replace_detail_table(transaction, "article_symptoms", article_id, &symptoms)?;
    replace_detail_table(transaction, "article_causes", article_id, &causes)?;
    replace_detail_table(transaction, "article_targets", article_id, &targets)?;
    replace_detail_table(transaction, "article_error_codes", article_id, &error_codes)?;
    replace_detail_table(
        transaction,
        "article_search_terms",
        article_id,
        &search_terms,
    )?;
    transaction.execute(
        "UPDATE articles SET procedure_text = ?2, caution_text = ?3 WHERE id = ?1",
        params![
            article_id,
            join_stored_values(&procedures),
            join_stored_values(&cautions)
        ],
    )?;

    transaction.execute(
        "DELETE FROM article_tags WHERE article_id = ?1",
        [article_id],
    )?;
    let now = Utc::now().to_rfc3339();
    for (value, normalized) in tags {
        let tag_id: Option<String> = transaction
            .query_row(
                "SELECT id FROM tags WHERE normalized_name = ?1",
                [&normalized],
                |row| row.get(0),
            )
            .optional()?;
        let tag_id = tag_id.unwrap_or_else(|| Uuid::now_v7().to_string());
        transaction.execute(
            "INSERT OR IGNORE INTO tags(id, name, normalized_name, created_at, updated_at) VALUES (?1, ?2, ?3, ?4, ?4)",
            params![tag_id, value, normalized, now],
        )?;
        transaction.execute(
            "INSERT INTO article_tags(article_id, tag_id) VALUES (?1, ?2)",
            params![article_id, tag_id],
        )?;
    }

    replace_article_relations(transaction, article_id, details.related_article_ids)
}

fn validate_detail_values(
    values: &[String],
    label: &str,
    maximum_length: usize,
) -> AppResult<Vec<(String, String)>> {
    if values.len() > 50 {
        return Err(article_detail_error(
            format!("{label}は50件以内で登録してください。"),
            "不要な項目を削除して、もう一度保存してください。",
        ));
    }
    let mut normalized_values = HashSet::new();
    let mut result = Vec::with_capacity(values.len());
    for value in values {
        let value = value.trim();
        if value.is_empty()
            || value.chars().count() > maximum_length
            || value.contains('\r')
            || value.contains('\n')
        {
            return Err(article_detail_error(
                format!("{label}は1～{maximum_length}文字の1行テキストで入力してください。"),
                "入力内容を短くし、空の項目や改行を削除してください。",
            ));
        }
        let normalized = normalize(value);
        if !normalized_values.insert(normalized.clone()) {
            return Err(article_detail_error(
                format!("{label}に同じ内容が重複しています。"),
                "重複している項目を1件にまとめてください。",
            ));
        }
        result.push((value.to_owned(), normalized));
    }
    Ok(result)
}

fn replace_detail_table(
    transaction: &Transaction<'_>,
    table: &str,
    article_id: &str,
    values: &[(String, String)],
) -> AppResult<()> {
    let table = match table {
        "article_symptoms" => "article_symptoms",
        "article_causes" => "article_causes",
        "article_targets" => "article_targets",
        "article_error_codes" => "article_error_codes",
        "article_search_terms" => "article_search_terms",
        _ => return Err(AppError::database("FAQ検索情報の種類が正しくありません。")),
    };
    transaction.execute(
        &format!("DELETE FROM {table} WHERE article_id = ?1"),
        [article_id],
    )?;
    let sql = format!(
        "INSERT INTO {table}(id, article_id, value, normalized_value, sort_order) VALUES (?1, ?2, ?3, ?4, ?5)"
    );
    for (index, (value, normalized)) in values.iter().enumerate() {
        transaction.execute(
            &sql,
            params![
                Uuid::now_v7().to_string(),
                article_id,
                value,
                normalized,
                index as i64
            ],
        )?;
    }
    Ok(())
}

fn replace_article_relations(
    transaction: &Transaction<'_>,
    article_id: &str,
    related_article_ids: &[String],
) -> AppResult<()> {
    if related_article_ids.len() > 50 {
        return Err(article_detail_error(
            "関連FAQは50件以内で選択してください。",
            "不要な関連FAQを外して、もう一度保存してください。",
        ));
    }
    let mut current = HashSet::new();
    {
        let mut statement = transaction.prepare(
            r#"
            SELECT CASE WHEN source_article_id = ?1 THEN target_article_id ELSE source_article_id END
              FROM article_relations
             WHERE source_article_id = ?1 OR target_article_id = ?1
            "#,
        )?;
        let rows = statement.query_map([article_id], |row| row.get::<_, String>(0))?;
        for row in rows {
            current.insert(row?);
        }
    }

    let mut unique = HashSet::new();
    let mut validated = Vec::with_capacity(related_article_ids.len());
    for related_id in related_article_ids {
        let related_id = related_id.trim();
        if related_id.is_empty()
            || related_id == article_id
            || !unique.insert(related_id.to_owned())
        {
            return Err(article_detail_error(
                "関連FAQに自己参照または重複があります。",
                "同じFAQを1回だけ選択し、編集中のFAQ自身は選択しないでください。",
            ));
        }
        let deleted_at: Option<Option<String>> = transaction
            .query_row(
                "SELECT deleted_at FROM articles WHERE id = ?1",
                [related_id],
                |row| row.get(0),
            )
            .optional()?;
        let Some(deleted_at) = deleted_at else {
            return Err(article_detail_error(
                "選択した関連FAQが見つかりません。",
                "関連FAQの一覧を更新し、選び直してください。",
            ));
        };
        if deleted_at.is_some() && !current.contains(related_id) {
            return Err(article_detail_error(
                "削除済みFAQを新しい関連FAQとして登録できません。",
                "削除されていないFAQを選択してください。",
            ));
        }
        validated.push(related_id.to_owned());
    }

    transaction.execute(
        "DELETE FROM article_relations WHERE source_article_id = ?1 OR target_article_id = ?1",
        [article_id],
    )?;
    for (index, related_id) in validated.iter().enumerate() {
        let (source, target) = if article_id < related_id.as_str() {
            (article_id, related_id.as_str())
        } else {
            (related_id.as_str(), article_id)
        };
        transaction.execute(
            "INSERT INTO article_relations(source_article_id, target_article_id, sort_order) VALUES (?1, ?2, ?3)",
            params![source, target, index as i64],
        )?;
    }
    Ok(())
}

fn update_article_search_index(
    transaction: &Transaction<'_>,
    article_id: &str,
    title: &str,
    summary: &str,
    body: &str,
    details: Option<&ArticleDetailsRecord<'_>>,
) -> AppResult<()> {
    if let Some(details) = details {
        transaction.execute(
            r#"
            INSERT INTO article_search_documents(
                article_id, title, summary, body, symptoms, causes, targets,
                error_codes, tags, search_terms
            ) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10)
            ON CONFLICT(article_id) DO UPDATE SET
                title = excluded.title, summary = excluded.summary, body = excluded.body,
                symptoms = excluded.symptoms, causes = excluded.causes, targets = excluded.targets,
                error_codes = excluded.error_codes, tags = excluded.tags,
                search_terms = excluded.search_terms
            "#,
            params![
                article_id,
                normalize(title),
                normalize(summary),
                normalize(body),
                normalized_values_text(details.symptoms),
                normalized_values_text(details.causes),
                normalized_values_text(details.targets),
                normalized_values_text(details.error_codes),
                normalized_values_text(details.tags),
                normalized_values_text(details.search_terms),
            ],
        )?;
    } else {
        transaction.execute(
            r#"
            INSERT INTO article_search_documents(article_id, title, summary, body)
            VALUES (?1, ?2, ?3, ?4)
            ON CONFLICT(article_id) DO UPDATE SET
                title = excluded.title, summary = excluded.summary, body = excluded.body
            "#,
            params![
                article_id,
                normalize(title),
                normalize(summary),
                normalize(body)
            ],
        )?;
    }
    let supplemental_body = if let Some(details) = details {
        [
            normalized_values_text(details.procedures),
            normalized_values_text(details.cautions),
        ]
        .join(" ")
    } else {
        let (procedures, cautions): (String, String) = transaction.query_row(
            "SELECT procedure_text, caution_text FROM articles WHERE id = ?1",
            [article_id],
            |row| Ok((row.get(0)?, row.get(1)?)),
        )?;
        format!("{} {}", normalize(&procedures), normalize(&cautions))
    };
    transaction.execute(
        "DELETE FROM article_search_fts WHERE article_id = ?1",
        [article_id],
    )?;
    transaction.execute(
        r#"
        INSERT INTO article_search_fts(
            article_id, title, summary, body, symptoms, causes, targets,
            error_codes, tags, search_terms
        )
        SELECT article_id, title, summary, TRIM(body || ' ' || ?2), symptoms, causes, targets,
               error_codes, tags, search_terms
          FROM article_search_documents
         WHERE article_id = ?1
        "#,
        params![article_id, supplemental_body],
    )?;
    Ok(())
}

fn normalized_values_text(values: &[String]) -> String {
    values
        .iter()
        .map(|value| normalize(value))
        .filter(|value| !value.is_empty())
        .collect::<Vec<_>>()
        .join(" ")
}

fn join_stored_values(values: &[(String, String)]) -> String {
    values
        .iter()
        .map(|(value, _)| value.as_str())
        .collect::<Vec<_>>()
        .join("\n")
}

fn split_stored_values(value: &str) -> Vec<String> {
    value
        .lines()
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .map(str::to_owned)
        .collect()
}

fn article_detail_error(message: impl Into<String>, action: impl Into<String>) -> AppError {
    AppError::new("ART-009", message, action)
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

fn search_query_groups(query: &str) -> Vec<SearchQueryGroup> {
    const STOP_WORDS: &[&str] = &[
        "の", "が", "を", "に", "は", "で", "と", "へ", "も", "です", "ます",
    ];
    let normalized = normalize(query);
    let mut segments = Vec::new();
    let mut current = String::new();
    let mut current_is_ascii = None;
    let flush = |current: &mut String, segments: &mut Vec<String>| {
        if !current.is_empty() {
            segments.push(std::mem::take(current));
        }
    };
    for character in normalized.chars() {
        if !character.is_alphanumeric() {
            flush(&mut current, &mut segments);
            current_is_ascii = None;
            continue;
        }
        let is_ascii = character.is_ascii();
        if current_is_ascii.is_some_and(|value| value != is_ascii) {
            flush(&mut current, &mut segments);
        }
        current_is_ascii = Some(is_ascii);
        current.push(character);
    }
    flush(&mut current, &mut segments);

    segments
        .into_iter()
        .filter(|segment| !STOP_WORDS.contains(&segment.as_str()))
        .map(|segment| {
            let characters = segment.chars().collect::<Vec<_>>();
            let mut values = vec![segment.clone()];
            if characters.len() > 3 && characters.iter().any(|character| !character.is_ascii()) {
                values.extend(
                    characters
                        .windows(3)
                        .map(|window| window.iter().collect::<String>()),
                );
            }
            let mut unique = HashSet::new();
            let variants = values
                .into_iter()
                .filter(|value| unique.insert(value.clone()))
                .map(|value| SearchVariant {
                    value,
                    synonym_from: None,
                })
                .collect();
            SearchQueryGroup {
                display: segment,
                variants,
            }
        })
        .collect()
}

fn score_search_candidate(
    mut candidate: SearchCandidate,
    groups: &[SearchQueryGroup],
    normalized_query: &str,
) -> Option<ScoredSearchCandidate> {
    let mut total_score = 0;
    let mut matched_groups = 0;
    let mut reasons = Vec::new();
    for group in groups {
        let mut best: Option<(i64, String)> = None;
        for variant in &group.variants {
            let fields = [
                ("タイトル", candidate.normalized_title.as_str(), 8),
                ("症状", candidate.symptoms.as_str(), 8),
                ("エラーコード", candidate.error_codes.as_str(), 8),
                ("タグ", candidate.normalized_tags.as_str(), 6),
                ("検索用語", candidate.search_terms.as_str(), 6),
                ("対象", candidate.targets.as_str(), 6),
                ("概要", candidate.normalized_summary.as_str(), 4),
                ("想定原因", candidate.causes.as_str(), 4),
                ("回答", candidate.body.as_str(), 1),
                ("対応手順", candidate.procedures.as_str(), 1),
                ("注意事項", candidate.cautions.as_str(), 1),
            ];
            for (label, value, base_score) in fields {
                if !value.contains(&variant.value) {
                    continue;
                }
                let exact_title = label == "タイトル"
                    && !normalized_query.is_empty()
                    && candidate.normalized_title == normalized_query;
                let exact_error = label == "エラーコード"
                    && candidate
                        .error_codes
                        .split_whitespace()
                        .any(|code| code == variant.value);
                let mut score = if exact_title || exact_error {
                    10
                } else {
                    base_score
                };
                if variant.synonym_from.is_some() {
                    score = (score - 1).max(1);
                }
                let reason = if let Some(source) = &variant.synonym_from {
                    format!("同義語：{source} → {}（{label}）", variant.value)
                } else {
                    format!("{label}「{}」", group.display)
                };
                if best.as_ref().is_none_or(|(current, _)| score > *current) {
                    best = Some((score, reason));
                }
            }
        }
        if let Some((score, reason)) = best {
            total_score += score;
            matched_groups += 1;
            reasons.push((score, reason));
        }
    }
    if matched_groups == 0 {
        return None;
    }
    reasons.sort_by(|left, right| right.0.cmp(&left.0).then_with(|| left.1.cmp(&right.1)));
    let mut unique_reasons = HashSet::new();
    candidate.item.match_reasons = reasons
        .into_iter()
        .map(|(_, reason)| reason)
        .filter(|reason| unique_reasons.insert(reason.clone()))
        .take(3)
        .collect();
    Some(ScoredSearchCandidate {
        item: candidate.item,
        score: total_score,
        matched_groups,
    })
}

fn compare_search_sort(
    left: &ArticleListItem,
    right: &ArticleListItem,
    sort: SearchSort,
) -> Ordering {
    match sort {
        SearchSort::UpdatedDesc => right.updated_at.cmp(&left.updated_at),
        SearchSort::UpdatedAsc => left.updated_at.cmp(&right.updated_at),
        SearchSort::ImportanceDesc => right.importance.cmp(&left.importance),
        SearchSort::ImportanceAsc => left.importance.cmp(&right.importance),
    }
}

fn validate_synonym_values(
    display_name: &str,
    terms: &[String],
) -> AppResult<(String, Vec<(String, String)>)> {
    let display_name = display_name.trim();
    if display_name.is_empty()
        || display_name.chars().count() > 100
        || display_name.contains('\r')
        || display_name.contains('\n')
    {
        return Err(AppError::new(
            "SYN-001",
            "代表語は1～100文字の1行テキストで入力してください。",
            "代表語を確認して、もう一度保存してください。",
        ));
    }
    if terms.len() > 50 {
        return Err(AppError::new(
            "SYN-001",
            "同義語は50件以内で登録してください。",
            "不要な同義語を削除して、もう一度保存してください。",
        ));
    }
    let mut unique = HashSet::new();
    let mut values = Vec::new();
    for term in std::iter::once(display_name).chain(terms.iter().map(String::as_str)) {
        let term = term.trim();
        if term.is_empty()
            || term.chars().count() > 100
            || term.contains('\r')
            || term.contains('\n')
        {
            return Err(AppError::new(
                "SYN-001",
                "同義語は1～100文字の1行テキストで入力してください。",
                "空の項目や改行を削除し、入力内容を短くしてください。",
            ));
        }
        let normalized = normalize(term);
        if unique.insert(normalized.clone()) {
            values.push((term.to_owned(), normalized));
        }
    }
    Ok((display_name.to_owned(), values))
}

fn synonym_group_not_found() -> AppError {
    AppError::new(
        "SYN-002",
        "指定した同義語グループが見つかりません。",
        "同義語一覧を更新して、選び直してください。",
    )
}

fn history_date_bounds(
    start_date: Option<&str>,
    end_date: Option<&str>,
) -> AppResult<(Option<String>, Option<String>)> {
    let parse_date = |value: &str, label: &str| {
        NaiveDate::parse_from_str(value, "%Y-%m-%d").map_err(|_| {
            AppError::new(
                "LOG-002",
                format!("{label}の日付形式が正しくありません。"),
                "日付を年-月-日の形式で指定してください。",
            )
        })
    };
    let start = start_date
        .filter(|value| !value.trim().is_empty())
        .map(|value| parse_date(value.trim(), "開始日"))
        .transpose()?;
    let end = end_date
        .filter(|value| !value.trim().is_empty())
        .map(|value| parse_date(value.trim(), "終了日"))
        .transpose()?;
    if let (Some(start), Some(end)) = (start, end)
        && start > end
    {
        return Err(AppError::new(
            "LOG-002",
            "開始日は終了日以前にしてください。",
            "履歴の期間を確認して、もう一度お試しください。",
        ));
    }
    let start_at = start.map(|date| format!("{date}T00:00:00+00:00"));
    let end_before = end
        .map(|date| {
            date.checked_add_days(Days::new(1)).ok_or_else(|| {
                AppError::new(
                    "LOG-002",
                    "終了日を処理できませんでした。",
                    "終了日を確認して、もう一度お試しください。",
                )
            })
        })
        .transpose()?
        .map(|date| format!("{date}T00:00:00+00:00"));
    Ok((start_at, end_before))
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

fn merge_target_visibility_error() -> AppError {
    AppError::new(
        "ART-009",
        "統合先になっているFAQは、非表示・下書き・廃止・削除にできません。",
        "FAQ管理画面で元FAQの統合済み設定を解除してから、もう一度お試しください。",
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
    use crate::models::{SearchScope, SearchSort};
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
        assert_eq!(database.schema_version().unwrap(), 7);
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
        let merge_relations_table: i64 = database
            .connection
            .query_row(
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'article_merge_relations'",
                [],
                |row| row.get(0),
            )
            .unwrap();
        assert_eq!(merge_relations_table, 1);
        let management_code_columns: i64 = database
            .connection
            .query_row(
                "SELECT (SELECT COUNT(*) FROM pragma_table_info('categories') WHERE name = 'management_code') + (SELECT COUNT(*) FROM pragma_table_info('articles') WHERE name = 'management_code')",
                [],
                |row| row.get(0),
            )
            .unwrap();
        assert_eq!(management_code_columns, 2);
    }

    #[test]
    fn display_settings_default_to_green_with_category_titles_and_persist() {
        let (_directory, database) = temporary_database();

        assert_eq!(database.get_settings().unwrap(), AppSettings::default());

        let settings = AppSettings {
            color_theme: crate::models::ColorTheme::Blue,
            show_top_category_in_title: false,
            show_mascot: false,
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
        assert!(settings.show_mascot);
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

        assert_eq!(target.schema_version().unwrap(), 7);
        let article = target.get_article("article").unwrap();
        assert_eq!(article.title, "旧FAQ");
        assert!(!article.is_hidden);
        assert!(article.new_badge_until.is_none());
        let category_management_code: String = target
            .connection
            .query_row(
                "SELECT management_code FROM categories WHERE id = 'cat'",
                [],
                |row| row.get(0),
            )
            .unwrap();
        let article_management_code: String = target
            .connection
            .query_row(
                "SELECT management_code FROM articles WHERE id = 'article'",
                [],
                |row| row.get(0),
            )
            .unwrap();
        assert_eq!(category_management_code, "CAT-00001");
        assert_eq!(article_management_code, "FAQ-00001");
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
    fn category_names_are_unique_after_normalization_within_each_parent() {
        let (_directory, mut database) = temporary_database();
        let parent_a = database.create_category("親A", None).unwrap();
        let parent_b = database.create_category("親B", None).unwrap();
        database
            .create_category("ＰＣ", Some(&parent_a.id))
            .unwrap();

        let duplicate = database
            .create_category("pc", Some(&parent_a.id))
            .unwrap_err();
        assert_eq!(duplicate.code, "CAT-004");
        assert!(database.create_category("pc", Some(&parent_b.id)).is_ok());
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
    fn category_reordering_swaps_only_siblings_and_persists_order() {
        let (_directory, mut database) = temporary_database();
        let root_a = database.create_category("A", None).unwrap();
        let root_b = database.create_category("B", None).unwrap();
        let child_a = database.create_category("A-1", Some(&root_a.id)).unwrap();
        let child_b = database.create_category("A-2", Some(&root_a.id)).unwrap();

        let reordered = database
            .reorder_category(&root_b.id, CategoryMoveDirection::Up)
            .unwrap();
        assert_eq!(
            reordered
                .iter()
                .filter(|category| category.depth == 1)
                .map(|category| category.name.as_str())
                .collect::<Vec<_>>(),
            ["B", "A"]
        );
        assert_eq!(
            reordered
                .iter()
                .filter(|category| category.parent_id.as_deref() == Some(root_a.id.as_str()))
                .map(|category| category.name.as_str())
                .collect::<Vec<_>>(),
            ["A-1", "A-2"]
        );
        let reordered = database
            .reorder_category(&child_b.id, CategoryMoveDirection::Up)
            .unwrap();
        assert_eq!(
            reordered
                .iter()
                .filter(|category| category.parent_id.as_deref() == Some(root_a.id.as_str()))
                .map(|category| category.id.as_str())
                .collect::<Vec<_>>(),
            [child_b.id.as_str(), child_a.id.as_str()]
        );
        let unchanged = database
            .reorder_category(&root_b.id, CategoryMoveDirection::Up)
            .unwrap();
        assert_eq!(unchanged[0].id, root_b.id);
    }

    #[test]
    fn management_codes_are_prefixed_sequential_and_never_reused() {
        let (_directory, mut database) = temporary_database();
        let first_category = database.create_category("一時分類", None).unwrap();
        let first_category_code: String = database
            .connection
            .query_row(
                "SELECT management_code FROM categories WHERE id = ?1",
                [&first_category.id],
                |row| row.get(0),
            )
            .unwrap();
        assert_eq!(first_category_code, "CAT-00001");
        database.delete_category(&first_category.id).unwrap();

        let category = database.create_category("継続分類", None).unwrap();
        let category_code: String = database
            .connection
            .query_row(
                "SELECT management_code FROM categories WHERE id = ?1",
                [&category.id],
                |row| row.get(0),
            )
            .unwrap();
        assert_eq!(category_code, "CAT-00002");

        let body = json!({"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"回答"}]}]});
        let first_article_id = Uuid::now_v7().to_string();
        database
            .save_article(ArticleRecord {
                id: &first_article_id,
                is_new: true,
                category_id: &category.id,
                title: "最初のFAQ",
                summary: "最初の概要",
                body_doc: &body,
                body_plain_text: "回答",
                status: "published",
                importance: 1,
                new_badge_until: None,
                updated_badge_until: None,
                is_hidden: false,
                attachments: &[],
            })
            .unwrap();
        database.delete_article(&first_article_id).unwrap();

        let second_article_id = Uuid::now_v7().to_string();
        database
            .save_article(ArticleRecord {
                id: &second_article_id,
                is_new: true,
                category_id: &category.id,
                title: "次のFAQ",
                summary: "次の概要",
                body_doc: &body,
                body_plain_text: "回答",
                status: "published",
                importance: 1,
                new_badge_until: None,
                updated_badge_until: None,
                is_hidden: false,
                attachments: &[],
            })
            .unwrap();
        let second_article_code: String = database
            .connection
            .query_row(
                "SELECT management_code FROM articles WHERE id = ?1",
                [&second_article_id],
                |row| row.get(0),
            )
            .unwrap();
        assert_eq!(second_article_code, "FAQ-00002");
    }

    #[test]
    fn management_codes_continue_beyond_five_digits() {
        let (_directory, mut database) = temporary_database();
        database
            .connection
            .execute(
                "UPDATE management_code_sequences SET next_value = 100000 WHERE entity_type = 'category'",
                [],
            )
            .unwrap();
        let category = database.create_category("大規模分類", None).unwrap();
        let category_code: String = database
            .connection
            .query_row(
                "SELECT management_code FROM categories WHERE id = ?1",
                [&category.id],
                |row| row.get(0),
            )
            .unwrap();
        assert_eq!(category_code, "CAT-100000");

        database
            .connection
            .execute(
                "UPDATE management_code_sequences SET next_value = 100000 WHERE entity_type = 'article'",
                [],
            )
            .unwrap();
        let article_id = Uuid::now_v7().to_string();
        let body = json!({"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"回答"}]}]});
        database
            .save_article(ArticleRecord {
                id: &article_id,
                is_new: true,
                category_id: &category.id,
                title: "10万件境界のFAQ",
                summary: "管理IDの桁数を確認します",
                body_doc: &body,
                body_plain_text: "回答",
                status: "published",
                importance: 1,
                new_badge_until: None,
                updated_badge_until: None,
                is_hidden: false,
                attachments: &[],
            })
            .unwrap();
        let article_code: String = database
            .connection
            .query_row(
                "SELECT management_code FROM articles WHERE id = ?1",
                [&article_id],
                |row| row.get(0),
            )
            .unwrap();
        assert_eq!(article_code, "FAQ-100000");
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
                scope: SearchScope::Descendants,
                include_drafts: false,
                page: 1,
                sort: SearchSort::UpdatedDesc,
            })
            .unwrap();
        assert!(public_results.items.is_empty());
        let management_results = database
            .search_articles(&SearchArticlesInput {
                query: "画面".into(),
                category_id: None,
                scope: SearchScope::Descendants,
                include_drafts: true,
                page: 1,
                sort: SearchSort::UpdatedDesc,
            })
            .unwrap();
        assert_eq!(management_results.items.len(), 1);
    }

    #[test]
    fn article_status_and_audit_users_follow_manual_delete_and_restore_operations() {
        let (_directory, mut database) = temporary_database();
        let creator = database
            .create_user("creator", "作成者", "", UserRole::User)
            .unwrap();
        let editor = database
            .create_user("editor", "更新者", "", UserRole::User)
            .unwrap();
        let deleter = database
            .create_user("deleter", "削除者", "", UserRole::User)
            .unwrap();
        let restorer = database
            .create_user("restorer", "復元者", "", UserRole::User)
            .unwrap();
        let category = database.create_category("監査", None).unwrap();
        let article_id = Uuid::now_v7().to_string();
        let body = json!({"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"回答"}]}]});
        let created = database
            .save_article_as(
                ArticleRecord {
                    id: &article_id,
                    is_new: true,
                    category_id: &category.id,
                    title: "監査対象FAQ",
                    summary: "状態と担当者を確認します",
                    body_doc: &body,
                    body_plain_text: "回答",
                    status: "draft",
                    importance: 1,
                    new_badge_until: None,
                    updated_badge_until: None,
                    is_hidden: false,
                    attachments: &[],
                },
                &creator.id,
            )
            .unwrap();
        assert_eq!(created.status, "draft");
        assert_eq!(created.created_by_display_name, "作成者");
        assert_eq!(created.updated_by_display_name, "作成者");

        let published = database
            .save_article_as(
                ArticleRecord {
                    id: &article_id,
                    is_new: false,
                    category_id: &category.id,
                    title: "監査対象FAQ",
                    summary: "状態と担当者を確認します",
                    body_doc: &body,
                    body_plain_text: "回答",
                    status: "published",
                    importance: 1,
                    new_badge_until: None,
                    updated_badge_until: None,
                    is_hidden: false,
                    attachments: &[],
                },
                &editor.id,
            )
            .unwrap();
        assert_eq!(published.status, "published");
        assert_eq!(published.created_by_display_name, "作成者");
        assert_eq!(published.updated_by_display_name, "更新者");

        let archived = database
            .save_article_as(
                ArticleRecord {
                    id: &article_id,
                    is_new: false,
                    category_id: &category.id,
                    title: "監査対象FAQ",
                    summary: "状態と担当者を確認します",
                    body_doc: &body,
                    body_plain_text: "回答",
                    status: "archived",
                    importance: 1,
                    new_badge_until: None,
                    updated_badge_until: None,
                    is_hidden: false,
                    attachments: &[],
                },
                &editor.id,
            )
            .unwrap();
        assert_eq!(archived.status, "archived");

        let deleted = database
            .delete_article_as(&article_id, &deleter.id)
            .unwrap();
        assert!(deleted.deleted_at.is_some());
        assert_eq!(deleted.created_by_display_name, "作成者");
        assert_eq!(deleted.updated_by_display_name, "削除者");

        let restored = database
            .restore_article_as(&article_id, &restorer.id)
            .unwrap();
        assert!(restored.deleted_at.is_none());
        assert_eq!(restored.status, "archived");
        assert_eq!(restored.created_by_display_name, "作成者");
        assert_eq!(restored.updated_by_display_name, "復元者");
    }

    #[test]
    fn normalizes_width_case_and_spaces() {
        assert_eq!(normalize("  ＰＣ  Setup "), "pc setup");
    }

    #[test]
    fn public_search_returns_fifty_items_per_page_with_the_total_count() {
        let (_directory, mut database) = temporary_database();
        let category = database.create_category("運用", None).unwrap();
        let body = json!({"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"確認します"}]}]});
        for index in 0..51 {
            let article_id = Uuid::now_v7().to_string();
            let title = format!("運用FAQ {index:02}");
            let summary = format!("運用手順 {index:02}");
            database
                .save_article(ArticleRecord {
                    id: &article_id,
                    is_new: true,
                    category_id: &category.id,
                    title: &title,
                    summary: &summary,
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
        }

        let first = database
            .search_articles(&SearchArticlesInput {
                query: String::new(),
                category_id: None,
                scope: SearchScope::Descendants,
                include_drafts: false,
                page: 1,
                sort: SearchSort::UpdatedDesc,
            })
            .unwrap();
        let second = database
            .search_articles(&SearchArticlesInput {
                query: String::new(),
                category_id: None,
                scope: SearchScope::Descendants,
                include_drafts: false,
                page: 2,
                sort: SearchSort::UpdatedDesc,
            })
            .unwrap();

        assert_eq!(first.total, 51);
        assert_eq!(first.page_size, 50);
        assert_eq!(first.items.len(), 50);
        assert_eq!(second.total, 51);
        assert_eq!(second.page, 2);
        assert_eq!(second.items.len(), 1);
        assert!(!first.items.iter().any(|item| item.id == second.items[0].id));
    }

    #[test]
    fn public_search_sorts_all_results_by_update_date_or_importance() {
        let (_directory, mut database) = temporary_database();
        let category = database.create_category("並び替え", None).unwrap();
        let body = json!({"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"確認します"}]}]});
        let records = [
            ("重要度が高いFAQ", 3, "2026-01-01T00:00:00Z"),
            ("中間のFAQ", 2, "2026-02-01T00:00:00Z"),
            ("更新日が新しいFAQ", 1, "2026-03-01T00:00:00Z"),
        ];
        for (title, importance, updated_at) in records {
            let article_id = Uuid::now_v7().to_string();
            database
                .save_article(ArticleRecord {
                    id: &article_id,
                    is_new: true,
                    category_id: &category.id,
                    title,
                    summary: "並び替えを確認します",
                    body_doc: &body,
                    body_plain_text: "確認します",
                    status: "published",
                    importance,
                    new_badge_until: None,
                    updated_badge_until: None,
                    is_hidden: false,
                    attachments: &[],
                })
                .unwrap();
            database
                .connection
                .execute(
                    "UPDATE articles SET updated_at = ?1 WHERE id = ?2",
                    params![updated_at, article_id],
                )
                .unwrap();
        }

        let sorted_titles = |sort| {
            database
                .search_articles(&SearchArticlesInput {
                    query: String::new(),
                    category_id: None,
                    scope: SearchScope::Descendants,
                    include_drafts: false,
                    page: 1,
                    sort,
                })
                .unwrap()
                .items
                .into_iter()
                .map(|item| item.title)
                .collect::<Vec<_>>()
        };

        assert_eq!(
            sorted_titles(SearchSort::UpdatedDesc),
            ["更新日が新しいFAQ", "中間のFAQ", "重要度が高いFAQ"]
        );
        assert_eq!(
            sorted_titles(SearchSort::UpdatedAsc),
            ["重要度が高いFAQ", "中間のFAQ", "更新日が新しいFAQ"]
        );
        assert_eq!(
            sorted_titles(SearchSort::ImportanceDesc),
            ["重要度が高いFAQ", "中間のFAQ", "更新日が新しいFAQ"]
        );
        assert_eq!(
            sorted_titles(SearchSort::ImportanceAsc),
            ["更新日が新しいFAQ", "中間のFAQ", "重要度が高いFAQ"]
        );
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
                    scope: SearchScope::Descendants,
                    include_drafts: true,
                    page: 1,
                    sort: SearchSort::UpdatedDesc,
                })
                .unwrap()
                .items
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
    fn merge_sources_are_hidden_from_search_only_after_explicit_marking_and_can_be_restored() {
        let (_directory, mut database) = temporary_database();
        let category = database.create_category("PC", None).unwrap();
        let body = json!({"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"回答"}]}]});
        let mut sources = Vec::new();
        for (index, title) in ["元FAQ A", "元FAQ B"].into_iter().enumerate() {
            let id = Uuid::now_v7().to_string();
            let article = database
                .save_article(ArticleRecord {
                    id: &id,
                    is_new: true,
                    category_id: &category.id,
                    title,
                    summary: "元FAQの概要",
                    body_doc: &body,
                    body_plain_text: &format!("回答{index}"),
                    status: "published",
                    importance: 1,
                    new_badge_until: None,
                    updated_badge_until: None,
                    is_hidden: false,
                    attachments: &[],
                })
                .unwrap();
            sources.push(CodexSourceArticle {
                article_id: article.id,
                source_updated_at: article.updated_at,
            });
        }

        let request_id = Uuid::now_v7().to_string();
        let target_id = Uuid::now_v7().to_string();
        database
            .record_codex_proposal(&CodexFaqProposal {
                format_version: 2,
                request_id: request_id.clone(),
                series_id: Some(request_id.clone()),
                created_at: Utc::now().to_rfc3339(),
                proposal_kind: CodexProposalKind::Merge,
                source_articles: sources.clone(),
                faq: crate::models::CodexFaqDraft {
                    title: "統合FAQ".into(),
                    summary: "統合した概要".into(),
                    body_doc: body.clone(),
                    importance: 2,
                },
                existing_category_candidates: vec![],
                new_category_proposal: None,
            })
            .unwrap();
        let (target, _) = database
            .accept_codex_proposal(CodexProposalArticleRecord {
                request_id: &request_id,
                proposal_kind: CodexProposalKind::Merge,
                source_articles: &sources,
                article_id: &target_id,
                category_id: &category.id,
                new_category: None,
                title: "統合FAQ",
                summary: "統合した概要",
                body_doc: &body,
                body_plain_text: "統合した回答",
                importance: 2,
            })
            .unwrap();
        assert_eq!(target.status, "draft");
        assert!(
            database
                .requires_new_badge_for_merge_publication(&target_id)
                .unwrap()
        );
        let context = database
            .get_codex_merge_publication_context(&target_id)
            .unwrap()
            .unwrap();
        assert!(context.can_mark_merged);
        assert!(!context.all_sources_merged);

        database
            .save_article(ArticleRecord {
                id: &target_id,
                is_new: false,
                category_id: &category.id,
                title: "統合FAQ",
                summary: "統合した概要",
                body_doc: &body,
                body_plain_text: "統合した回答",
                status: "published",
                importance: 2,
                new_badge_until: Some("2026-08-31"),
                updated_badge_until: None,
                is_hidden: false,
                attachments: &[],
            })
            .unwrap();
        assert_eq!(
            database
                .mark_codex_merge_sources(&target_id)
                .unwrap()
                .marked_count,
            2
        );

        let public = database
            .search_articles(&SearchArticlesInput {
                query: String::new(),
                category_id: None,
                scope: SearchScope::Descendants,
                include_drafts: false,
                page: 1,
                sort: SearchSort::UpdatedDesc,
            })
            .unwrap();
        assert_eq!(public.items.len(), 1);
        assert_eq!(public.items[0].id, target_id);
        let source = database.get_article(&sources[0].article_id).unwrap();
        assert_eq!(
            source
                .merge_info
                .as_ref()
                .map(|info| info.target_article_id.as_str()),
            Some(target_id.as_str())
        );
        let hidden_target_error = database
            .save_article(ArticleRecord {
                id: &target_id,
                is_new: false,
                category_id: &category.id,
                title: "統合FAQ",
                summary: "統合した概要",
                body_doc: &body,
                body_plain_text: "統合した回答",
                status: "published",
                importance: 2,
                new_badge_until: Some("2026-08-31"),
                updated_badge_until: None,
                is_hidden: true,
                attachments: &[],
            })
            .unwrap_err();
        assert_eq!(hidden_target_error.code, "ART-009");
        assert_eq!(
            database.delete_article(&target_id).unwrap_err().code,
            "ART-009"
        );

        let restored = database
            .clear_article_merge(&sources[0].article_id)
            .unwrap();
        assert!(restored.merge_info.is_none());
        let public = database
            .search_articles(&SearchArticlesInput {
                query: String::new(),
                category_id: None,
                scope: SearchScope::Descendants,
                include_drafts: false,
                page: 1,
                sort: SearchSort::UpdatedDesc,
            })
            .unwrap();
        assert_eq!(public.items.len(), 2);

        database
            .clear_article_merge(&sources[1].article_id)
            .unwrap();
        database
            .connection
            .execute(
                "UPDATE articles SET updated_at = '2099-01-01T00:00:00Z' WHERE id = ?1",
                [&sources[1].article_id],
            )
            .unwrap();
        let stale_error = database.mark_codex_merge_sources(&target_id).unwrap_err();
        assert_eq!(stale_error.code, "CDX-012");
        assert!(
            database
                .get_article(&sources[0].article_id)
                .unwrap()
                .merge_info
                .is_none()
        );
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
        let reviewer = database
            .create_user("codex-reviewer", "Codex確認者", "", UserRole::User)
            .unwrap();
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
            .accept_codex_revision_as(
                CodexProposalRevisionRecord {
                    request_id: &request_id,
                    source_article: &source,
                    title: "読みやすい質問",
                    summary: "読みやすい概要",
                    body_doc: &revised_body,
                    body_plain_text: "読みやすい回答",
                    importance: 2,
                },
                &reviewer.id,
            )
            .unwrap();
        assert_eq!(revised.status, "published");
        assert_eq!(revised.category_id, category.id);
        assert!(revised.is_hidden);
        assert_eq!(revised.new_badge_until.as_deref(), Some("2026-08-31"));
        assert_eq!(revised.title, "読みやすい質問");
        assert_eq!(revised.updated_by_display_name, "Codex確認者");

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
    fn synonym_groups_merge_normalized_duplicates_and_confirm_cross_group_conflicts() {
        let (_directory, mut database) = temporary_database();
        let first = database
            .save_synonym_group(
                None,
                "パソコン",
                &["PC".into(), "ＰＣ".into(), "コンピューター".into()],
                false,
            )
            .unwrap();
        assert_eq!(first.terms, ["PC", "コンピューター"]);
        let conflict = database
            .save_synonym_group(None, "端末", &["ｐｃ".into()], false)
            .unwrap_err();
        assert_eq!(conflict.code, "SYN-003");
        let second = database
            .save_synonym_group(None, "端末", &["ｐｃ".into()], true)
            .unwrap();
        assert_eq!(database.list_synonym_groups().unwrap().len(), 2);

        let updated = database
            .save_synonym_group(
                Some(&first.id),
                "パーソナルコンピューター",
                &["PC".into()],
                true,
            )
            .unwrap();
        assert_eq!(updated.display_name, "パーソナルコンピューター");
        database.delete_synonym_group(&second.id).unwrap();
        assert_eq!(database.list_synonym_groups().unwrap(), vec![updated]);
    }

    #[test]
    fn faq_details_and_bidirectional_relations_are_saved_and_validated_atomically() {
        let (_directory, mut database) = temporary_database();
        let category = database.create_category("PC", None).unwrap();
        let body = json!({"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"回答"}]}]});
        let related_id = Uuid::now_v7().to_string();
        database
            .save_article(ArticleRecord {
                id: &related_id,
                is_new: true,
                category_id: &category.id,
                title: "関連するネットワークFAQ",
                summary: "関連概要",
                body_doc: &body,
                body_plain_text: "回答",
                status: "published",
                importance: 1,
                new_badge_until: None,
                updated_badge_until: None,
                is_hidden: false,
                attachments: &[],
            })
            .unwrap();

        let article_id = Uuid::now_v7().to_string();
        let symptoms = vec!["画面が真っ暗".to_owned()];
        let causes = vec!["表示先の切替".to_owned()];
        let targets = vec!["Windows 11".to_owned(), "パソコン".to_owned()];
        let error_codes = vec!["0x80070005".to_owned()];
        let procedures = vec!["Windowsキーを押す".to_owned()];
        let cautions = vec!["未保存のファイルを閉じる".to_owned()];
        let tags = vec!["ディスプレイ".to_owned()];
        let search_terms = vec!["ブラックスクリーン".to_owned()];
        let related_article_ids = vec![related_id.clone()];
        let details = ArticleDetailsRecord {
            symptoms: &symptoms,
            causes: &causes,
            targets: &targets,
            error_codes: &error_codes,
            procedures: &procedures,
            cautions: &cautions,
            tags: &tags,
            search_terms: &search_terms,
            related_article_ids: &related_article_ids,
        };
        let saved = database
            .save_article_with_details_as(
                ArticleRecord {
                    id: &article_id,
                    is_new: true,
                    category_id: &category.id,
                    title: "画面の確認方法",
                    summary: "表示先を確認します",
                    body_doc: &body,
                    body_plain_text: "回答",
                    status: "published",
                    importance: 1,
                    new_badge_until: None,
                    updated_badge_until: None,
                    is_hidden: false,
                    attachments: &[],
                },
                &details,
                INITIAL_ADMIN_USER_ID,
            )
            .unwrap();
        assert_eq!(saved.symptoms, symptoms);
        assert_eq!(saved.causes, causes);
        assert_eq!(saved.targets, targets);
        assert_eq!(saved.error_codes, error_codes);
        assert_eq!(saved.procedures, procedures);
        assert_eq!(saved.cautions, cautions);
        assert_eq!(saved.tags, tags);
        assert_eq!(saved.search_terms, search_terms);
        assert_eq!(saved.related_articles[0].id, related_id);
        assert_eq!(
            database.get_article(&related_id).unwrap().related_articles[0].id,
            article_id
        );
        let candidates = database
            .search_related_article_candidates(Some(&article_id), "ネットワーク")
            .unwrap();
        assert_eq!(candidates.len(), 1);
        assert!(candidates[0].is_related);
        let metadata_results = database
            .search_articles(&SearchArticlesInput {
                query: "ブラックスクリーン".into(),
                category_id: None,
                scope: SearchScope::Descendants,
                include_drafts: false,
                page: 1,
                sort: SearchSort::UpdatedDesc,
            })
            .unwrap();
        assert_eq!(metadata_results.items[0].id, article_id);
        assert!(
            metadata_results.items[0]
                .match_reasons
                .iter()
                .any(|reason| reason.starts_with("検索用語"))
        );
        database
            .save_synonym_group(None, "パソコン", &["PC".to_owned()], false)
            .unwrap();
        let synonym_results = database
            .search_articles(&SearchArticlesInput {
                query: " ＰＣ ".into(),
                category_id: None,
                scope: SearchScope::Descendants,
                include_drafts: false,
                page: 1,
                sort: SearchSort::UpdatedDesc,
            })
            .unwrap();
        assert_eq!(synonym_results.items[0].id, article_id);
        assert!(
            synonym_results.items[0]
                .match_reasons
                .iter()
                .any(|reason| reason.contains("同義語"))
        );

        database.delete_article(&related_id).unwrap();
        let related = &database.get_article(&article_id).unwrap().related_articles[0];
        assert!(related.deleted_at.is_some());
        database
            .save_article_with_details_as(
                ArticleRecord {
                    id: &article_id,
                    is_new: false,
                    category_id: &category.id,
                    title: "画面の確認方法",
                    summary: "表示先を確認します",
                    body_doc: &body,
                    body_plain_text: "回答",
                    status: "published",
                    importance: 1,
                    new_badge_until: None,
                    updated_badge_until: None,
                    is_hidden: false,
                    attachments: &[],
                },
                &details,
                INITIAL_ADMIN_USER_ID,
            )
            .unwrap();

        let duplicate_symptoms = vec!["PC".to_owned(), "ＰＣ".to_owned()];
        let invalid_details = ArticleDetailsRecord {
            symptoms: &duplicate_symptoms,
            causes: &[],
            targets: &[],
            error_codes: &[],
            procedures: &[],
            cautions: &[],
            tags: &[],
            search_terms: &[],
            related_article_ids: &[],
        };
        let invalid_id = Uuid::now_v7().to_string();
        let error = database
            .save_article_with_details_as(
                ArticleRecord {
                    id: &invalid_id,
                    is_new: true,
                    category_id: &category.id,
                    title: "重複検査",
                    summary: "検査",
                    body_doc: &body,
                    body_plain_text: "回答",
                    status: "draft",
                    importance: 1,
                    new_badge_until: None,
                    updated_badge_until: None,
                    is_hidden: false,
                    attachments: &[],
                },
                &invalid_details,
                INITIAL_ADMIN_USER_ID,
            )
            .unwrap_err();
        assert_eq!(error.code, "ART-009");
        assert_eq!(
            database.get_article(&invalid_id).unwrap_err().code,
            "ART-004"
        );

        let transaction = database.connection.transaction().unwrap();
        assert_eq!(
            replace_article_relations(&transaction, &article_id, std::slice::from_ref(&article_id))
                .unwrap_err()
                .code,
            "ART-009"
        );
        assert_eq!(
            replace_article_relations(
                &transaction,
                &invalid_id,
                &[related_id.clone(), related_id.clone()],
            )
            .unwrap_err()
            .code,
            "ART-009"
        );
        assert_eq!(
            replace_article_relations(
                &transaction,
                &invalid_id,
                std::slice::from_ref(&related_id),
            )
            .unwrap_err()
            .code,
            "ART-009"
        );
    }

    #[test]
    fn search_scope_distinguishes_current_descendants_and_all_categories() {
        let (_directory, mut database) = temporary_database();
        let root = database.create_category("親分類", None).unwrap();
        let child = database.create_category("子分類", Some(&root.id)).unwrap();
        let other = database.create_category("別分類", None).unwrap();
        let body = json!({"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"回答"}]}]});
        for (category_id, title) in [
            (root.id.as_str(), "親のFAQ"),
            (child.id.as_str(), "子のFAQ"),
            (other.id.as_str(), "別のFAQ"),
        ] {
            let article_id = Uuid::now_v7().to_string();
            database
                .save_article(ArticleRecord {
                    id: &article_id,
                    is_new: true,
                    category_id,
                    title,
                    summary: "概要",
                    body_doc: &body,
                    body_plain_text: "回答",
                    status: "published",
                    importance: 1,
                    new_badge_until: None,
                    updated_badge_until: None,
                    is_hidden: false,
                    attachments: &[],
                })
                .unwrap();
        }
        let search = |scope| {
            database
                .search_articles(&SearchArticlesInput {
                    query: String::new(),
                    category_id: Some(root.id.clone()),
                    scope,
                    include_drafts: false,
                    page: 1,
                    sort: SearchSort::UpdatedDesc,
                })
                .unwrap()
                .total
        };
        assert_eq!(search(SearchScope::Current), 1);
        assert_eq!(search(SearchScope::Descendants), 2);
        assert_eq!(search(SearchScope::All), 3);
        assert_eq!(
            database
                .search_articles(&SearchArticlesInput {
                    query: String::new(),
                    category_id: None,
                    scope: SearchScope::Current,
                    include_drafts: false,
                    page: 1,
                    sort: SearchSort::UpdatedDesc,
                })
                .unwrap()
                .total,
            0
        );
        let article_id: String = database
            .connection
            .query_row(
                "SELECT id FROM articles WHERE category_id = ?1 LIMIT 1",
                [&root.id],
                |row| row.get(0),
            )
            .unwrap();
        let search_log_id = database
            .record_search_log(" 親 FAQ ", Some(&root.id), SearchScope::Current, 1)
            .unwrap();
        database
            .record_article_view(&article_id, Some(&search_log_id))
            .unwrap();
        let stored: (String, String, String, i64) = database
            .connection
            .query_row(
                "SELECT query_text, normalized_query, scope, result_count FROM search_logs WHERE id = ?1",
                [&search_log_id],
                |row| Ok((row.get(0)?, row.get(1)?, row.get(2)?, row.get(3)?)),
            )
            .unwrap();
        assert_eq!(
            stored,
            ("親 FAQ".into(), "親 faq".into(), "current".into(), 1)
        );
        let source_id: String = database
            .connection
            .query_row(
                "SELECT source_search_log_id FROM view_logs WHERE article_id = ?1",
                [&article_id],
                |row| row.get(0),
            )
            .unwrap();
        assert_eq!(source_id, search_log_id);
    }

    #[test]
    fn history_lists_filters_and_deletes_search_and_view_records() {
        let (_directory, mut database) = temporary_database();
        let category = database.create_category("PC", None).unwrap();
        let body = json!({"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"回答"}]}]});
        let article_id = Uuid::now_v7().to_string();
        database
            .save_article(ArticleRecord {
                id: &article_id,
                is_new: true,
                category_id: &category.id,
                title: "ネットワーク確認",
                summary: "接続を確認します",
                body_doc: &body,
                body_plain_text: "回答",
                status: "published",
                importance: 1,
                new_badge_until: None,
                updated_badge_until: None,
                is_hidden: false,
                attachments: &[],
            })
            .unwrap();
        let zero_id = database
            .record_search_log("見つからない語", None, SearchScope::All, 0)
            .unwrap();
        let found_id = database
            .record_search_log("ネットワーク", Some(&category.id), SearchScope::Current, 1)
            .unwrap();
        database
            .record_article_view(&article_id, Some(&found_id))
            .unwrap();
        database
            .connection
            .execute(
                "UPDATE search_logs SET created_at = '2026-08-15T12:00:00+00:00' WHERE id = ?1",
                [&zero_id],
            )
            .unwrap();
        database
            .connection
            .execute(
                "UPDATE search_logs SET created_at = '2026-08-16T12:00:00+00:00' WHERE id = ?1",
                [&found_id],
            )
            .unwrap();
        database
            .connection
            .execute(
                "UPDATE view_logs SET viewed_at = '2026-08-16T13:00:00+00:00' WHERE article_id = ?1",
                [&article_id],
            )
            .unwrap();

        let zero_only = database
            .list_search_logs(&ListSearchLogsInput {
                query: "見つからない".into(),
                start_date: Some("2026-08-15".into()),
                end_date: Some("2026-08-15".into()),
                zero_results_only: true,
                page: 1,
            })
            .unwrap();
        assert_eq!(zero_only.total, 1);
        assert_eq!(zero_only.items[0].id, zero_id);
        let views = database
            .list_view_logs(&ListViewLogsInput {
                query: "ネットワーク".into(),
                start_date: Some("2026-08-16".into()),
                end_date: Some("2026-08-16".into()),
                page: 1,
            })
            .unwrap();
        assert_eq!(views.total, 1);
        assert_eq!(
            views.items[0].source_query_text.as_deref(),
            Some("ネットワーク")
        );
        assert_eq!(
            database
                .list_search_logs(&ListSearchLogsInput {
                    query: String::new(),
                    start_date: Some("2026-08-17".into()),
                    end_date: Some("2026-08-16".into()),
                    zero_results_only: false,
                    page: 1,
                })
                .unwrap_err()
                .code,
            "LOG-002"
        );

        assert_eq!(
            database
                .delete_history(
                    HistoryTarget::Search,
                    Some("2026-08-16"),
                    Some("2026-08-16"),
                    false,
                )
                .unwrap(),
            1
        );
        assert_eq!(
            database
                .list_search_logs(&ListSearchLogsInput {
                    query: String::new(),
                    start_date: None,
                    end_date: None,
                    zero_results_only: false,
                    page: 1,
                })
                .unwrap()
                .total,
            1
        );
        assert!(
            database
                .list_view_logs(&ListViewLogsInput {
                    query: String::new(),
                    start_date: None,
                    end_date: None,
                    page: 1,
                })
                .unwrap()
                .items[0]
                .source_query_text
                .is_none()
        );
        assert_eq!(
            database
                .delete_history(HistoryTarget::View, None, None, true)
                .unwrap(),
            1
        );
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
                    scope: SearchScope::Descendants,
                    include_drafts: true,
                    page: 1,
                    sort: SearchSort::UpdatedDesc,
                })
                .unwrap()
                .items
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
                    scope: SearchScope::Descendants,
                    include_drafts: false,
                    page: 1,
                    sort: SearchSort::UpdatedDesc,
                })
                .unwrap()
                .items
                .len(),
            1
        );
    }

    #[test]
    fn authenticates_empty_password_users_and_keeps_the_last_admin_active() {
        let (_directory, database) = temporary_database();
        let initial = database.authenticate_user("0000", "").unwrap();
        assert_eq!(initial.role, UserRole::Admin);
        assert_eq!(
            database
                .authenticate_user("0000", "wrong")
                .unwrap_err()
                .code,
            "AUTH-001"
        );

        let added = database
            .create_user("operator", "担当者", "", UserRole::User)
            .unwrap();
        assert_eq!(
            database.authenticate_user("operator", "").unwrap().id,
            added.id
        );
        database.set_user_active(&added.id, false).unwrap();
        assert_eq!(
            database.authenticate_user("operator", "").unwrap_err().code,
            "AUTH-001"
        );
        assert_eq!(
            database
                .set_user_active(&initial.id, false)
                .unwrap_err()
                .code,
            "USR-002"
        );
    }

    #[test]
    fn password_policy_only_applies_to_future_user_changes() {
        let (_directory, database) = temporary_database();
        let existing = database
            .create_user("existing", "既存担当者", "", UserRole::User)
            .unwrap();

        let policy = PasswordPolicySettings {
            allow_empty_passwords: false,
        };
        database.save_password_policy(&policy).unwrap();
        assert_eq!(database.get_password_policy().unwrap(), policy);

        assert_eq!(
            database.authenticate_user("existing", "").unwrap().id,
            existing.id
        );
        assert_eq!(
            database
                .create_user("new-empty", "新規担当者", "", UserRole::User)
                .unwrap_err()
                .code,
            "USR-004"
        );

        let new_user = database
            .create_user("new-user", "新規担当者", "password", UserRole::User)
            .unwrap();
        assert_eq!(
            database
                .reset_user_password(&new_user.id, "")
                .unwrap_err()
                .code,
            "USR-004"
        );
        assert_eq!(
            database
                .authenticate_user("new-user", "password")
                .unwrap()
                .id,
            new_user.id
        );
    }

    #[test]
    fn surface_six_digit_management_id_fixture_imports_without_errors() {
        let (directory, mut database) = temporary_database();
        let fixture_path = directory
            .path()
            .join("six-digit-management-id.knowledge-export.json");
        fs::write(
            &fixture_path,
            include_bytes!("../../../tests/release-fixtures/six-digit-management-id.fixture.json"),
        )
        .unwrap();

        let preview = database.inspect_json(&fixture_path).unwrap();
        assert_eq!(preview.error_count, 0, "{:?}", preview.errors);
        assert_eq!(preview.counts.categories, 1);
        assert_eq!(preview.counts.articles, 1);
        assert_eq!(preview.create_count, 2);

        let imported = database
            .import_json(
                &fixture_path,
                &preview.file_sha256,
                INITIAL_ADMIN_USER_ID,
                "before-surface-fixture.faqbackup".into(),
            )
            .unwrap();
        assert_eq!(imported.created_count, 2);

        let category_code: String = database
            .connection
            .query_row(
                "SELECT management_code FROM categories WHERE id = ?1",
                ["0198c6f0-0000-7000-8000-000000000001"],
                |row| row.get(0),
            )
            .unwrap();
        let article_code: String = database
            .connection
            .query_row(
                "SELECT management_code FROM articles WHERE id = ?1",
                ["0198c6f0-0000-7000-8000-000000000002"],
                |row| row.get(0),
            )
            .unwrap();
        assert_eq!(category_code, "CAT-100000");
        assert_eq!(article_code, "FAQ-100000");
    }

    #[test]
    fn json_round_trip_validates_relations_details_and_rolls_back_partial_failure() {
        let (directory, mut source) = temporary_database();
        let category = source.create_category("PC", None).unwrap();
        let body = json!({
            "type": "doc",
            "content": [
                {"type":"paragraph","content":[{"type":"text","text":"回答"}]},
                {"type":"copyBlock","attrs":{"text":r"C:\手順書\確認.xlsx"}}
            ]
        });
        let first_id = Uuid::now_v7().to_string();
        let second_id = Uuid::now_v7().to_string();
        source
            .save_article(ArticleRecord {
                id: &second_id,
                is_new: true,
                category_id: &category.id,
                title: "関連先FAQ",
                summary: "関連先です",
                body_doc: &body,
                body_plain_text: r"回答 C:\手順書\確認.xlsx",
                status: "published",
                importance: 1,
                new_badge_until: None,
                updated_badge_until: None,
                is_hidden: false,
                attachments: &[],
            })
            .unwrap();
        let symptoms = vec!["接続できない".to_owned()];
        let procedures = vec!["LANケーブルを確認する".to_owned()];
        let tags = vec!["ネットワーク".to_owned()];
        let related = vec![second_id.clone()];
        source
            .save_article_with_details_as(
                ArticleRecord {
                    id: &first_id,
                    is_new: true,
                    category_id: &category.id,
                    title: "接続を確認するには？",
                    summary: "接続状態を確認します。",
                    body_doc: &body,
                    body_plain_text: r"回答 C:\手順書\確認.xlsx",
                    status: "published",
                    importance: 2,
                    new_badge_until: None,
                    updated_badge_until: None,
                    is_hidden: false,
                    attachments: &[],
                },
                &ArticleDetailsRecord {
                    symptoms: &symptoms,
                    causes: &[],
                    targets: &[],
                    error_codes: &[],
                    procedures: &procedures,
                    cautions: &[],
                    tags: &tags,
                    search_terms: &[],
                    related_article_ids: &related,
                },
                INITIAL_ADMIN_USER_ID,
            )
            .unwrap();
        source
            .save_synonym_group(None, "パソコン", &["PC".into()], false)
            .unwrap();
        let json_path = directory.path().join("roundtrip.knowledge-export.json");
        let exported = source.export_json(&json_path).unwrap();
        assert_eq!(exported.counts.articles, 2);
        assert_eq!(exported.counts.relations, 1);
        let source_preview = source.inspect_json(&json_path).unwrap();
        assert_eq!(source_preview.error_count, 0);
        assert_eq!(source_preview.create_count, 0);
        assert_eq!(source_preview.update_count, 0);

        let (_target_directory, mut target) = temporary_database();
        let preview = target.inspect_json(&json_path).unwrap();
        assert_eq!(preview.error_count, 0);
        assert!(preview.create_count >= 6);
        let imported = target
            .import_json(
                &json_path,
                &preview.file_sha256,
                INITIAL_ADMIN_USER_ID,
                "before-json.faqbackup".into(),
            )
            .unwrap();
        assert_eq!(imported.updated_count, 0);
        let first = target.get_article(&first_id).unwrap();
        assert_eq!(first.symptoms, symptoms);
        assert_eq!(first.procedures, procedures);
        assert_eq!(first.tags, tags);
        assert_eq!(first.related_articles[0].id, second_id);
        assert_eq!(
            first.body_doc["content"][1]["attrs"]["text"],
            r"C:\手順書\確認.xlsx"
        );
        assert_eq!(target.list_synonym_groups().unwrap()[0].terms, ["PC"]);
        assert_eq!(target.inspect_json(&json_path).unwrap().update_count, 0);

        let mut dangerous: KnowledgeJsonDocument =
            serde_json::from_slice(&fs::read(&json_path).unwrap()).unwrap();
        dangerous.articles[0].body_doc =
            json!({"type":"doc","content":[{"type":"script","attrs":{"src":"bad"}}]});
        let dangerous_path = directory.path().join("dangerous.knowledge-export.json");
        fs::write(
            &dangerous_path,
            serde_json::to_vec_pretty(&dangerous).unwrap(),
        )
        .unwrap();
        assert!(source.inspect_json(&dangerous_path).unwrap().error_count > 0);

        let mut collision: KnowledgeJsonDocument =
            serde_json::from_slice(&fs::read(&json_path).unwrap()).unwrap();
        collision.categories[0].management_code = "CAT-99999".into();
        fs::write(
            &dangerous_path,
            serde_json::to_vec_pretty(&collision).unwrap(),
        )
        .unwrap();
        assert!(source.inspect_json(&dangerous_path).unwrap().error_count > 0);

        for unsupported_version in [0, 99] {
            dangerous.format_version = unsupported_version;
            fs::write(
                &dangerous_path,
                serde_json::to_vec_pretty(&dangerous).unwrap(),
            )
            .unwrap();
            assert_eq!(
                source.inspect_json(&dangerous_path).unwrap_err().code,
                "JSON-003"
            );
        }
        fs::write(&dangerous_path, b"{broken-json").unwrap();
        assert_eq!(
            source.inspect_json(&dangerous_path).unwrap_err().code,
            "JSON-002"
        );

        let (_rollback_directory, mut rollback_target) = temporary_database();
        let preview = rollback_target.inspect_json(&json_path).unwrap();
        assert!(
            rollback_target
                .import_json(
                    &json_path,
                    &preview.file_sha256,
                    "00000000-0000-0000-0000-000000000999",
                    "before-json.faqbackup".into(),
                )
                .is_err()
        );
        assert!(rollback_target.list_categories().unwrap().is_empty());
    }

    #[test]
    #[ignore = "release candidate performance verification uses a large temporary fixture"]
    fn release_performance_fixture_meets_search_and_detail_targets() {
        let (directory, mut database) = temporary_database();
        let fixture_started = std::time::Instant::now();
        let now = "2026-08-16T00:00:00Z";
        let transaction = database.connection.transaction().unwrap();
        for index in 0..1_000 {
            transaction
                .execute(
                    r#"
                    INSERT INTO categories(
                        id, parent_id, name, normalized_name, description,
                        depth, sort_order, created_at, updated_at
                    ) VALUES (?1, NULL, ?2, ?3, '', 1, ?4, ?5, ?5)
                    "#,
                    params![
                        format!("perf-category-{index:04}"),
                        format!("性能分類{index:04}"),
                        format!("性能分類{index:04}"),
                        index,
                        now,
                    ],
                )
                .unwrap();
        }
        let body_doc_json = r#"{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"ネットワーク設定を確認して再起動します"}]}]}"#;
        for index in 0..10_000 {
            let article_id = format!("perf-article-{index:05}");
            let category_id = format!("perf-category-{:04}", index % 1_000);
            let title = format!("ネットワーク設定 FAQ {index:05}");
            let summary = format!("社内PCの接続設定を確認する手順 {index:05}");
            let body = "ネットワーク設定を確認して再起動します";
            transaction
                .execute(
                    r#"
                    INSERT INTO articles(
                        id, category_id, title, normalized_title, summary,
                        body_doc_json, body_format_version, body_plain_text,
                        status, importance, created_at, updated_at,
                        created_by_user_id, updated_by_user_id
                    ) VALUES (?1, ?2, ?3, ?3, ?4, ?5, 2, ?6,
                              'published', ?7, ?8, ?8, ?9, ?9)
                    "#,
                    params![
                        article_id,
                        category_id,
                        title,
                        summary,
                        body_doc_json,
                        body,
                        (index % 3) + 1,
                        now,
                        INITIAL_ADMIN_USER_ID,
                    ],
                )
                .unwrap();
            transaction
                .execute(
                    "INSERT INTO article_search_documents(article_id, title, summary, body) VALUES (?1, ?2, ?3, ?4)",
                    params![article_id, title, summary, body],
                )
                .unwrap();
            transaction
                .execute(
                    "INSERT INTO article_search_fts(article_id, title, summary, body) VALUES (?1, ?2, ?3, ?4)",
                    params![article_id, title, summary, body],
                )
                .unwrap();
        }
        transaction.commit().unwrap();
        let fixture_ms = fixture_started.elapsed().as_millis();
        database
            .save_synonym_group(
                None,
                "ネットワーク",
                &["ネットワーク".into(), "LAN".into()],
                false,
            )
            .unwrap();

        let categories_started = std::time::Instant::now();
        let categories = database.list_categories().unwrap();
        let categories_ms = categories_started.elapsed().as_millis();
        assert_eq!(categories.len(), 1_000);

        let search_started = std::time::Instant::now();
        let search = database
            .search_articles(&SearchArticlesInput {
                query: "LAN 設定".into(),
                category_id: None,
                scope: SearchScope::All,
                include_drafts: false,
                page: 1,
                sort: SearchSort::ImportanceDesc,
            })
            .unwrap();
        let search_ms = search_started.elapsed().as_millis();
        assert_eq!(search.total, 10_000);
        assert_eq!(search.items.len(), 50);

        let detail_started = std::time::Instant::now();
        let detail = database.get_article("perf-article-05000").unwrap();
        let detail_ms = detail_started.elapsed().as_millis();
        assert_eq!(detail.title, "ネットワーク設定 FAQ 05000");

        let history_fixture_started = std::time::Instant::now();
        database
            .connection
            .execute_batch(
                r#"
                WITH digits(value) AS (
                    VALUES (0),(1),(2),(3),(4),(5),(6),(7),(8),(9)
                ), numbers(value) AS (
                    SELECT a.value + b.value * 10 + c.value * 100
                         + d.value * 1000 + e.value * 10000 + f.value * 100000
                      FROM digits a CROSS JOIN digits b CROSS JOIN digits c
                      CROSS JOIN digits d CROSS JOIN digits e CROSS JOIN digits f
                )
                INSERT INTO search_logs(
                    id, query_text, normalized_query, scope,
                    category_id, result_count, created_at
                )
                SELECT printf('perf-log-%06d', value),
                       CASE WHEN value % 10 = 0 THEN '0件検索' ELSE 'ネットワーク' END,
                       CASE WHEN value % 10 = 0 THEN '0件検索' ELSE 'ネットワーク' END,
                       'all', NULL, CASE WHEN value % 10 = 0 THEN 0 ELSE 10 END,
                       '2026-08-16T00:00:00Z'
                  FROM numbers;
                "#,
            )
            .unwrap();
        let history_fixture_ms = history_fixture_started.elapsed().as_millis();
        let history_started = std::time::Instant::now();
        let history = database
            .list_search_logs(&crate::models::ListSearchLogsInput {
                query: String::new(),
                start_date: None,
                end_date: None,
                zero_results_only: true,
                page: 2_000,
            })
            .unwrap();
        let history_ms = history_started.elapsed().as_millis();
        assert_eq!(history.total, 100_000);
        assert_eq!(history.items.len(), 50);

        let csv_started = std::time::Instant::now();
        let csv_path = directory.path().join("performance.knowledge-faq.csv");
        let csv_result = database.export_faq_csv(&csv_path).unwrap();
        let csv_ms = csv_started.elapsed().as_millis();
        assert_eq!(csv_result.exported_count, 10_000);

        let backup_started = std::time::Instant::now();
        let backup_path = directory.path().join("performance-backup.db");
        database.backup_to(&backup_path).unwrap();
        let backup_ms = backup_started.elapsed().as_millis();
        assert!(backup_path.is_file());

        println!(
            "PERF_RESULT fixture_ms={fixture_ms} categories_ms={categories_ms} search_ms={search_ms} detail_ms={detail_ms} history_fixture_ms={history_fixture_ms} history_ms={history_ms} csv_ms={csv_ms} backup_ms={backup_ms} db_bytes={} csv_bytes={}",
            fs::metadata(directory.path().join("knowledge.db"))
                .unwrap()
                .len(),
            fs::metadata(csv_path).unwrap().len(),
        );
        assert!(
            search_ms <= 1_000,
            "10,000件検索が1秒を超えました: {search_ms}ms"
        );
        assert!(
            detail_ms <= 500,
            "FAQ詳細取得が0.5秒を超えました: {detail_ms}ms"
        );
        assert!(
            categories_ms <= 500,
            "1,000分類取得が0.5秒を超えました: {categories_ms}ms"
        );
    }

    #[test]
    fn csv_formula_cells_are_escaped_for_excel_and_restored_on_import() {
        for value in ["=SUM(A1:A2)", "+1", "-1", "@command"] {
            let escaped = safe_excel_cell(value);
            assert_eq!(escaped, format!("'{value}"));
            assert_eq!(unsafed_excel_cell(&escaped), value);
        }
        assert_eq!(safe_excel_cell("通常の文字列"), "通常の文字列");
        assert_eq!(unsafed_excel_cell("'通常の文字列"), "'通常の文字列");
    }

    #[test]
    fn csv_rejects_import_when_every_data_row_has_an_error() {
        let (directory, mut database) = temporary_database();
        let category = database.create_category("CSV検証", None).unwrap();
        let article_id = Uuid::now_v7().to_string();
        let body = json!({"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"回答"}]}]});
        database
            .save_article(ArticleRecord {
                id: &article_id,
                is_new: true,
                category_id: &category.id,
                title: "取込拒否の確認",
                summary: "エラー行を確認します",
                body_doc: &body,
                body_plain_text: "回答",
                status: "published",
                importance: 1,
                new_badge_until: None,
                updated_badge_until: None,
                is_hidden: false,
                attachments: &[],
            })
            .unwrap();
        let csv_path = directory.path().join("invalid.knowledge-faq.csv");
        database.export_faq_csv(&csv_path).unwrap();
        rewrite_csv_cell(&csv_path, 9, "9");

        let preview = database.inspect_faq_csv(&csv_path).unwrap();
        assert_eq!(preview.total_rows, 1);
        assert_eq!(preview.error_count, 1);
        assert_eq!(
            database
                .import_faq_csv(
                    &csv_path,
                    &preview.file_sha256,
                    INITIAL_ADMIN_USER_ID,
                    "not-created.faqbackup".into(),
                )
                .unwrap_err()
                .code,
            "CSV-004"
        );
        assert_eq!(database.get_article(&article_id).unwrap().importance, 1);
    }

    #[test]
    fn csv_import_rolls_back_all_rows_when_the_transaction_fails() {
        let (directory, mut database) = temporary_database();
        let category = database
            .create_category("CSVトランザクション", None)
            .unwrap();
        let body = json!({"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"回答"}]}]});
        let mut article_ids = Vec::new();
        for title in ["1件目", "2件目"] {
            let article_id = Uuid::now_v7().to_string();
            database
                .save_article(ArticleRecord {
                    id: &article_id,
                    is_new: true,
                    category_id: &category.id,
                    title,
                    summary: "変更前",
                    body_doc: &body,
                    body_plain_text: "回答",
                    status: "published",
                    importance: 1,
                    new_badge_until: None,
                    updated_badge_until: None,
                    is_hidden: false,
                    attachments: &[],
                })
                .unwrap();
            article_ids.push(article_id);
        }
        let csv_path = directory.path().join("rollback.knowledge-faq.csv");
        database.export_faq_csv(&csv_path).unwrap();
        rewrite_all_csv_cells(&csv_path, 5, "変更後");
        let preview = database.inspect_faq_csv(&csv_path).unwrap();
        assert_eq!(preview.update_count, 2);

        assert!(
            database
                .import_faq_csv(
                    &csv_path,
                    &preview.file_sha256,
                    "00000000-0000-0000-0000-000000000999",
                    "not-created.faqbackup".into(),
                )
                .is_err()
        );
        for article_id in article_ids {
            assert_eq!(database.get_article(&article_id).unwrap().summary, "変更前");
        }
    }

    #[test]
    fn csv_round_trip_preserves_rich_body_until_the_answer_cell_changes() {
        let (directory, mut database) = temporary_database();
        let operator = database
            .create_user("writer", "作成担当", "", UserRole::User)
            .unwrap();
        let category = database.create_category("PC", None).unwrap();
        let body = json!({
            "type": "doc",
            "content": [{
                "type": "table",
                "content": [{"type":"tableRow","content":[{"type":"tableCell","content":[{"type":"paragraph","content":[{"type":"text","text":"表の回答"}]}]}]}]
            }]
        });
        let article_id = Uuid::now_v7().to_string();
        let created = database
            .save_article_as(
                ArticleRecord {
                    id: &article_id,
                    is_new: true,
                    category_id: &category.id,
                    title: "表を含むFAQ",
                    summary: "元の概要",
                    body_doc: &body,
                    body_plain_text: "表の回答",
                    status: "published",
                    importance: 1,
                    new_badge_until: None,
                    updated_badge_until: None,
                    is_hidden: false,
                    attachments: &[],
                },
                &operator.id,
            )
            .unwrap();
        assert_eq!(created.created_by_display_name, "作成担当");
        let symptoms = vec!["画面が崩れる".to_owned()];
        let tags = vec!["表示".to_owned()];
        let details = ArticleDetailsRecord {
            symptoms: &symptoms,
            causes: &[],
            targets: &[],
            error_codes: &[],
            procedures: &[],
            cautions: &[],
            tags: &tags,
            search_terms: &[],
            related_article_ids: &[],
        };
        database
            .save_article_with_details_as(
                ArticleRecord {
                    id: &article_id,
                    is_new: false,
                    category_id: &category.id,
                    title: &created.title,
                    summary: &created.summary,
                    body_doc: &created.body_doc,
                    body_plain_text: &created.body_plain_text,
                    status: &created.status,
                    importance: created.importance,
                    new_badge_until: created.new_badge_until.as_deref(),
                    updated_badge_until: created.updated_badge_until.as_deref(),
                    is_hidden: created.is_hidden,
                    attachments: &[],
                },
                &details,
                &operator.id,
            )
            .unwrap();

        let csv_path = directory.path().join("roundtrip.knowledge-faq.csv");
        database.export_faq_csv(&csv_path).unwrap();
        let exported_bytes = fs::read(&csv_path).unwrap();
        assert!(exported_bytes.starts_with(&[0xEF, 0xBB, 0xBF]));
        assert!(exported_bytes.iter().enumerate().all(
            |(index, byte)| *byte != b'\n' || (index > 0 && exported_bytes[index - 1] == b'\r')
        ));
        let exported_content = exported_bytes
            .strip_prefix(&[0xEF, 0xBB, 0xBF])
            .unwrap_or(&exported_bytes);
        let exported_text = String::from_utf8(exported_content.to_vec()).unwrap();
        let password_hash: String = database
            .connection
            .query_row(
                "SELECT password_hash FROM users WHERE id = ?1",
                [&operator.id],
                |row| row.get(0),
            )
            .unwrap();
        assert!(!exported_text.contains(&password_hash));
        assert!(!exported_text.contains(&article_id));
        assert!(!exported_text.contains(&category.id));
        let mut exported_reader = csv::Reader::from_reader(exported_content);
        assert_eq!(exported_reader.headers().unwrap().len(), 17);
        assert!(
            exported_reader
                .headers()
                .unwrap()
                .iter()
                .eq(crate::services::csv_transfer::HEADERS)
        );
        let exported_record = exported_reader.records().next().unwrap().unwrap();
        assert_eq!(exported_record.get(0), Some("2"));
        assert_eq!(exported_record.get(1), Some("FAQ-00001"));
        assert_eq!(exported_record.get(2), Some("CAT-00001"));

        let json_path = directory.path().join("audit.knowledge-export.json");
        database.export_json(&json_path).unwrap();
        let json_text = fs::read_to_string(&json_path).unwrap();
        assert!(!json_text.contains(&password_hash));
        let search_log_id = database
            .record_search_log("表 FAQ", Some(&category.id), SearchScope::Current, 1)
            .unwrap();
        database
            .record_article_view(&article_id, Some(&search_log_id))
            .unwrap();
        let search_logs = database
            .list_search_logs(&crate::models::ListSearchLogsInput {
                query: String::new(),
                start_date: None,
                end_date: None,
                zero_results_only: false,
                page: 1,
            })
            .unwrap();
        let view_logs = database
            .list_view_logs(&crate::models::ListViewLogsInput {
                query: String::new(),
                start_date: None,
                end_date: None,
                page: 1,
            })
            .unwrap();
        assert!(
            !serde_json::to_string(&search_logs)
                .unwrap()
                .contains(&password_hash)
        );
        assert!(
            !serde_json::to_string(&view_logs)
                .unwrap()
                .contains(&password_hash)
        );

        rewrite_csv_cell(&csv_path, 5, "Excelで変更した概要");
        let preview = database.inspect_faq_csv(&csv_path).unwrap();
        assert_eq!(preview.update_count, 1);
        assert_eq!(preview.body_replacement_count, 0);
        database
            .import_faq_csv(
                &csv_path,
                &preview.file_sha256,
                &operator.id,
                "safety.faqbackup".into(),
            )
            .unwrap();
        let summary_only = database.get_article(&article_id).unwrap();
        assert_eq!(summary_only.summary, "Excelで変更した概要");
        assert_eq!(summary_only.body_doc, body);
        assert_eq!(summary_only.symptoms, symptoms);
        assert_eq!(summary_only.tags, tags);

        database
            .save_article_as(
                ArticleRecord {
                    id: &article_id,
                    is_new: false,
                    category_id: &category.id,
                    title: "アプリ内で後から変更したタイトル",
                    summary: &summary_only.summary,
                    body_doc: &summary_only.body_doc,
                    body_plain_text: &summary_only.body_plain_text,
                    status: "published",
                    importance: 1,
                    new_badge_until: None,
                    updated_badge_until: None,
                    is_hidden: false,
                    attachments: &[],
                },
                &operator.id,
            )
            .unwrap();
        let stale_preview = database.inspect_faq_csv(&csv_path).unwrap();
        assert_eq!(stale_preview.stale_overwrite_count, 1);
        database
            .import_faq_csv(
                &csv_path,
                &stale_preview.file_sha256,
                &operator.id,
                "safety.faqbackup".into(),
            )
            .unwrap();
        assert_eq!(
            database.get_article(&article_id).unwrap().title,
            "表を含むFAQ"
        );

        database.export_faq_csv(&csv_path).unwrap();
        let before_file_change = database.inspect_faq_csv(&csv_path).unwrap();
        rewrite_csv_cell(&csv_path, 6, "Excelで置き換えた回答");
        assert_eq!(
            database
                .import_faq_csv(
                    &csv_path,
                    &before_file_change.file_sha256,
                    &operator.id,
                    "safety.faqbackup".into(),
                )
                .unwrap_err()
                .code,
            "CSV-007"
        );
        let preview = database.inspect_faq_csv(&csv_path).unwrap();
        assert_eq!(preview.body_replacement_count, 1);
        database
            .import_faq_csv(
                &csv_path,
                &preview.file_sha256,
                &operator.id,
                "safety.faqbackup".into(),
            )
            .unwrap();
        let replaced = database.get_article(&article_id).unwrap();
        assert_eq!(replaced.body_plain_text, "Excelで置き換えた回答");
        assert_eq!(replaced.body_doc["content"][0]["type"], "paragraph");
        assert_eq!(replaced.updated_by_display_name, "作成担当");
    }

    fn rewrite_csv_cell(path: &Path, column: usize, value: &str) {
        let bytes = fs::read(path).unwrap();
        let content = bytes.strip_prefix(&[0xEF, 0xBB, 0xBF]).unwrap_or(&bytes);
        let mut reader = csv::Reader::from_reader(content);
        let headers = reader.headers().unwrap().clone();
        let mut records = reader.records().map(Result::unwrap).collect::<Vec<_>>();
        records[0] = records[0]
            .iter()
            .enumerate()
            .map(|(index, cell)| if index == column { value } else { cell })
            .collect();
        let mut writer = csv::WriterBuilder::new()
            .terminator(csv::Terminator::CRLF)
            .from_writer(Vec::new());
        writer.write_record(&headers).unwrap();
        for record in records {
            writer.write_record(&record).unwrap();
        }
        let mut output = vec![0xEF, 0xBB, 0xBF];
        output.extend(writer.into_inner().unwrap());
        fs::write(path, output).unwrap();
    }

    fn rewrite_all_csv_cells(path: &Path, column: usize, value: &str) {
        let bytes = fs::read(path).unwrap();
        let content = bytes.strip_prefix(&[0xEF, 0xBB, 0xBF]).unwrap_or(&bytes);
        let mut reader = csv::Reader::from_reader(content);
        let headers = reader.headers().unwrap().clone();
        let records = reader
            .records()
            .map(Result::unwrap)
            .map(|record| {
                record
                    .iter()
                    .enumerate()
                    .map(|(index, cell)| if index == column { value } else { cell })
                    .collect::<csv::StringRecord>()
            })
            .collect::<Vec<_>>();
        let mut writer = csv::WriterBuilder::new()
            .terminator(csv::Terminator::CRLF)
            .from_writer(Vec::new());
        writer.write_record(&headers).unwrap();
        for record in records {
            writer.write_record(&record).unwrap();
        }
        let mut output = vec![0xEF, 0xBB, 0xBF];
        output.extend(writer.into_inner().unwrap());
        fs::write(path, output).unwrap();
    }
}
