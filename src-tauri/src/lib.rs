mod commands;
mod errors;
mod models;
mod repositories;
mod services;

use std::{path::PathBuf, sync::Mutex};

use repositories::database::Database;
use services::data_root::{DataRootService, find_git_root};
use tauri::{Manager, Runtime};

pub struct AppState {
    data_root: DataRootService,
    database: Mutex<Database>,
}

pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_opener::init())
        .setup(|app| {
            let app_data_dir = app.path().app_local_data_dir().map_err(|_| {
                errors::AppError::system("利用者データの保存先を取得できませんでした。")
            })?;
            let forbidden_roots = forbidden_roots(app);
            let data_root = DataRootService::initialize(app_data_dir, &forbidden_roots)?;
            services::attachments::cleanup_stale_stages(&data_root);
            let database = Database::open(&data_root.database_path())?;
            let categories = database.list_categories()?;
            services::codex_proposals::write_category_catalog(&data_root, &categories)?;
            app.manage(AppState {
                data_root,
                database: Mutex::new(database),
            });
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            commands::get_system_info,
            commands::list_categories,
            commands::create_category,
            commands::update_category,
            commands::delete_category,
            commands::list_codex_proposals,
            commands::accept_codex_proposal,
            commands::reject_codex_proposal,
            commands::reopen_rejected_codex_proposal,
            commands::create_codex_delegation,
            commands::get_article,
            commands::search_articles,
            commands::save_article,
            commands::duplicate_article,
            commands::stage_article_image,
            commands::stage_article_image_bytes,
            commands::discard_staged_article_image,
            commands::open_external_url,
            commands::list_articles_for_management,
            commands::delete_article,
            commands::restore_article,
            commands::create_full_backup,
            commands::get_backup_overview,
            commands::inspect_backup,
            commands::restore_backup,
        ])
        .run(tauri::generate_context!())
        .expect("KnowledgeAppの起動に失敗しました");
}

fn forbidden_roots<R: Runtime>(app: &tauri::App<R>) -> Vec<PathBuf> {
    let mut roots = Vec::new();

    if let Ok(current_dir) = std::env::current_dir() {
        if let Some(repository) = find_git_root(&current_dir) {
            roots.push(repository);
        }
    }
    if let Ok(executable) = std::env::current_exe() {
        if let Some(parent) = executable.parent() {
            roots.push(parent.to_path_buf());
        }
    }
    if let Ok(resources) = app.path().resource_dir() {
        roots.push(resources);
    }

    roots
}
