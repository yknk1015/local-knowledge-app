use std::{
    collections::{HashMap, HashSet},
    path::PathBuf,
};

use chrono::{Local, NaiveDate};
use tauri::State;
use tauri_plugin_opener::OpenerExt;
use url::Url;

use crate::{
    AppState,
    errors::{AppError, AppResult},
    models::{
        AcceptCodexProposalInput, AcceptCodexProposalResult, AppSettings, Article,
        AuthenticatedUser, BackupOverview, BackupPreview, BackupResult, Category,
        CodexDelegationKind, CodexDelegationResult, CodexMergePublicationContext,
        CodexProposalInbox, CodexProposalKind, CreateCategoryInput, CreateCodexDelegationInput,
        CreateFullBackupInput, CreateUserInput, CsvExportResult, CsvImportPreview, CsvImportResult,
        DeleteHistoryInput, ExportFaqCsvInput, ExportJsonInput, ImportFaqCsvInput, ImportJsonInput,
        JsonExportResult, JsonImportPreview, JsonImportResult, ListSearchLogsInput,
        ListViewLogsInput, LoginInput, ManagementArticlePage, ManagementArticlesInput,
        MarkCodexMergeSourcesResult, PasswordPolicySettings, RecordArticleViewInput,
        RecordSearchLogInput, RelatedArticleCandidate, ReorderCategoryInput,
        ResetUserPasswordInput, RestoreResult, SaveArticleInput, SaveSynonymGroupInput,
        SearchArticlePage, SearchArticlesInput, SearchLogPage, SearchRelatedArticlesInput,
        SetUserActiveInput, StageArticleImageBytesInput, StagedArticleImage, SynonymGroup,
        SystemInfo, UpdateCategoryInput, UserRole, UserSummary, ViewLogPage,
    },
    repositories::database::{
        ArticleDetailsRecord, ArticleRecord, CodexProposalArticleRecord,
        CodexProposalRevisionRecord, NewCategoryRecord, normalize,
    },
    services::{attachments, backup, codex_proposals, rich_content},
};
use uuid::Uuid;

fn lock_database<'state, 'managed>(
    state: &'state State<'managed, AppState>,
) -> AppResult<std::sync::MutexGuard<'state, crate::repositories::database::Database>> {
    state.database.lock().map_err(|_| {
        AppError::database("データベースが別の処理で利用中のため操作できませんでした。")
    })
}

fn lock_session<'state, 'managed>(
    state: &'state State<'managed, AppState>,
) -> AppResult<std::sync::MutexGuard<'state, Option<AuthenticatedUser>>> {
    state
        .session
        .lock()
        .map_err(|_| AppError::system("ログイン状態を確認できませんでした。"))
}

fn require_user_value(user: Option<AuthenticatedUser>) -> AppResult<AuthenticatedUser> {
    user.ok_or_else(crate::services::auth::login_required_error)
}

fn require_user(state: &State<'_, AppState>) -> AppResult<AuthenticatedUser> {
    require_user_value(lock_session(state)?.clone())
}

fn require_admin_value(user: Option<AuthenticatedUser>) -> AppResult<AuthenticatedUser> {
    let user = require_user_value(user)?;
    if user.role != UserRole::Admin {
        return Err(crate::services::auth::admin_required_error());
    }
    Ok(user)
}

fn require_admin(state: &State<'_, AppState>) -> AppResult<AuthenticatedUser> {
    require_admin_value(lock_session(state)?.clone())
}

fn validate_user_activation(
    current: &AuthenticatedUser,
    input: &SetUserActiveInput,
) -> AppResult<()> {
    if current.id == input.id && !input.is_active {
        return Err(AppError::new(
            "USR-002",
            "ログイン中の利用者自身は利用停止にできません。",
            "別の管理者でログインしてから利用停止にしてください。",
        ));
    }
    Ok(())
}

#[tauri::command]
pub fn login(input: LoginInput, state: State<'_, AppState>) -> AppResult<AuthenticatedUser> {
    if input.login_id.trim().is_empty() {
        return Err(crate::services::auth::authentication_error());
    }
    let user = lock_database(&state)?.authenticate_user(&input.login_id, &input.password)?;
    *lock_session(&state)? = Some(user.clone());
    Ok(user)
}

#[tauri::command]
pub fn logout(state: State<'_, AppState>) -> AppResult<()> {
    *lock_session(&state)? = None;
    Ok(())
}

#[tauri::command]
pub fn get_current_user(state: State<'_, AppState>) -> AppResult<Option<AuthenticatedUser>> {
    Ok(lock_session(&state)?.clone())
}

#[tauri::command]
pub fn list_users(state: State<'_, AppState>) -> AppResult<Vec<UserSummary>> {
    require_admin(&state)?;
    lock_database(&state)?.list_users()
}

#[tauri::command]
pub fn create_user(input: CreateUserInput, state: State<'_, AppState>) -> AppResult<UserSummary> {
    require_admin(&state)?;
    lock_database(&state)?.create_user(
        &input.login_id,
        &input.display_name,
        &input.password,
        input.role,
    )
}

#[tauri::command]
pub fn set_user_active(
    input: SetUserActiveInput,
    state: State<'_, AppState>,
) -> AppResult<UserSummary> {
    let current = require_admin(&state)?;
    validate_user_activation(&current, &input)?;
    lock_database(&state)?.set_user_active(&input.id, input.is_active)
}

#[tauri::command]
pub fn reset_user_password(
    input: ResetUserPasswordInput,
    state: State<'_, AppState>,
) -> AppResult<UserSummary> {
    require_admin(&state)?;
    lock_database(&state)?.reset_user_password(&input.id, &input.password)
}

#[tauri::command]
pub fn get_system_info(state: State<'_, AppState>) -> AppResult<SystemInfo> {
    require_user(&state)?;
    Ok(SystemInfo {
        app_version: env!("CARGO_PKG_VERSION").to_owned(),
        data_root: state.data_root.root().display().to_string(),
        database_path: state.data_root.database_path().display().to_string(),
        codex_category_catalog_path: codex_proposals::category_catalog_path(&state.data_root)
            .display()
            .to_string(),
        codex_inbox_path: state.data_root.codex_inbox_path().display().to_string(),
    })
}

