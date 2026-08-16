mod commands;
mod errors;
mod models;
mod repositories;
mod services;

use std::{path::PathBuf, sync::Mutex};

use models::AuthenticatedUser;
use repositories::database::Database;
use services::data_root::{DataRootService, find_git_root};
use tauri::{Manager, Runtime};
use tauri_plugin_dialog::{DialogExt, MessageDialogKind};

pub struct AppState {
    data_root: DataRootService,
    database: Mutex<Database>,
    session: Mutex<Option<AuthenticatedUser>>,
}

pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_single_instance::init(|app, _args, _cwd| {
            if let Some(window) = app.get_webview_window("main") {
                let _ = window.unminimize();
                let _ = window.show();
                let _ = window.set_focus();
            }
        }))
        .plugin(tauri_plugin_clipboard_manager::init())
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_opener::init())
        .setup(|app| {
            let app_data_dir = app.path().app_local_data_dir().map_err(|_| {
                errors::AppError::system("利用者データの保存先を取得できませんでした。")
            })?;
            let forbidden_roots = forbidden_roots(app);
            let data_root = DataRootService::initialize(app_data_dir, &forbidden_roots)?;
            services::attachments::cleanup_stale_stages(&data_root);
            let database = match (|| {
                services::backup::create_pre_migration_backup_if_needed(&data_root)?;
                Database::open(&data_root.database_path())
            })() {
                Ok(database) => database,
                Err(error) => {
                    app.dialog()
                        .message(format!("{}\n\n対処: {}", error.message, error.action))
                        .title("KnowledgeAppを起動できません")
                        .kind(MessageDialogKind::Error)
                        .blocking_show();
                    return Err(error.into());
                }
            };
            let categories = database.list_categories()?;
            services::codex_proposals::write_category_catalog(&data_root, &categories)?;
            app.manage(AppState {
                data_root,
                database: Mutex::new(database),
                session: Mutex::new(None),
            });
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            commands::login,
            commands::logout,
            commands::get_current_user,
            commands::list_users,
            commands::create_user,
            commands::set_user_active,
            commands::reset_user_password,
            commands::get_system_info,
            commands::get_settings,
            commands::save_settings,
            commands::get_password_policy,
            commands::save_password_policy,
            commands::list_categories,
            commands::create_category,
            commands::update_category,
            commands::reorder_category,
            commands::delete_category,
            commands::list_codex_proposals,
            commands::accept_codex_proposal,
            commands::reject_codex_proposal,
            commands::reopen_rejected_codex_proposal,
            commands::create_codex_delegation,
            commands::get_article,
            commands::get_codex_merge_publication_context,
            commands::mark_codex_merge_sources,
            commands::clear_article_merge,
            commands::search_articles,
            commands::record_search_log,
            commands::record_article_view,
            commands::list_search_logs,
            commands::list_view_logs,
            commands::delete_history,
            commands::list_synonym_groups,
            commands::save_synonym_group,
            commands::delete_synonym_group,
            commands::search_related_articles,
            commands::save_article,
            commands::duplicate_article,
            commands::stage_article_image,
            commands::stage_article_image_bytes,
            commands::discard_staged_article_image,
            commands::open_external_url,
            commands::list_articles_for_management,
            commands::export_faq_csv,
            commands::inspect_faq_csv,
            commands::import_faq_csv,
            commands::export_json,
            commands::inspect_json,
            commands::import_json,
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
