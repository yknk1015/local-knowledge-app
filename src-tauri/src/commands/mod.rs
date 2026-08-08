use std::path::PathBuf;

use tauri::State;

use crate::{
    AppState,
    errors::{AppError, AppResult},
    models::{
        Article, ArticleListItem, BackupOverview, BackupPreview, BackupResult, Category,
        CreateCategoryInput, CreateFullBackupInput, RestoreResult, SaveArticleInput,
        SearchArticlesInput, SystemInfo,
    },
    repositories::database::ArticleRecord,
    services::{backup, rich_content},
};

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
pub fn get_article(id: String, state: State<'_, AppState>) -> AppResult<Article> {
    lock_database(&state)?.get_article(&id)
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
    let body_plain_text = rich_content::validate_and_extract(&input.body_doc)?;
    if input.status == "published" && body_plain_text.trim().is_empty() {
        return Err(AppError::new(
            "ART-001",
            "公開するFAQには回答が必要です。",
            "回答を入力するか、下書きとして保存してください。",
        ));
    }

    lock_database(&state)?.save_article(ArticleRecord {
        id: input.id.as_deref(),
        category_id: &input.category_id,
        title: input.title.trim(),
        summary: input.summary.trim(),
        body_doc: &input.body_doc,
        body_plain_text: &body_plain_text,
        status: &input.status,
        importance: input.importance,
    })
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
}