#[tauri::command]
pub fn get_settings(state: State<'_, AppState>) -> AppResult<AppSettings> {
    require_user(&state)?;
    lock_database(&state)?.get_settings()
}

#[tauri::command]
pub fn save_settings(input: AppSettings, state: State<'_, AppState>) -> AppResult<AppSettings> {
    require_user(&state)?;
    lock_database(&state)?.save_settings(&input)?;
    Ok(input)
}

#[tauri::command]
pub fn get_password_policy(state: State<'_, AppState>) -> AppResult<PasswordPolicySettings> {
    require_admin(&state)?;
    lock_database(&state)?.get_password_policy()
}

#[tauri::command]
pub fn save_password_policy(
    input: PasswordPolicySettings,
    state: State<'_, AppState>,
) -> AppResult<PasswordPolicySettings> {
    require_admin(&state)?;
    lock_database(&state)?.save_password_policy(&input)?;
    Ok(input)
}

#[tauri::command]
pub fn list_categories(state: State<'_, AppState>) -> AppResult<Vec<Category>> {
    require_user(&state)?;
    lock_database(&state)?.list_categories()
}

#[tauri::command]
pub fn create_category(
    input: CreateCategoryInput,
    state: State<'_, AppState>,
) -> AppResult<Category> {
    require_user(&state)?;
    let category = lock_database(&state)?.create_category_with_description(
        &input.name,
        &input.description,
        input.parent_id.as_deref(),
    )?;
    refresh_category_catalog_best_effort(&state);
    Ok(category)
}

#[tauri::command]
pub fn update_category(
    input: UpdateCategoryInput,
    state: State<'_, AppState>,
) -> AppResult<Category> {
    require_user(&state)?;
    let category = lock_database(&state)?.update_category_with_description(
        &input.id,
        &input.name,
        &input.description,
        input.parent_id.as_deref(),
    )?;
    refresh_category_catalog_best_effort(&state);
    Ok(category)
}

#[tauri::command]
pub fn reorder_category(
    input: ReorderCategoryInput,
    state: State<'_, AppState>,
) -> AppResult<Vec<Category>> {
    require_user(&state)?;
    let categories = lock_database(&state)?.reorder_category(&input.id, input.direction)?;
    refresh_category_catalog_best_effort(&state);
    Ok(categories)
}

#[tauri::command]
pub fn delete_category(id: String, state: State<'_, AppState>) -> AppResult<()> {
    require_user(&state)?;
    lock_database(&state)?.delete_category(&id)?;
    refresh_category_catalog_best_effort(&state);
    Ok(())
}

#[tauri::command]
pub fn list_codex_proposals(state: State<'_, AppState>) -> AppResult<CodexProposalInbox> {
    require_user(&state)?;
    let categories = lock_database(&state)?.list_categories()?;
    codex_proposals::write_category_catalog(&state.data_root, &categories)?;
    let (proposals, rejected) = codex_proposals::list_proposals(&state.data_root)?;
    let database = lock_database(&state)?;
    for proposal in proposals {
        if !database.is_codex_proposal_accepted(&proposal.request_id)? {
            database.record_codex_proposal(&proposal)?;
        }
    }
    let pending = database.list_pending_codex_proposals()?;
    let history = database.list_codex_proposal_history()?;
    drop(database);
    Ok(CodexProposalInbox {
        proposals: pending,
        history,
        rejected,
        inbox_path: state.data_root.codex_inbox_path().display().to_string(),
        category_catalog_path: codex_proposals::category_catalog_path(&state.data_root)
            .display()
            .to_string(),
    })
}

#[tauri::command]
pub fn accept_codex_proposal(
    input: AcceptCodexProposalInput,
    state: State<'_, AppState>,
) -> AppResult<AcceptCodexProposalResult> {
    let actor = require_user(&state)?;
    let proposal = {
        let database = lock_database(&state)?;
        match database.get_pending_codex_proposal(&input.request_id) {
            Ok(proposal) => proposal,
            Err(error) if error.code == "CDX-003" => {
                let proposal = codex_proposals::read_proposal(&state.data_root, &input.request_id)?;
                database.record_codex_proposal(&proposal)?;
                proposal
            }
            Err(error) => return Err(error),
        }
    };
    codex_proposals::validate_proposal(&proposal)?;
    codex_proposals::validate_delegated_sources(&state.data_root, &proposal)?;
    let content = rich_content::validate_and_extract_with_attachments(&proposal.faq.body_doc)?;
    if proposal.proposal_kind != CodexProposalKind::Revise && !content.attachments.is_empty() {
        return Err(AppError::new(
            "CDX-002",
            "Codex提案から画像は取り込めません。",
            "画像は下書きを取り込んだ後、FAQ編集画面から追加してください。",
        ));
    }

    if proposal.proposal_kind == CodexProposalKind::Revise {
        let source = proposal.source_articles.first().ok_or_else(|| {
            AppError::new(
                "CDX-002",
                "修正提案の元FAQが指定されていません。",
                "Codexへ委譲番号を指定して、もう一度修正を依頼してください。",
            )
        })?;
        let mut database = lock_database(&state)?;
        let current = database.get_article(&source.article_id)?;
        validate_revision_attachments(&content.attachments, &current)?;
        let mut article = database.accept_codex_revision_as(
            CodexProposalRevisionRecord {
                request_id: &proposal.request_id,
                source_article: source,
                title: proposal.faq.title.trim(),
                summary: proposal.faq.summary.trim(),
                body_doc: &proposal.faq.body_doc,
                body_plain_text: &content.plain_text,
                importance: proposal.faq.importance,
            },
            &actor.id,
        )?;
        drop(database);
        attachments::hydrate_article_paths(&state.data_root, &mut article)?;
        let _ = codex_proposals::discard_proposal(&state.data_root, &proposal.request_id);
        return Ok(AcceptCodexProposalResult {
            article,
            created_category: None,
        });
    }

    let article_id = Uuid::now_v7().to_string();
    let new_category_id = Uuid::now_v7().to_string();
    let (category_id, new_category) = if input.create_proposed_category {
        let category = proposal.new_category_proposal.as_ref().ok_or_else(|| {
            AppError::new(
                "CDX-004",
                "このCodex提案には新規分類案がありません。",
                "既存分類を選ぶか、Codexへ分類案を含めて再依頼してください。",
            )
        })?;
        (
            new_category_id.as_str(),
            Some(NewCategoryRecord {
                id: &new_category_id,
                name: category.name.trim(),
                description: category.description.trim(),
                parent_id: category.parent_category_id.as_deref(),
            }),
        )
    } else {
        let category_id = input.category_id.as_deref().ok_or_else(|| {
            AppError::new(
                "CDX-005",
                "下書きの所属分類を選択してください。",
                "既存分類を1つ選んでから取り込んでください。",
            )
        })?;
        (category_id, None)
    };

    let (mut article, created_category) = lock_database(&state)?.accept_codex_proposal_as(
        CodexProposalArticleRecord {
            request_id: &proposal.request_id,
            proposal_kind: proposal.proposal_kind,
            source_articles: &proposal.source_articles,
            article_id: &article_id,
            category_id,
            new_category,
            title: proposal.faq.title.trim(),
            summary: proposal.faq.summary.trim(),
            body_doc: &proposal.faq.body_doc,
            body_plain_text: &content.plain_text,
            importance: proposal.faq.importance,
        },
        &actor.id,
    )?;
    attachments::hydrate_article_paths(&state.data_root, &mut article)?;
    let _ = codex_proposals::discard_proposal(&state.data_root, &proposal.request_id);
    refresh_category_catalog_best_effort(&state);
    Ok(AcceptCodexProposalResult {
        article,
        created_category,
    })
}

