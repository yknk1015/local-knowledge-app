use std::{
    collections::{HashMap, HashSet},
    fs,
    path::Path,
    time::Duration,
};

use chrono::{NaiveDate, Utc};
use rusqlite::{Connection, OpenFlags, OptionalExtension, Transaction, backup::Backup, params};
use serde_json::Value;
use sha2::{Digest, Sha256};
use unicode_normalization::UnicodeNormalization;
use uuid::Uuid;

use crate::{
    errors::{AppError, AppResult},
    models::{
        AppSettings, Article, ArticleAttachment, ArticleListItem, ArticleMergeInfo,
        AuthenticatedUser, BackupCounts, Category, CodexFaqProposal, CodexMergePublicationContext,
        CodexMergeSourcePreview, CodexProposalHistoryItem, CodexProposalKind, CodexSourceArticle,
        CsvExportResult, CsvImportPreview, CsvImportPreviewRow, CsvImportResult,
        ManagementArticleListItem, ManagementArticlePage, ManagementArticlesInput,
        MarkCodexMergeSourcesResult, PasswordPolicySettings, SearchArticlePage,
        SearchArticlesInput, UserRole, UserSummary,
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
const CURRENT_SCHEMA_VERSION: i64 = 7;
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

    pub fn save_article_as(
        &mut self,
        article: ArticleRecord<'_>,
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

    pub fn search_articles(&self, input: &SearchArticlesInput) -> AppResult<SearchArticlePage> {
        const PAGE_SIZE: i64 = 50;
        let normalized_query = normalize(input.query.trim());
        let like_query = format!("%{}%", escape_like(&normalized_query));
        let category_id = input.category_id.as_deref();
        let total: i64 = self.connection.query_row(
            r#"
            WITH RECURSIVE selected_categories(id) AS (
                SELECT id FROM categories WHERE id = ?2
                UNION ALL
                SELECT c.id FROM categories c
                JOIN selected_categories parent ON c.parent_id = parent.id
            )
            SELECT COUNT(*)
              FROM articles a
              JOIN article_search_documents search_doc ON search_doc.article_id = a.id
             WHERE a.deleted_at IS NULL
               AND a.is_hidden = 0
               AND NOT EXISTS (
                   SELECT 1 FROM article_merge_relations merge_relation
                    WHERE merge_relation.source_article_id = a.id
               )
               AND (?3 = 1 OR a.status = 'published')
               AND (?2 IS NULL OR a.category_id IN (SELECT id FROM selected_categories))
               AND (?1 = '' OR search_doc.title LIKE ?4 ESCAPE '\'
                    OR search_doc.summary LIKE ?4 ESCAPE '\'
                    OR search_doc.body LIKE ?4 ESCAPE '\')
            "#,
            params![
                &normalized_query,
                category_id,
                input.include_drafts,
                &like_query
            ],
            |row| row.get(0),
        )?;
        let total_pages = ((total + PAGE_SIZE - 1) / PAGE_SIZE).max(1);
        let page = input.page.max(1).min(total_pages);
        let offset = (page - 1) * PAGE_SIZE;
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
               AND NOT EXISTS (
                   SELECT 1 FROM article_merge_relations merge_relation
                    WHERE merge_relation.source_article_id = a.id
               )
               AND (?3 = 1 OR a.status = 'published')
               AND (?2 IS NULL OR a.category_id IN (SELECT id FROM selected_categories))
               AND (?1 = '' OR search_doc.title LIKE ?4 ESCAPE '\'
                    OR search_doc.summary LIKE ?4 ESCAPE '\'
                    OR search_doc.body LIKE ?4 ESCAPE '\')
             ORDER BY CASE WHEN ?7 = 'updatedDesc' THEN a.updated_at END DESC,
                      CASE WHEN ?7 = 'updatedAsc' THEN a.updated_at END ASC,
                      CASE WHEN ?7 = 'importanceDesc' THEN a.importance END DESC,
                      CASE WHEN ?7 = 'importanceAsc' THEN a.importance END ASC,
                      a.updated_at DESC, a.id DESC
             LIMIT ?5 OFFSET ?6
            "#,
        )?;
        let rows = statement.query_map(
            params![
                &normalized_query,
                category_id,
                input.include_drafts,
                &like_query,
                PAGE_SIZE,
                offset,
                input.sort.as_str()
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
        Ok(SearchArticlePage {
            items: rows.collect::<Result<Vec<_>, _>>()?,
            total,
            page,
            page_size: PAGE_SIZE,
        })
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
    use crate::models::SearchSort;
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
                page: 1,
                sort: SearchSort::UpdatedDesc,
            })
            .unwrap();
        assert!(public_results.items.is_empty());
        let management_results = database
            .search_articles(&SearchArticlesInput {
                query: "画面".into(),
                category_id: None,
                include_drafts: true,
                page: 1,
                sort: SearchSort::UpdatedDesc,
            })
            .unwrap();
        assert_eq!(management_results.items.len(), 1);
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
                include_drafts: false,
                page: 1,
                sort: SearchSort::UpdatedDesc,
            })
            .unwrap();
        let second = database
            .search_articles(&SearchArticlesInput {
                query: String::new(),
                category_id: None,
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

        let csv_path = directory.path().join("roundtrip.knowledge-faq.csv");
        database.export_faq_csv(&csv_path).unwrap();
        let exported_bytes = fs::read(&csv_path).unwrap();
        let exported_content = exported_bytes
            .strip_prefix(&[0xEF, 0xBB, 0xBF])
            .unwrap_or(&exported_bytes);
        let exported_text = String::from_utf8(exported_content.to_vec()).unwrap();
        assert!(!exported_text.contains(&article_id));
        assert!(!exported_text.contains(&category.id));
        let mut exported_reader = csv::Reader::from_reader(exported_content);
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
}
