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
        .setup(|app| {
            let app_data_dir = app.path().app_local_data_dir().map_err(|_| {
                errors::AppError::system("利用者データの保存先を取得できませんでした。")
            })?;
            let forbidden_roots = forbidden_roots(app);
            let data_root = DataRootService::initialize(app_data_dir, &forbidden_roots)?;
            let database = Database::open(&data_root.database_path())?;
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
            commands::get_article,
            commands::search_articles,
            commands::save_article,
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