#[tauri::command]
pub fn reject_codex_proposal(request_id: String, state: State<'_, AppState>) -> AppResult<()> {
    require_user(&state)?;
    lock_database(&state)?.reject_codex_proposal(&request_id)?;
    let _ = codex_proposals::discard_proposal(&state.data_root, &request_id);
    Ok(())
}

#[tauri::command]
pub fn reopen_rejected_codex_proposal(
    request_id: String,
    state: State<'_, AppState>,
) -> AppResult<()> {
    require_user(&state)?;
    lock_database(&state)?.reopen_rejected_codex_proposal(&request_id)
}

#[tauri::command]
pub fn create_codex_delegation(
    input: CreateCodexDelegationInput,
    state: State<'_, AppState>,
) -> AppResult<CodexDelegationResult> {
    require_user(&state)?;
    let expected = match input.kind {
        CodexDelegationKind::Revise => 1..=1,
        CodexDelegationKind::Merge => 2..=10,
    };
    if !expected.contains(&input.article_ids.len()) {
        return Err(AppError::new(
            "CDX-020",
            match input.kind {
                CodexDelegationKind::Revise => "推敲・修正するFAQを1件選択してください。",
                CodexDelegationKind::Merge => "統合するFAQを2～10件選択してください。",
            },
            "FAQ管理画面で対象を選び直してください。",
        ));
    }
    let mut unique = HashSet::new();
    if input
        .article_ids
        .iter()
        .any(|id| !unique.insert(id.as_str()))
    {
        return Err(AppError::new(
            "CDX-020",
            "同じFAQが複数回選択されています。",
            "重複を外してから、もう一度委譲してください。",
        ));
    }
    let database = lock_database(&state)?;
    let mut articles = Vec::with_capacity(input.article_ids.len());
    for id in &input.article_ids {
        let article = database.get_article(id)?;
        if article.deleted_at.is_some() {
            return Err(AppError::new(
                "CDX-020",
                "削除済みFAQはCodexへ委譲できません。",
                "FAQを復元するか、登録中のFAQを選び直してください。",
            ));
        }
        if article.merge_info.is_some() {
            return Err(AppError::new(
                "CDX-020",
                "統合済みFAQはCodexへ委譲できません。",
                "統合先のFAQを利用するか、FAQ管理画面で統合を解除してから選び直してください。",
            ));
        }
        articles.push(article);
    }
    let categories = database.list_categories()?;
    drop(database);
    codex_proposals::write_delegation(&state.data_root, input.kind, &articles, &categories)
}

fn validate_revision_attachments(
    proposed: &[rich_content::AttachmentReference],
    current: &Article,
) -> AppResult<()> {
    validate_revision_attachment_records(proposed, &current.attachments)
}

fn validate_revision_attachment_records(
    proposed: &[rich_content::AttachmentReference],
    current: &[crate::models::ArticleAttachment],
) -> AppResult<()> {
    let expected = current
        .iter()
        .map(|attachment| (attachment.id.as_str(), attachment.alt_text.as_str()))
        .collect::<HashSet<_>>();
    let actual = proposed
        .iter()
        .map(|attachment| (attachment.id.as_str(), attachment.alt_text.as_str()))
        .collect::<HashSet<_>>();
    if expected == actual {
        Ok(())
    } else {
        Err(AppError::new(
            "CDX-021",
            "Codex修正案で既存画像の参照が変更されています。",
            "画像の欠落を防ぐため、元FAQの画像ノードを変更せずに修正案を作り直してください。",
        ))
    }
}

fn refresh_category_catalog_best_effort(state: &State<'_, AppState>) {
    if let Ok(database) = lock_database(state)
        && let Ok(categories) = database.list_categories()
    {
        let _ = codex_proposals::write_category_catalog(&state.data_root, &categories);
    }
}

#[tauri::command]
pub fn get_article(id: String, state: State<'_, AppState>) -> AppResult<Article> {
    require_user(&state)?;
    let mut article = lock_database(&state)?.get_article(&id)?;
    attachments::hydrate_article_paths(&state.data_root, &mut article)?;
    Ok(article)
}

#[tauri::command]
pub fn get_codex_merge_publication_context(
    article_id: String,
    state: State<'_, AppState>,
) -> AppResult<Option<CodexMergePublicationContext>> {
    require_user(&state)?;
    lock_database(&state)?.get_codex_merge_publication_context(&article_id)
}

#[tauri::command]
pub fn mark_codex_merge_sources(
    article_id: String,
    state: State<'_, AppState>,
) -> AppResult<MarkCodexMergeSourcesResult> {
    require_user(&state)?;
    lock_database(&state)?.mark_codex_merge_sources(&article_id)
}

