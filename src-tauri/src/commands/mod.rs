use std::{
    collections::{HashMap, HashSet},
    path::PathBuf,
};

use chrono::NaiveDate;
use tauri::State;
use tauri_plugin_opener::OpenerExt;
use url::Url;

use crate::{
    AppState,
    errors::{AppError, AppResult},
    models::{
        AcceptCodexProposalInput, AcceptCodexProposalResult, Article, ArticleListItem,
        BackupOverview, BackupPreview, BackupResult, Category, CodexDelegationKind,
        CodexDelegationResult, CodexProposalInbox, CodexProposalKind, CreateCategoryInput,
        CreateCodexDelegationInput, CreateFullBackupInput, ManagementArticlePage,
        ManagementArticlesInput, RestoreResult, SaveArticleInput, SearchArticlesInput,
        StageArticleImageBytesInput, StagedArticleImage, SystemInfo, UpdateCategoryInput,
    },
    repositories::database::{
        ArticleRecord, CodexProposalArticleRecord, CodexProposalRevisionRecord, NewCategoryRecord,
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

#[tauri::command]
pub fn get_system_info(state: State<'_, AppState>) -> SystemInfo {
    SystemInfo {
        app_version: env!("CARGO_PKG_VERSION").to_owned(),
        data_root: state.data_root.root().display().to_string(),
        database_path: state.data_root.database_path().display().to_string(),
    }
}

#[tauri::command]
pub fn list_categories(state: State<'_, AppState>) -> AppResult<Vec<Category>> {
    lock_database(&state)?.list_categories()
}

#[tauri::command]
pub fn create_category(
    input: CreateCategoryInput,
    state: State<'_, AppState>,
) -> AppResult<Category> {
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
pub fn delete_category(id: String, state: State<'_, AppState>) -> AppResult<()> {
    lock_database(&state)?.delete_category(&id)?;
    refresh_category_catalog_best_effort(&state);
    Ok(())
}

#[tauri::command]
pub fn list_codex_proposals(state: State<'_, AppState>) -> AppResult<CodexProposalInbox> {
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
        let mut article = database.accept_codex_revision(CodexProposalRevisionRecord {
            request_id: &proposal.request_id,
            source_article: source,
            title: proposal.faq.title.trim(),
            summary: proposal.faq.summary.trim(),
            body_doc: &proposal.faq.body_doc,
            body_plain_text: &content.plain_text,
            importance: proposal.faq.importance,
        })?;
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

    let (mut article, created_category) =
        lock_database(&state)?.accept_codex_proposal(CodexProposalArticleRecord {
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
        })?;
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
    lock_database(&state)?.reject_codex_proposal(&request_id)?;
    let _ = codex_proposals::discard_proposal(&state.data_root, &request_id);
    Ok(())
}

#[tauri::command]
pub fn reopen_rejected_codex_proposal(
    request_id: String,
    state: State<'_, AppState>,
) -> AppResult<()> {
    lock_database(&state)?.reopen_rejected_codex_proposal(&request_id)
}

#[tauri::command]
pub fn create_codex_delegation(
    input: CreateCodexDelegationInput,
    state: State<'_, AppState>,
) -> AppResult<CodexDelegationResult> {
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
    let expected = current
        .attachments
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
    let mut article = lock_database(&state)?.get_article(&id)?;
    attachments::hydrate_article_paths(&state.data_root, &mut article)?;
    Ok(article)
}

#[tauri::command]
pub fn search_articles(
    input: SearchArticlesInput,
    state: State<'_, AppState>,
) -> AppResult<Vec<ArticleListItem>> {
    lock_database(&state)?.search_articles(&input)
}

#[tauri::command]
pub fn save_article(input: SaveArticleInput, state: State<'_, AppState>) -> AppResult<Article> {
    validate_article_fields(&input)?;
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
    let saved = database.save_article(ArticleRecord {
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
    });
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
    let saved = database.save_article(ArticleRecord {
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
    });
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
    attachments::stage_from_path(&state.data_root, &PathBuf::from(path))
}

#[tauri::command]
pub fn stage_article_image_bytes(
    input: StageArticleImageBytesInput,
    state: State<'_, AppState>,
) -> AppResult<StagedArticleImage> {
    attachments::stage_bytes(&state.data_root, &input.original_name, &input.bytes)
}

#[tauri::command]
pub fn discard_staged_article_image(id: String, state: State<'_, AppState>) {
    attachments::discard_stage(&state.data_root, &id);
}

#[tauri::command]
pub fn open_external_url(url: String, app: tauri::AppHandle) -> AppResult<()> {
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
pub fn delete_article(id: String, state: State<'_, AppState>) -> AppResult<Article> {
    let mut article = lock_database(&state)?.delete_article(&id)?;
    attachments::hydrate_article_paths(&state.data_root, &mut article)?;
    Ok(article)
}

#[tauri::command]
pub fn restore_article(id: String, state: State<'_, AppState>) -> AppResult<Article> {
    let mut article = lock_database(&state)?.restore_article(&id)?;
    attachments::hydrate_article_paths(&state.data_root, &mut article)?;
    Ok(article)
}

#[tauri::command]
pub fn create_full_backup(
    input: CreateFullBackupInput,
    state: State<'_, AppState>,
) -> AppResult<BackupResult> {
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
    let database = lock_database(&state)?;
    backup::backup_overview(&state.data_root, &database)
}

#[tauri::command]
pub fn inspect_backup(path: String, state: State<'_, AppState>) -> AppResult<BackupPreview> {
    backup::inspect_backup(&state.data_root, &PathBuf::from(path))
}

#[tauri::command]
pub fn restore_backup(path: String, state: State<'_, AppState>) -> AppResult<RestoreResult> {
    let mut database = lock_database(&state)?;
    let result = backup::restore_backup(&state.data_root, &mut database, &PathBuf::from(path))?;
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
}
