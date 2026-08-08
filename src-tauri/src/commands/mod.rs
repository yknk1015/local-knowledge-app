use std::{collections::HashMap, path::PathBuf};

use tauri::State;
use tauri_plugin_opener::OpenerExt;
use url::Url;

use crate::{
    AppState,
    errors::{AppError, AppResult},
    models::{
        Article, ArticleListItem, BackupOverview, BackupPreview, BackupResult, Category,
        CreateCategoryInput, CreateFullBackupInput, ManagementArticlePage, ManagementArticlesInput,
        RestoreResult, SaveArticleInput, SearchArticlesInput, StageArticleImageBytesInput,
        StagedArticleImage, SystemInfo, UpdateCategoryInput,
    },
    repositories::database::ArticleRecord,
    services::{attachments, backup, rich_content},
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
    lock_database(&state)?.create_category(&input.name, input.parent_id.as_deref())
}

#[tauri::command]
pub fn update_category(
    input: UpdateCategoryInput,
    state: State<'_, AppState>,
) -> AppResult<Category> {
    lock_database(&state)?.update_category(&input.id, &input.name, input.parent_id.as_deref())
}

#[tauri::command]
pub fn delete_category(id: String, state: State<'_, AppState>) -> AppResult<()> {
    lock_database(&state)?.delete_category(&id)
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
    backup::restore_backup(&state.data_root, &mut database, &PathBuf::from(path))
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
        };
        assert_eq!(validate_article_fields(&input).unwrap_err().code, "ART-001");
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