#[tauri::command]
pub fn clear_article_merge(article_id: String, state: State<'_, AppState>) -> AppResult<Article> {
    require_user(&state)?;
    let mut article = lock_database(&state)?.clear_article_merge(&article_id)?;
    attachments::hydrate_article_paths(&state.data_root, &mut article)?;
    Ok(article)
}

#[tauri::command]
pub fn search_articles(
    input: SearchArticlesInput,
    state: State<'_, AppState>,
) -> AppResult<SearchArticlePage> {
    require_user(&state)?;
    lock_database(&state)?.search_articles(&input)
}

#[tauri::command]
pub fn record_search_log(
    input: RecordSearchLogInput,
    state: State<'_, AppState>,
) -> AppResult<String> {
    require_user(&state)?;
    if input.query.chars().count() > 500 {
        return Err(AppError::new(
            "LOG-001",
            "検索文は500文字以内で入力してください。",
            "検索文を短くして、もう一度検索してください。",
        ));
    }
    lock_database(&state)?.record_search_log(
        &input.query,
        input.category_id.as_deref(),
        input.scope,
        input.result_count,
    )
}

#[tauri::command]
pub fn record_article_view(
    input: RecordArticleViewInput,
    state: State<'_, AppState>,
) -> AppResult<()> {
    require_user(&state)?;
    lock_database(&state)?
        .record_article_view(&input.article_id, input.source_search_log_id.as_deref())
}

#[tauri::command]
pub fn list_search_logs(
    input: ListSearchLogsInput,
    state: State<'_, AppState>,
) -> AppResult<SearchLogPage> {
    require_user(&state)?;
    if input.query.chars().count() > 500 {
        return Err(AppError::new(
            "LOG-001",
            "絞り込み文字列は500文字以内で入力してください。",
            "文字列を短くして、もう一度お試しください。",
        ));
    }
    lock_database(&state)?.list_search_logs(&input)
}

#[tauri::command]
pub fn list_view_logs(
    input: ListViewLogsInput,
    state: State<'_, AppState>,
) -> AppResult<ViewLogPage> {
    require_user(&state)?;
    if input.query.chars().count() > 500 {
        return Err(AppError::new(
            "LOG-001",
            "絞り込み文字列は500文字以内で入力してください。",
            "文字列を短くして、もう一度お試しください。",
        ));
    }
    lock_database(&state)?.list_view_logs(&input)
}

#[tauri::command]
pub fn delete_history(input: DeleteHistoryInput, state: State<'_, AppState>) -> AppResult<i64> {
    require_user(&state)?;
    lock_database(&state)?.delete_history(
        input.target,
        input.start_date.as_deref(),
        input.end_date.as_deref(),
        input.delete_all,
    )
}

#[tauri::command]
pub fn list_synonym_groups(state: State<'_, AppState>) -> AppResult<Vec<SynonymGroup>> {
    require_user(&state)?;
    lock_database(&state)?.list_synonym_groups()
}

#[tauri::command]
pub fn save_synonym_group(
    input: SaveSynonymGroupInput,
    state: State<'_, AppState>,
) -> AppResult<SynonymGroup> {
    require_user(&state)?;
    lock_database(&state)?.save_synonym_group(
        input.id.as_deref(),
        &input.display_name,
        &input.terms,
        input.allow_conflicts,
    )
}

#[tauri::command]
pub fn delete_synonym_group(id: String, state: State<'_, AppState>) -> AppResult<()> {
    require_user(&state)?;
    lock_database(&state)?.delete_synonym_group(&id)
}

#[tauri::command]
pub fn search_related_articles(
    input: SearchRelatedArticlesInput,
    state: State<'_, AppState>,
) -> AppResult<Vec<RelatedArticleCandidate>> {
    require_user(&state)?;
    if input.query.chars().count() > 200 {
        return Err(AppError::new(
            "ART-009",
            "関連FAQの検索文は200文字以内で入力してください。",
            "検索文を短くして、もう一度お試しください。",
        ));
    }
    lock_database(&state)?
        .search_related_article_candidates(input.article_id.as_deref(), &input.query)
}

#[tauri::command]
pub fn save_article(input: SaveArticleInput, state: State<'_, AppState>) -> AppResult<Article> {
    let actor = require_user(&state)?;
    validate_article_fields(&input)?;
    validate_article_details(&input)?;
    let rich_content = rich_content::validate_and_extract_with_attachments(&input.body_doc)?;
    if input.status == "published"
        && rich_content.plain_text.trim().is_empty()
        && rich_content.attachments.is_empty()
    {
        return Err(AppError::new(
            "ART-001",
            "公開するFAQには回答が必要です。",
            "回答を入力するか、下書きとして保存してください。",
        ));
    }

    let is_new = input.id.is_none();
    let article_id = input
        .id
        .clone()
        .unwrap_or_else(|| Uuid::now_v7().to_string());
    let mut database = lock_database(&state)?;
    if !is_new
        && input.status == "published"
        && database.requires_new_badge_for_merge_publication(&article_id)?
    {
        let valid_new_badge = input
            .new_badge_until
            .as_deref()
            .and_then(|date| NaiveDate::parse_from_str(date, "%Y-%m-%d").ok())
            .is_some_and(|date| date >= Local::now().date_naive());
        if !valid_new_badge || input.is_hidden {
            return Err(AppError::new(
                "ART-008",
                "統合FAQを公開する場合は、本日以降の新着表示終了日と表示設定が必要です。",
                "「新着」を表示する設定をオンにして本日以降の日付を選び、非表示をオフにしてください。",
            ));
        }
    }
    let existing = if is_new {
        Vec::new()
    } else {
        database.get_article(&article_id)?.attachments
    };
    let prepared = attachments::prepare(
        &state.data_root,
        &article_id,
        &rich_content.attachments,
        &existing,
    )?;
    let details = ArticleDetailsRecord {
        symptoms: &input.symptoms,
        causes: &input.causes,
        targets: &input.targets,
        error_codes: &input.error_codes,
        procedures: &input.procedures,
        cautions: &input.cautions,
        tags: &input.tags,
        search_terms: &input.search_terms,
        related_article_ids: &input.related_article_ids,
    };
    let saved = database.save_article_with_details_as(
        ArticleRecord {
            id: &article_id,
            is_new,
            category_id: &input.category_id,
            title: input.title.trim(),
            summary: input.summary.trim(),
            body_doc: &input.body_doc,
            body_plain_text: &rich_content.plain_text,
            status: &input.status,
            importance: input.importance,
            new_badge_until: input.new_badge_until.as_deref(),
            updated_badge_until: input.updated_badge_until.as_deref(),
            is_hidden: input.is_hidden,
            attachments: &prepared.records,
        },
        &details,
        &actor.id,
    );
    let mut article = match saved {
        Ok(article) => article,
        Err(error) => {
            attachments::rollback(&prepared);
            return Err(error);
        }
    };
    attachments::commit(&state.data_root, &prepared);
    attachments::hydrate_article_paths(&state.data_root, &mut article)?;
    Ok(article)
}

#[tauri::command]
pub fn duplicate_article(id: String, state: State<'_, AppState>) -> AppResult<Article> {
    let actor = require_user(&state)?;
    let mut database = lock_database(&state)?;
    let source = database.get_article(&id)?;
    if source.deleted_at.is_some() {
        return Err(AppError::new(
            "ART-006",
            "削除済みFAQは複製できません。",
            "FAQを復元してから複製してください。",
        ));
    }

    let source_content = rich_content::validate_and_extract_with_attachments(&source.body_doc)?;
    let source_attachments = source
        .attachments
        .iter()
        .map(|attachment| (attachment.id.as_str(), attachment))
        .collect::<HashMap<_, _>>();
    let mut replacements = HashMap::new();
    let mut staged_ids = Vec::new();
    for reference in &source_content.attachments {
        let Some(source_attachment) = source_attachments.get(reference.id.as_str()) else {
            cleanup_duplicate_stages(&state, &staged_ids);
            return Err(AppError::new(
                "ATT-005",
                "複製元FAQの画像が見つかりません。",
                "複製元FAQを開いて画像を確認し、必要に応じて追加し直してください。",
            ));
        };
        let staged =
            match attachments::stage_copy_of_attachment(&state.data_root, source_attachment) {
                Ok(staged) => staged,
                Err(error) => {
                    cleanup_duplicate_stages(&state, &staged_ids);
                    return Err(error);
                }
            };
        replacements.insert(reference.id.clone(), staged.id.clone());
        staged_ids.push(staged.id);
    }

    let body_doc = match rich_content::remap_attachment_ids(&source.body_doc, &replacements) {
        Ok(body_doc) => body_doc,
        Err(error) => {
            cleanup_duplicate_stages(&state, &staged_ids);
            return Err(error);
        }
    };
    let copied_content = match rich_content::validate_and_extract_with_attachments(&body_doc) {
        Ok(content) => content,
        Err(error) => {
            cleanup_duplicate_stages(&state, &staged_ids);
            return Err(error);
        }
    };
    let article_id = Uuid::now_v7().to_string();
    let prepared = match attachments::prepare(
        &state.data_root,
        &article_id,
        &copied_content.attachments,
        &[],
    ) {
        Ok(prepared) => prepared,
        Err(error) => {
            cleanup_duplicate_stages(&state, &staged_ids);
            return Err(error);
        }
    };
    let title = duplicate_title(&source.title);
    let related_article_ids = source
        .related_articles
        .iter()
        .map(|related| related.id.clone())
        .collect::<Vec<_>>();
    let details = ArticleDetailsRecord {
        symptoms: &source.symptoms,
        causes: &source.causes,
        targets: &source.targets,
        error_codes: &source.error_codes,
        procedures: &source.procedures,
        cautions: &source.cautions,
        tags: &source.tags,
        search_terms: &source.search_terms,
        related_article_ids: &related_article_ids,
    };
    let saved = database.save_article_with_details_as(
        ArticleRecord {
            id: &article_id,
            is_new: true,
            category_id: &source.category_id,
            title: &title,
            summary: &source.summary,
            body_doc: &body_doc,
            body_plain_text: &copied_content.plain_text,
            status: "draft",
            importance: source.importance,
            new_badge_until: None,
            updated_badge_until: None,
            is_hidden: source.is_hidden,
            attachments: &prepared.records,
        },
        &details,
        &actor.id,
    );
    let mut article = match saved {
        Ok(article) => article,
        Err(error) => {
            attachments::rollback(&prepared);
            cleanup_duplicate_stages(&state, &staged_ids);
            return Err(error);
        }
    };
    attachments::commit(&state.data_root, &prepared);
    attachments::hydrate_article_paths(&state.data_root, &mut article)?;
    Ok(article)
}

fn cleanup_duplicate_stages(state: &State<'_, AppState>, ids: &[String]) {
    for id in ids {
        attachments::discard_stage(&state.data_root, id);
    }
}

fn duplicate_title(source: &str) -> String {
    const SUFFIX: &str = "（コピー）";
    let maximum_source = 200 - SUFFIX.chars().count();
    format!(
        "{}{}",
        source.chars().take(maximum_source).collect::<String>(),
        SUFFIX
    )
}

#[tauri::command]
pub fn stage_article_image(
    path: String,
    state: State<'_, AppState>,
) -> AppResult<StagedArticleImage> {
    require_user(&state)?;
    attachments::stage_from_path(&state.data_root, &PathBuf::from(path))
}

#[tauri::command]
pub fn stage_article_image_bytes(
    input: StageArticleImageBytesInput,
    state: State<'_, AppState>,
) -> AppResult<StagedArticleImage> {
    require_user(&state)?;
    attachments::stage_bytes(&state.data_root, &input.original_name, &input.bytes)
}

#[tauri::command]
pub fn discard_staged_article_image(id: String, state: State<'_, AppState>) -> AppResult<()> {
    require_user(&state)?;
    attachments::discard_stage(&state.data_root, &id);
    Ok(())
}

#[tauri::command]
pub fn open_external_url(
    url: String,
    app: tauri::AppHandle,
    state: State<'_, AppState>,
) -> AppResult<()> {
    require_user(&state)?;
    validate_external_url(&url)?;
    app.opener().open_url(url, None::<&str>).map_err(|_| {
        AppError::new(
            "URL-002",
            "参考URLを既定ブラウザーで開けませんでした。",
            "Windowsの既定ブラウザー設定を確認して、もう一度お試しください。",
        )
    })
}

fn validate_external_url(url: &str) -> AppResult<()> {
    if url.chars().count() > 2048 {
        return Err(invalid_external_url());
    }
    let parsed = Url::parse(url).map_err(|_| invalid_external_url())?;
    if !matches!(parsed.scheme(), "http" | "https")
        || parsed.host_str().is_none()
        || !parsed.username().is_empty()
        || parsed.password().is_some()
    {
        return Err(invalid_external_url());
    }
    Ok(())
}

fn invalid_external_url() -> AppError {
    AppError::new(
        "URL-001",
        "この参考URLは安全に開けません。",
        "http:// または https:// で始まるURLに修正してください。",
    )
}

#[tauri::command]
pub fn list_articles_for_management(
    input: ManagementArticlesInput,
    state: State<'_, AppState>,
) -> AppResult<ManagementArticlePage> {
    require_user(&state)?;
    if input.page < 1 {
        return Err(AppError::new(
            "ART-001",
            "管理一覧のページ指定が正しくありません。",
            "一覧を開き直してください。",
        ));
    }
    if let Some(status) = input.status.as_deref() {
        if !matches!(status, "draft" | "published" | "archived") {
            return Err(AppError::new(
                "ART-001",
                "FAQ状態の絞り込み指定が正しくありません。",
                "状態を選び直してください。",
            ));
        }
    }
    lock_database(&state)?.list_articles_for_management(&input)
}

#[tauri::command]
pub fn export_faq_csv(
    input: ExportFaqCsvInput,
    state: State<'_, AppState>,
) -> AppResult<CsvExportResult> {
    require_user(&state)?;
    let path = PathBuf::from(input.destination_path);
    validate_csv_path(&path, false)?;
    lock_database(&state)?.export_faq_csv(&path)
}

#[tauri::command]
pub fn inspect_faq_csv(path: String, state: State<'_, AppState>) -> AppResult<CsvImportPreview> {
    require_user(&state)?;
    let path = PathBuf::from(path);
    validate_csv_path(&path, true)?;
    lock_database(&state)?.inspect_faq_csv(&path)
}

#[tauri::command]
pub fn import_faq_csv(
    input: ImportFaqCsvInput,
    state: State<'_, AppState>,
) -> AppResult<CsvImportResult> {
    let actor = require_user(&state)?;
    let path = PathBuf::from(input.source_path);
    validate_csv_path(&path, true)?;
    let mut database = lock_database(&state)?;
    let preview = database.inspect_faq_csv(&path)?;
    if preview.file_sha256 != input.expected_file_sha256 {
        return Err(AppError::new(
            "CSV-007",
            "確認後にCSVファイルが変更されています。",
            "CSVをもう一度プレビューしてから取り込んでください。",
        ));
    }
    if preview.error_count > 0 {
        return Err(AppError::new(
            "CSV-004",
            "エラーがあるためCSVを取り込めません。",
            "プレビューに表示された行を修正し、もう一度選択してください。",
        ));
    }
    let safety_path = backup::create_csv_import_safety_backup(&state.data_root, &database)?;
    database.import_faq_csv(
        &path,
        &input.expected_file_sha256,
        &actor.id,
        safety_path.display().to_string(),
    )
}

#[tauri::command]
pub fn export_json(
    input: ExportJsonInput,
    state: State<'_, AppState>,
) -> AppResult<JsonExportResult> {
    require_user(&state)?;
    let path = PathBuf::from(input.destination_path);
    validate_json_path(&path, false)?;
    lock_database(&state)?.export_json(&path)
}

#[tauri::command]
pub fn inspect_json(path: String, state: State<'_, AppState>) -> AppResult<JsonImportPreview> {
    require_user(&state)?;
    let path = PathBuf::from(path);
    validate_json_path(&path, true)?;
    lock_database(&state)?.inspect_json(&path)
}

#[tauri::command]
pub fn import_json(
    input: ImportJsonInput,
    state: State<'_, AppState>,
) -> AppResult<JsonImportResult> {
    let actor = require_user(&state)?;
    let path = PathBuf::from(input.source_path);
    validate_json_path(&path, true)?;
    let mut database = lock_database(&state)?;
    let preview = database.inspect_json(&path)?;
    if preview.file_sha256 != input.expected_file_sha256 {
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
    let safety_path = backup::create_json_import_safety_backup(&state.data_root, &database)?;
    let result = database.import_json(
        &path,
        &input.expected_file_sha256,
        &actor.id,
        safety_path.display().to_string(),
    )?;
    let categories = database.list_categories()?;
    drop(database);
    codex_proposals::write_category_catalog(&state.data_root, &categories)?;
    Ok(result)
}

#[tauri::command]
pub fn delete_article(id: String, state: State<'_, AppState>) -> AppResult<Article> {
    let actor = require_user(&state)?;
    let mut article = lock_database(&state)?.delete_article_as(&id, &actor.id)?;
    attachments::hydrate_article_paths(&state.data_root, &mut article)?;
    Ok(article)
}

#[tauri::command]
pub fn restore_article(id: String, state: State<'_, AppState>) -> AppResult<Article> {
    let actor = require_user(&state)?;
    let mut article = lock_database(&state)?.restore_article_as(&id, &actor.id)?;
    attachments::hydrate_article_paths(&state.data_root, &mut article)?;
    Ok(article)
}

#[tauri::command]
pub fn create_full_backup(
    input: CreateFullBackupInput,
    state: State<'_, AppState>,
) -> AppResult<BackupResult> {
    require_admin(&state)?;
    let database = lock_database(&state)?;
    backup::create_full_backup(
        &state.data_root,
        &database,
        &PathBuf::from(input.destination_path),
        &input.display_name,
        input.overwrite,
    )
}

#[tauri::command]
pub fn get_backup_overview(state: State<'_, AppState>) -> AppResult<BackupOverview> {
    require_admin(&state)?;
    let database = lock_database(&state)?;
    backup::backup_overview(&state.data_root, &database)
}

#[tauri::command]
pub fn inspect_backup(path: String, state: State<'_, AppState>) -> AppResult<BackupPreview> {
    require_admin(&state)?;
    backup::inspect_backup(&state.data_root, &PathBuf::from(path))
}

#[tauri::command]
pub fn restore_backup(path: String, state: State<'_, AppState>) -> AppResult<RestoreResult> {
    require_admin(&state)?;
    let mut database = lock_database(&state)?;
    let result = backup::restore_backup(&state.data_root, &mut database, &PathBuf::from(path))?;
    *lock_session(&state)? = None;
    let categories = database.list_categories()?;
    drop(database);
    codex_proposals::write_category_catalog(&state.data_root, &categories)?;
    Ok(result)
}

fn validate_article_fields(input: &SaveArticleInput) -> AppResult<()> {
    if input.title.trim().is_empty() || input.title.chars().count() > 200 {
        return Err(AppError::new(
            "ART-001",
            "タイトルは1～200文字で入力してください。",
            "タイトルを確認して、もう一度保存してください。",
        ));
    }
    if input.category_id.trim().is_empty() {
        return Err(AppError::new(
            "ART-001",
            "所属分類を選択してください。",
            "分類を選択して、もう一度保存してください。",
        ));
    }
    if !matches!(input.status.as_str(), "draft" | "published" | "archived") {
        return Err(AppError::new(
            "ART-001",
            "FAQの状態が正しくありません。",
            "下書き、公開、廃止のいずれかを選択してください。",
        ));
    }
    if input.status == "published" && input.summary.trim().is_empty() {
        return Err(AppError::new(
            "ART-001",
            "公開するFAQには概要が必要です。",
            "概要を入力するか、下書きとして保存してください。",
        ));
    }
    if input.summary.chars().count() > 500 {
        return Err(AppError::new(
            "ART-001",
            "概要は500文字以内で入力してください。",
            "概要を短くして、もう一度保存してください。",
        ));
    }
    if !(1..=3).contains(&input.importance) {
        return Err(AppError::new(
            "ART-001",
            "重要度は1～3から選択してください。",
            "重要度を選び直してください。",
        ));
    }
    validate_badge_date(input.new_badge_until.as_deref(), "新着")?;
    validate_badge_date(input.updated_badge_until.as_deref(), "更新")?;
    Ok(())
}

fn validate_article_details(input: &SaveArticleInput) -> AppResult<()> {
    for (label, values, maximum_length) in [
        ("症状", input.symptoms.as_slice(), 200),
        ("想定原因", input.causes.as_slice(), 200),
        ("対象", input.targets.as_slice(), 200),
        ("エラーコード", input.error_codes.as_slice(), 100),
        ("対応手順", input.procedures.as_slice(), 500),
        ("注意事項", input.cautions.as_slice(), 500),
        ("タグ", input.tags.as_slice(), 100),
        ("検索用語", input.search_terms.as_slice(), 200),
    ] {
        if values.len() > 50 {
            return Err(AppError::new(
                "ART-009",
                format!("{label}は50件以内で登録してください。"),
                "不要な項目を削除して、もう一度保存してください。",
            ));
        }
        let mut normalized_values = HashSet::new();
        for value in values {
            let value = value.trim();
            if value.is_empty()
                || value.chars().count() > maximum_length
                || value.contains('\r')
                || value.contains('\n')
            {
                return Err(AppError::new(
                    "ART-009",
                    format!("{label}は1～{maximum_length}文字の1行テキストで入力してください。"),
                    "入力内容を短くし、空の項目や改行を削除してください。",
                ));
            }
            if !normalized_values.insert(normalize(value)) {
                return Err(AppError::new(
                    "ART-009",
                    format!("{label}に同じ内容が重複しています。"),
                    "重複している項目を1件にまとめてください。",
                ));
            }
        }
    }
    if input.related_article_ids.len() > 50 {
        return Err(AppError::new(
            "ART-009",
            "関連FAQは50件以内で選択してください。",
            "不要な関連FAQを外して、もう一度保存してください。",
        ));
    }
    let mut related_ids = HashSet::new();
    for related_id in &input.related_article_ids {
        if related_id.trim().is_empty()
            || input.id.as_deref() == Some(related_id.as_str())
            || !related_ids.insert(related_id)
        {
            return Err(AppError::new(
                "ART-009",
                "関連FAQに自己参照または重複があります。",
                "同じFAQを1回だけ選択し、編集中のFAQ自身は選択しないでください。",
            ));
        }
    }
    Ok(())
}

fn validate_csv_path(path: &std::path::Path, must_exist: bool) -> AppResult<()> {
    if !path.is_absolute()
        || !path
            .to_string_lossy()
            .to_ascii_lowercase()
            .ends_with(".knowledge-faq.csv")
        || (must_exist && !path.is_file())
    {
        return Err(AppError::new(
            "CSV-001",
            "CSVファイルの場所またはファイル名が正しくありません。",
            "絶対パスにある「.knowledge-faq.csv」で終わるファイルを指定してください。",
        ));
    }
    let parent = path.parent().ok_or_else(|| {
        AppError::new(
            "CSV-001",
            "CSVファイルの保存先を確認できません。",
            "別のフォルダを選択してください。",
        )
    })?;
    if !must_exist && !parent.is_dir() {
        return Err(AppError::new(
            "CSV-001",
            "CSVの保存先フォルダが見つかりません。",
            "既存のフォルダを選択してください。",
        ));
    }
    if crate::services::data_root::find_git_root(parent).is_some() {
        return Err(AppError::new(
            "CSV-006",
            "Git管理フォルダ内のCSVは使用できません。",
            "FAQデータの誤登録を防ぐため、デスクトップやドキュメントなどGit管理外を選択してください。",
        ));
    }
    Ok(())
}

fn validate_json_path(path: &std::path::Path, must_exist: bool) -> AppResult<()> {
    if !path.is_absolute()
        || !path
            .to_string_lossy()
            .to_ascii_lowercase()
            .ends_with(".knowledge-export.json")
        || (must_exist && !path.is_file())
    {
        return Err(AppError::new(
            "JSON-001",
            "JSONファイルの場所またはファイル名が正しくありません。",
            "絶対パスにある「.knowledge-export.json」で終わるファイルを指定してください。",
        ));
    }
    let parent = path.parent().ok_or_else(|| {
        AppError::new(
            "JSON-001",
            "JSONファイルの保存先を確認できません。",
            "別のフォルダを選択してください。",
        )
    })?;
    if !must_exist && !parent.is_dir() {
        return Err(AppError::new(
            "JSON-001",
            "JSONの保存先フォルダが見つかりません。",
            "既存のフォルダを選択してください。",
        ));
    }
    if crate::services::data_root::find_git_root(parent).is_some() {
        return Err(AppError::new(
            "JSON-006",
            "Git管理フォルダ内のJSONは使用できません。",
            "FAQデータの誤登録を防ぐため、デスクトップやドキュメントなどGit管理外を選択してください。",
        ));
    }
    Ok(())
}

fn validate_badge_date(value: Option<&str>, label: &str) -> AppResult<()> {
    let Some(value) = value else {
        return Ok(());
    };
    if NaiveDate::parse_from_str(value, "%Y-%m-%d").is_err() {
        return Err(AppError::new(
            "ART-001",
            format!("{label}フラグの表示終了日が正しくありません。"),
            "カレンダーから表示終了日を選び直してください。",
        ));
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn authenticated_user(id: &str, role: UserRole) -> AuthenticatedUser {
        AuthenticatedUser {
            id: id.into(),
            login_id: format!("login-{id}"),
            display_name: format!("利用者{id}"),
            role,
        }
    }

    #[test]
    fn protected_command_guards_require_login_and_admin_role() {
        assert_eq!(require_user_value(None).unwrap_err().code, "AUTH-002");
        assert_eq!(require_admin_value(None).unwrap_err().code, "AUTH-002");
        assert_eq!(
            require_admin_value(Some(authenticated_user("general", UserRole::User)))
                .unwrap_err()
                .code,
            "AUTH-003"
        );
        assert!(require_admin_value(Some(authenticated_user("admin", UserRole::Admin))).is_ok());
    }

    #[test]
    fn current_user_cannot_disable_their_own_account() {
        let current = authenticated_user("admin", UserRole::Admin);
        let input = SetUserActiveInput {
            id: current.id.clone(),
            is_active: false,
        };
        assert_eq!(
            validate_user_activation(&current, &input).unwrap_err().code,
            "USR-002"
        );
        assert!(
            validate_user_activation(
                &current,
                &SetUserActiveInput {
                    id: "another-admin".into(),
                    is_active: false,
                },
            )
            .is_ok()
        );
    }

    #[test]
    fn codex_revision_must_preserve_every_image_id_and_alt_text() {
        let id = Uuid::now_v7().to_string();
        let current = vec![crate::models::ArticleAttachment {
            id: id.clone(),
            original_name: "screen.png".into(),
            media_type: "image/png".into(),
            byte_size: 123,
            sha256: "hash".into(),
            alt_text: "設定画面".into(),
            asset_path: "managed/screen.png".into(),
            created_at: "2026-08-16T00:00:00Z".into(),
        }];
        assert!(
            validate_revision_attachment_records(
                &[rich_content::AttachmentReference {
                    id: id.clone(),
                    alt_text: "設定画面".into(),
                }],
                &current,
            )
            .is_ok()
        );
        assert_eq!(
            validate_revision_attachment_records(
                &[rich_content::AttachmentReference {
                    id,
                    alt_text: "変更された説明".into(),
                }],
                &current,
            )
            .unwrap_err()
            .code,
            "CDX-021"
        );
        assert_eq!(
            validate_revision_attachment_records(&[], &current)
                .unwrap_err()
                .code,
            "CDX-021"
        );
    }

    #[test]
    fn published_article_requires_summary() {
        let input = SaveArticleInput {
            id: None,
            category_id: "category".into(),
            title: "質問".into(),
            summary: "".into(),
            body_doc: json!({"type": "doc"}),
            status: "published".into(),
            importance: 1,
            new_badge_until: None,
            updated_badge_until: None,
            is_hidden: false,
            symptoms: Vec::new(),
            causes: Vec::new(),
            targets: Vec::new(),
            error_codes: Vec::new(),
            procedures: Vec::new(),
            cautions: Vec::new(),
            tags: Vec::new(),
            search_terms: Vec::new(),
            related_article_ids: Vec::new(),
        };
        assert_eq!(validate_article_fields(&input).unwrap_err().code, "ART-001");
    }

    #[test]
    fn badge_dates_use_iso_calendar_dates() {
        assert!(validate_badge_date(Some("2026-08-10"), "新着").is_ok());
        assert_eq!(
            validate_badge_date(Some("2026-02-30"), "更新")
                .unwrap_err()
                .code,
            "ART-001"
        );
    }

    #[test]
    fn external_url_allows_only_http_and_https_with_a_host() {
        assert!(validate_external_url("https://example.com/guide").is_ok());
        assert!(validate_external_url("http://localhost:7100/").is_ok());
        for invalid in [
            "javascript:alert(1)",
            "file:///C:/secret.txt",
            "data:text/plain,test",
            "https://",
            "https://user:password@example.com/",
        ] {
            assert_eq!(validate_external_url(invalid).unwrap_err().code, "URL-001");
        }
    }

    #[test]
    fn duplicate_title_stays_within_article_limit() {
        let title = duplicate_title(&"あ".repeat(200));
        assert_eq!(title.chars().count(), 200);
        assert!(title.ends_with("（コピー）"));
    }

    #[test]
    fn json_transfer_rejects_git_managed_and_wrong_extension_paths() {
        let directory = tempfile::tempdir().unwrap();
        let repository = directory.path().join("repository");
        std::fs::create_dir_all(repository.join(".git")).unwrap();
        let unsafe_path = repository.join("data.knowledge-export.json");
        assert_eq!(
            validate_json_path(&unsafe_path, false).unwrap_err().code,
            "JSON-006"
        );
        let wrong_extension = directory.path().join("data.json");
        assert_eq!(
            validate_json_path(&wrong_extension, false)
                .unwrap_err()
                .code,
            "JSON-001"
        );
    }
}
