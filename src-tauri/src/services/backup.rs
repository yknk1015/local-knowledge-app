use std::{
    collections::{HashMap, HashSet},
    fs::{self, File},
    io::{self, Read, Write},
    path::{Component, Path, PathBuf},
};

use chrono::Utc;
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use uuid::Uuid;
use walkdir::WalkDir;
use zip::{CompressionMethod, ZipArchive, ZipWriter, write::SimpleFileOptions};

use crate::{
    errors::{AppError, AppResult},
    models::{BackupCounts, BackupOverview, BackupPreview, BackupResult, RestoreResult},
    repositories::database::Database,
    services::data_root::{DataRootService, find_git_root},
};

const BACKUP_FORMAT_VERSION: u32 = 1;
const RICH_TEXT_FORMAT_VERSION: u32 = 2;
const CURRENT_SCHEMA_VERSION: i64 = 4;
const MAX_ARCHIVE_FILES: usize = 10_000;
const MAX_UNCOMPRESSED_BYTES: u64 = 10 * 1024 * 1024 * 1024;

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
struct BackupManifest {
    backup_format_version: u32,
    app_version: String,
    schema_version: i64,
    rich_text_format_version: u32,
    created_at: String,
    display_name: String,
    file_count: usize,
    total_bytes: u64,
    counts: BackupCounts,
    files: Vec<BackupFileEntry>,
}

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
struct BackupFileEntry {
    path: String,
    size: u64,
    sha256: String,
}

#[derive(Debug)]
struct ArchiveSource {
    archive_path: String,
    source_path: PathBuf,
}

#[derive(Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
struct BackupSettings {
    last_successful_directory: String,
}

pub fn backup_overview(
    data_root: &DataRootService,
    database: &Database,
) -> AppResult<BackupOverview> {
    let mut estimated_bytes = fs::metadata(data_root.database_path())
        .map(|metadata| metadata.len())
        .unwrap_or(0);
    for root in [
        data_root.attachments_path(),
        data_root.manuals_path(),
        data_root.settings_path(),
    ] {
        estimated_bytes = estimated_bytes.saturating_add(directory_size(&root)?);
    }
    let settings_path = data_root.settings_path().join("backup.json");
    let default_directory = fs::read_to_string(settings_path)
        .ok()
        .and_then(|json| serde_json::from_str::<BackupSettings>(&json).ok())
        .map(|settings| settings.last_successful_directory)
        .filter(|path| Path::new(path).is_dir());

    Ok(BackupOverview {
        estimated_bytes,
        counts: database.backup_counts()?,
        default_directory,
    })
}

pub fn create_full_backup(
    data_root: &DataRootService,
    database: &Database,
    destination: &Path,
    display_name: &str,
    overwrite: bool,
) -> AppResult<BackupResult> {
    validate_destination(destination, display_name, overwrite)?;

    let operation_id = Uuid::now_v7().to_string();
    let working_directory = data_root.temp_path().join(format!("backup-{operation_id}"));
    fs::create_dir_all(working_directory.join("data")).map_err(|_| backup_write_error())?;
    let local_archive = working_directory.join("completed.faqbackup");
    let snapshot = working_directory.join("data").join("knowledge.db");

    let result = (|| {
        database.backup_to(&snapshot)?;
        let manifest = build_archive(
            data_root,
            database,
            &snapshot,
            &local_archive,
            display_name.trim(),
        )?;
        inspect_verified_archive(data_root, &local_archive)?;
        publish_archive(data_root, &local_archive, destination, overwrite)?;
        let published = inspect_verified_archive(data_root, destination)?;
        remember_successful_directory(data_root, destination);

        Ok(BackupResult {
            destination_path: destination.display().to_string(),
            display_name: published.display_name,
            created_at: published.created_at,
            total_bytes: manifest.total_bytes,
            counts: manifest.counts,
        })
    })();

    cleanup_generated_directory(&working_directory, &data_root.temp_path());
    result
}

pub fn inspect_backup(data_root: &DataRootService, source: &Path) -> AppResult<BackupPreview> {
    validate_source(source)?;
    let manifest = inspect_verified_archive(data_root, source)?;
    Ok(preview_from_manifest(source, manifest))
}

pub fn restore_backup(
    data_root: &DataRootService,
    database: &mut Database,
    source: &Path,
) -> AppResult<RestoreResult> {
    validate_source(source)?;
    let preview = inspect_backup(data_root, source)?;
    let operation_id = Uuid::now_v7().to_string();
    let staging_directory = data_root
        .restore_staging_path()
        .join(format!("restore-{operation_id}"));
    fs::create_dir_all(&staging_directory)
        .map_err(|_| restore_error("復元用の一時フォルダを作成できませんでした。"))?;

    let restore_result = (|| {
        extract_verified_archive(source, &staging_directory)?;
        let restored_database = staging_directory.join("data").join("knowledge.db");
        Database::validate_snapshot(&restored_database)?;

        for relative in ["attachments/articles", "manuals", "settings"] {
            fs::create_dir_all(staging_directory.join(relative)).map_err(|_| {
                restore_error("バックアップ内のフォルダを復元用に準備できませんでした。")
            })?;
        }

        let safety_name = format!(
            "KnowledgeApp_before_restore_{}",
            Utc::now().format("%Y%m%d_%H%M%S")
        );
        let safety_path = unique_safety_path(data_root, &safety_name);
        create_full_backup(data_root, database, &safety_path, &safety_name, false)?;

        let rollback_database = staging_directory.join("rollback-knowledge.db");
        database.backup_to(&rollback_database)?;
        if let Err(error) = database.restore_from(&restored_database) {
            let _ = database.restore_from(&rollback_database);
            return Err(error);
        }

        if let Err(error) = replace_managed_directories(data_root, &staging_directory) {
            let _ = database.restore_from(&rollback_database);
            return Err(error);
        }

        Ok(RestoreResult {
            source_path: source.display().to_string(),
            safety_backup_path: safety_path.display().to_string(),
            restored_at: Utc::now().to_rfc3339(),
            counts: preview.counts,
        })
    })();

    cleanup_generated_directory(&staging_directory, &data_root.restore_staging_path());
    restore_result
}

fn build_archive(
    data_root: &DataRootService,
    database: &Database,
    snapshot: &Path,
    output: &Path,
    display_name: &str,
) -> AppResult<BackupManifest> {
    let mut sources = vec![ArchiveSource {
        archive_path: "data/knowledge.db".to_owned(),
        source_path: snapshot.to_path_buf(),
    }];
    collect_directory(
        &data_root.attachments_path(),
        "attachments/articles",
        &mut sources,
    )?;
    collect_directory(&data_root.manuals_path(), "manuals", &mut sources)?;
    collect_directory(&data_root.settings_path(), "settings", &mut sources)?;

    sources.sort_by(|left, right| left.archive_path.cmp(&right.archive_path));
    if sources.len() > MAX_ARCHIVE_FILES {
        return Err(AppError::new(
            "BK-004",
            "バックアップ対象のファイル数が多すぎます。",
            "不要な添付画像や手順書を整理してから、もう一度実行してください。",
        ));
    }

    let files = sources
        .iter()
        .map(|source| {
            let metadata = fs::metadata(&source.source_path).map_err(|_| backup_read_error())?;
            Ok(BackupFileEntry {
                path: source.archive_path.clone(),
                size: metadata.len(),
                sha256: hash_file(&source.source_path)?,
            })
        })
        .collect::<AppResult<Vec<_>>>()?;
    let total_bytes = files.iter().map(|file| file.size).sum::<u64>();
    if total_bytes > MAX_UNCOMPRESSED_BYTES {
        return Err(AppError::new(
            "BK-004",
            "バックアップ対象の合計サイズが上限を超えています。",
            "不要な添付画像や手順書を整理してから、もう一度実行してください。",
        ));
    }

    let manifest = BackupManifest {
        backup_format_version: BACKUP_FORMAT_VERSION,
        app_version: env!("CARGO_PKG_VERSION").to_owned(),
        schema_version: database.schema_version()?,
        rich_text_format_version: RICH_TEXT_FORMAT_VERSION,
        created_at: Utc::now().to_rfc3339(),
        display_name: display_name.to_owned(),
        file_count: files.len(),
        total_bytes,
        counts: database.backup_counts()?,
        files,
    };

    let output_file = File::create(output).map_err(|_| backup_write_error())?;
    let mut writer = ZipWriter::new(output_file);
    let options = SimpleFileOptions::default()
        .compression_method(CompressionMethod::Deflated)
        .unix_permissions(0o600);
    let manifest_json = serde_json::to_vec_pretty(&manifest).map_err(|_| backup_write_error())?;
    writer
        .start_file("manifest.json", options)
        .map_err(|_| backup_write_error())?;
    writer
        .write_all(&manifest_json)
        .map_err(|_| backup_write_error())?;

    for source in sources {
        writer
            .start_file(&source.archive_path, options)
            .map_err(|_| backup_write_error())?;
        let mut input = File::open(&source.source_path).map_err(|_| backup_read_error())?;
        io::copy(&mut input, &mut writer).map_err(|_| backup_write_error())?;
    }
    writer.finish().map_err(|_| backup_write_error())?;
    Ok(manifest)
}

fn collect_directory(
    root: &Path,
    archive_root: &str,
    output: &mut Vec<ArchiveSource>,
) -> AppResult<()> {
    for entry in WalkDir::new(root).follow_links(false) {
        let entry = entry.map_err(|_| backup_read_error())?;
        if entry.file_type().is_symlink() {
            return Err(AppError::new(
                "BK-005",
                "バックアップ対象に安全に処理できないリンクが含まれています。",
                "対象フォルダ内のショートカットやシンボリックリンクを取り除いてください。",
            ));
        }
        if !entry.file_type().is_file() {
            continue;
        }
        let relative = entry
            .path()
            .strip_prefix(root)
            .map_err(|_| backup_read_error())?;
        let relative = archive_path(relative)?;
        output.push(ArchiveSource {
            archive_path: format!("{archive_root}/{relative}"),
            source_path: entry.path().to_path_buf(),
        });
    }
    Ok(())
}

fn directory_size(root: &Path) -> AppResult<u64> {
    let mut size = 0_u64;
    for entry in WalkDir::new(root).follow_links(false) {
        let entry = entry.map_err(|_| backup_read_error())?;
        if entry.file_type().is_symlink() {
            return Err(AppError::new(
                "BK-005",
                "バックアップ対象に安全に処理できないリンクが含まれています。",
                "対象フォルダ内のショートカットやシンボリックリンクを取り除いてください。",
            ));
        }
        if entry.file_type().is_file() {
            size = size.saturating_add(entry.metadata().map_err(|_| backup_read_error())?.len());
        }
    }
    Ok(size)
}

fn inspect_verified_archive(
    data_root: &DataRootService,
    source: &Path,
) -> AppResult<BackupManifest> {
    let manifest = verify_archive(source)?;
    validate_manifest_versions(&manifest)?;

    let inspection_directory = data_root
        .temp_path()
        .join(format!("inspect-{}", Uuid::now_v7()));
    fs::create_dir_all(inspection_directory.join("data"))
        .map_err(|_| restore_error("バックアップ検査用の一時フォルダを作成できませんでした。"))?;
    let database_path = inspection_directory.join("data").join("knowledge.db");
    let database_result = extract_named_file(source, "data/knowledge.db", &database_path)
        .and_then(|_| Database::validate_snapshot(&database_path));
    cleanup_generated_directory(&inspection_directory, &data_root.temp_path());
    database_result?;
    Ok(manifest)
}

fn verify_archive(source: &Path) -> AppResult<BackupManifest> {
    let input = File::open(source)
        .map_err(|_| backup_invalid_error("バックアップファイルを開けませんでした。"))?;
    let mut archive = ZipArchive::new(input)
        .map_err(|_| backup_invalid_error("バックアップファイルの形式が正しくありません。"))?;
    if archive.is_empty() || archive.len() > MAX_ARCHIVE_FILES + 1 {
        return Err(backup_invalid_error(
            "バックアップ内のファイル数が正しくありません。",
        ));
    }

    let manifest: BackupManifest = {
        let mut manifest_file = archive
            .by_name("manifest.json")
            .map_err(|_| backup_invalid_error("バックアップに検証情報が含まれていません。"))?;
        if manifest_file.size() > 5 * 1024 * 1024 {
            return Err(backup_invalid_error(
                "バックアップの検証情報が大きすぎます。",
            ));
        }
        let mut manifest_json = String::new();
        manifest_file
            .read_to_string(&mut manifest_json)
            .map_err(|_| backup_invalid_error("バックアップの検証情報を読み取れませんでした。"))?;
        serde_json::from_str(&manifest_json)
            .map_err(|_| backup_invalid_error("バックアップの検証情報が正しくありません。"))?
    };

    if manifest.files.len() != manifest.file_count || manifest.files.len() > MAX_ARCHIVE_FILES {
        return Err(backup_invalid_error(
            "バックアップのファイル一覧が正しくありません。",
        ));
    }
    let calculated_total = manifest.files.iter().map(|file| file.size).sum::<u64>();
    if calculated_total != manifest.total_bytes || calculated_total > MAX_UNCOMPRESSED_BYTES {
        return Err(backup_invalid_error(
            "バックアップの合計サイズが正しくありません。",
        ));
    }

    let mut expected = HashMap::new();
    for file in &manifest.files {
        validate_archive_entry_name(&file.path)?;
        if expected.insert(file.path.as_str(), file).is_some() {
            return Err(backup_invalid_error(
                "バックアップのファイル一覧に重複があります。",
            ));
        }
    }
    if !expected.contains_key("data/knowledge.db") {
        return Err(backup_invalid_error(
            "バックアップにFAQデータベースが含まれていません。",
        ));
    }

    let mut found = HashSet::new();
    for index in 0..archive.len() {
        let mut file = archive.by_index(index).map_err(|_| {
            backup_invalid_error("バックアップ内のファイルを読み取れませんでした。")
        })?;
        let name = file.name().to_owned();
        validate_archive_entry_name(&name)?;
        if !found.insert(name.clone()) {
            return Err(backup_invalid_error(
                "バックアップ内に同名ファイルがあります。",
            ));
        }
        if name == "manifest.json" {
            continue;
        }
        let expected_file = expected.get(name.as_str()).ok_or_else(|| {
            backup_invalid_error("バックアップに未登録のファイルが含まれています。")
        })?;
        if file.size() != expected_file.size {
            return Err(backup_invalid_error(
                "バックアップ内のファイルサイズが一致しません。",
            ));
        }
        let digest = hash_reader(&mut file)?;
        if digest != expected_file.sha256 {
            return Err(backup_invalid_error(
                "バックアップ内のファイルが破損または変更されています。",
            ));
        }
    }
    if expected.keys().any(|name| !found.contains(*name)) {
        return Err(backup_invalid_error(
            "バックアップ内に不足しているファイルがあります。",
        ));
    }
    Ok(manifest)
}

fn extract_verified_archive(source: &Path, destination: &Path) -> AppResult<()> {
    verify_archive(source)?;
    let input = File::open(source)
        .map_err(|_| backup_invalid_error("バックアップファイルを開けませんでした。"))?;
    let mut archive = ZipArchive::new(input)
        .map_err(|_| backup_invalid_error("バックアップファイルの形式が正しくありません。"))?;
    for index in 0..archive.len() {
        let mut file = archive
            .by_index(index)
            .map_err(|_| restore_error("バックアップ内のファイルを読み取れませんでした。"))?;
        let name = file.name().to_owned();
        validate_archive_entry_name(&name)?;
        if name == "manifest.json" {
            continue;
        }
        let output_path = destination.join(name.replace('/', std::path::MAIN_SEPARATOR_STR));
        if let Some(parent) = output_path.parent() {
            fs::create_dir_all(parent)
                .map_err(|_| restore_error("復元用のフォルダを作成できませんでした。"))?;
        }
        let mut output = File::create(&output_path)
            .map_err(|_| restore_error("バックアップ内のファイルを展開できませんでした。"))?;
        io::copy(&mut file, &mut output)
            .map_err(|_| restore_error("バックアップ内のファイルを展開できませんでした。"))?;
        output
            .sync_all()
            .map_err(|_| restore_error("復元用ファイルの書き込みを完了できませんでした。"))?;
    }
    Ok(())
}

fn extract_named_file(source: &Path, name: &str, destination: &Path) -> AppResult<()> {
    let input = File::open(source)
        .map_err(|_| backup_invalid_error("バックアップファイルを開けませんでした。"))?;
    let mut archive = ZipArchive::new(input)
        .map_err(|_| backup_invalid_error("バックアップファイルの形式が正しくありません。"))?;
    let mut archived = archive
        .by_name(name)
        .map_err(|_| backup_invalid_error("バックアップにFAQデータベースが含まれていません。"))?;
    let mut output = File::create(destination)
        .map_err(|_| restore_error("検査用データベースを作成できませんでした。"))?;
    io::copy(&mut archived, &mut output)
        .map_err(|_| restore_error("検査用データベースを展開できませんでした。"))?;
    Ok(())
}

fn publish_archive(
    data_root: &DataRootService,
    local_archive: &Path,
    destination: &Path,
    overwrite: bool,
) -> AppResult<()> {
    let partial = destination.with_file_name(format!(
        "{}.{}.partial",
        destination
            .file_name()
            .and_then(|name| name.to_str())
            .unwrap_or("backup.faqbackup"),
        Uuid::now_v7()
    ));
    fs::copy(local_archive, &partial).map_err(|_| backup_write_error())?;
    let expected_size = fs::metadata(local_archive)
        .map_err(|_| backup_read_error())?
        .len();
    let copied_size = fs::metadata(&partial)
        .map_err(|_| backup_write_error())?
        .len();
    if expected_size != copied_size {
        let _ = fs::remove_file(&partial);
        return Err(backup_write_error());
    }

    let replaced = destination.with_file_name(format!(
        "{}.{}.replace",
        destination
            .file_name()
            .and_then(|name| name.to_str())
            .unwrap_or("backup.faqbackup"),
        Uuid::now_v7()
    ));
    let had_existing = destination.exists();
    if had_existing {
        if !overwrite {
            let _ = fs::remove_file(&partial);
            return Err(existing_backup_error());
        }
        fs::rename(destination, &replaced).map_err(|_| backup_write_error())?;
    }

    if fs::rename(&partial, destination).is_err() {
        if had_existing {
            let _ = fs::rename(&replaced, destination);
        }
        let _ = fs::remove_file(&partial);
        return Err(backup_write_error());
    }

    if let Err(error) = inspect_verified_archive(data_root, destination) {
        let _ = fs::remove_file(destination);
        if had_existing {
            let _ = fs::rename(&replaced, destination);
        }
        return Err(error);
    }
    if had_existing {
        let _ = fs::remove_file(replaced);
    }
    Ok(())
}

fn replace_managed_directories(data_root: &DataRootService, staging: &Path) -> AppResult<()> {
    let rollback_root = staging.join("rollback-files");
    fs::create_dir_all(&rollback_root)
        .map_err(|_| restore_error("現在の添付ファイルを一時退避できませんでした。"))?;
    let directories = [
        (
            staging.join("attachments/articles"),
            data_root.attachments_path(),
            "attachments",
        ),
        (staging.join("manuals"), data_root.manuals_path(), "manuals"),
        (
            staging.join("settings"),
            data_root.settings_path(),
            "settings",
        ),
    ];
    let mut swapped: Vec<(PathBuf, PathBuf)> = Vec::new();

    for (incoming, target, key) in directories {
        let rollback = rollback_root.join(key);
        if target.exists() {
            fs::rename(&target, &rollback).map_err(|_| {
                rollback_directories(&swapped, staging);
                restore_error("現在の添付ファイルを一時退避できませんでした。")
            })?;
        }
        if fs::rename(&incoming, &target).is_err() {
            if rollback.exists() {
                let _ = fs::rename(&rollback, &target);
            }
            rollback_directories(&swapped, staging);
            return Err(restore_error(
                "バックアップの添付ファイルを配置できませんでした。",
            ));
        }
        swapped.push((target, rollback));
    }
    Ok(())
}

fn rollback_directories(swapped: &[(PathBuf, PathBuf)], staging: &Path) {
    for (index, (target, rollback)) in swapped.iter().enumerate().rev() {
        let failed = staging.join(format!("failed-new-{index}"));
        if target.exists() {
            let _ = fs::rename(target, failed);
        }
        if rollback.exists() {
            let _ = fs::rename(rollback, target);
        }
    }
}

fn validate_destination(destination: &Path, display_name: &str, overwrite: bool) -> AppResult<()> {
    validate_backup_path(destination)?;
    validate_backup_name(display_name)?;
    let parent = destination.parent().ok_or_else(backup_write_error)?;
    if !parent.is_dir() {
        return Err(backup_write_error());
    }
    if find_git_root(parent).is_some() {
        return Err(AppError::new(
            "BK-008",
            "ソースコードの管理フォルダ内にはバックアップを保存できません。",
            "ドキュメント、外付けドライブ、ネットワークドライブなど別の保存先を選択してください。",
        ));
    }
    if destination.exists() && !overwrite {
        return Err(existing_backup_error());
    }
    Ok(())
}

fn validate_source(source: &Path) -> AppResult<()> {
    validate_backup_path(source)?;
    if !source.is_file() {
        return Err(backup_invalid_error(
            "選択したバックアップファイルが見つかりません。",
        ));
    }
    Ok(())
}

fn validate_backup_path(path: &Path) -> AppResult<()> {
    if !path.is_absolute()
        || path
            .extension()
            .and_then(|value| value.to_str())
            .map(str::to_ascii_lowercase)
            .as_deref()
            != Some("faqbackup")
    {
        return Err(AppError::new(
            "BK-001",
            "バックアップの保存場所またはファイル名が正しくありません。",
            "絶対パスを使用し、ファイル名の末尾を.faqbackupにしてください。",
        ));
    }
    let file_stem = path
        .file_stem()
        .and_then(|value| value.to_str())
        .unwrap_or_default();
    validate_backup_name(file_stem)
}

fn validate_backup_name(name: &str) -> AppResult<()> {
    let trimmed = name.trim();
    let invalid_char = |character: char| {
        matches!(
            character,
            '<' | '>' | ':' | '"' | '/' | '\\' | '|' | '?' | '*'
        ) || character.is_control()
    };
    let reserved = [
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8",
        "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];
    if trimmed.is_empty()
        || trimmed.chars().count() > 120
        || trimmed.ends_with(['.', ' '])
        || trimmed.chars().any(invalid_char)
        || reserved
            .iter()
            .any(|reserved| trimmed.eq_ignore_ascii_case(reserved))
    {
        return Err(AppError::new(
            "BK-001",
            "バックアップ名に使用できない文字または名前が含まれています。",
            "120文字以内で、記号 < > : \" / \\ | ? * と末尾の空白・ピリオドを避けてください。",
        ));
    }
    Ok(())
}

fn validate_manifest_versions(manifest: &BackupManifest) -> AppResult<()> {
    if manifest.backup_format_version != BACKUP_FORMAT_VERSION
        || manifest.schema_version > CURRENT_SCHEMA_VERSION
        || manifest.rich_text_format_version > RICH_TEXT_FORMAT_VERSION
    {
        return Err(AppError::new(
            "BK-009",
            "このバックアップは現在のアプリでは復元できない形式です。",
            "KnowledgeAppを最新版へ更新してから、もう一度お試しください。",
        ));
    }
    Ok(())
}

fn validate_archive_entry_name(name: &str) -> AppResult<()> {
    let path = Path::new(name);
    if name.is_empty()
        || name.contains('\\')
        || path.is_absolute()
        || path
            .components()
            .any(|component| !matches!(component, Component::Normal(_)))
    {
        return Err(backup_invalid_error(
            "バックアップに安全でないファイルパスが含まれています。",
        ));
    }
    Ok(())
}

fn archive_path(path: &Path) -> AppResult<String> {
    let parts = path
        .components()
        .map(|component| match component {
            Component::Normal(value) => value.to_str().map(str::to_owned),
            _ => None,
        })
        .collect::<Option<Vec<_>>>()
        .ok_or_else(backup_read_error)?;
    if parts.is_empty() {
        return Err(backup_read_error());
    }
    Ok(parts.join("/"))
}

fn hash_file(path: &Path) -> AppResult<String> {
    let mut file = File::open(path).map_err(|_| backup_read_error())?;
    let mut hasher = Sha256::new();
    let mut buffer = [0_u8; 64 * 1024];
    loop {
        let read = file.read(&mut buffer).map_err(|_| backup_read_error())?;
        if read == 0 {
            break;
        }
        hasher.update(&buffer[..read]);
    }
    Ok(hex_digest(hasher.finalize().as_slice()))
}

fn hash_reader(reader: &mut impl Read) -> AppResult<String> {
    let mut hasher = Sha256::new();
    let mut buffer = [0_u8; 64 * 1024];
    loop {
        let read = reader.read(&mut buffer).map_err(|_| {
            backup_invalid_error("バックアップ内のファイルを読み取れませんでした。")
        })?;
        if read == 0 {
            break;
        }
        hasher.update(&buffer[..read]);
    }
    Ok(hex_digest(hasher.finalize().as_slice()))
}

fn hex_digest(bytes: &[u8]) -> String {
    bytes.iter().map(|byte| format!("{byte:02x}")).collect()
}

fn unique_safety_path(data_root: &DataRootService, base_name: &str) -> PathBuf {
    let mut candidate = data_root
        .safety_backups_path()
        .join(format!("{base_name}.faqbackup"));
    if candidate.exists() {
        candidate = data_root
            .safety_backups_path()
            .join(format!("{base_name}_{}.faqbackup", Uuid::now_v7()));
    }
    candidate
}

fn remember_successful_directory(data_root: &DataRootService, destination: &Path) {
    let Some(parent) = destination.parent() else {
        return;
    };
    let settings = BackupSettings {
        last_successful_directory: parent.display().to_string(),
    };
    let Ok(json) = serde_json::to_vec_pretty(&settings) else {
        return;
    };
    let final_path = data_root.settings_path().join("backup.json");
    let temporary_path = data_root
        .settings_path()
        .join(format!("backup-{}.partial", Uuid::now_v7()));
    if fs::write(&temporary_path, json).is_ok() {
        if final_path.exists() {
            let _ = fs::remove_file(&final_path);
        }
        if fs::rename(&temporary_path, final_path).is_err() {
            let _ = fs::remove_file(temporary_path);
        }
    }
}

fn preview_from_manifest(source: &Path, manifest: BackupManifest) -> BackupPreview {
    BackupPreview {
        source_path: source.display().to_string(),
        display_name: manifest.display_name,
        created_at: manifest.created_at,
        app_version: manifest.app_version,
        schema_version: manifest.schema_version,
        backup_format_version: manifest.backup_format_version,
        total_bytes: manifest.total_bytes,
        counts: manifest.counts,
    }
}

fn cleanup_generated_directory(path: &Path, allowed_parent: &Path) {
    if path.starts_with(allowed_parent) && path != allowed_parent {
        let _ = fs::remove_dir_all(path);
    }
}

fn existing_backup_error() -> AppError {
    AppError::new(
        "BK-002",
        "同じ名前のバックアップがすでに存在します。",
        "上書きする場合は確認画面で承認するか、別の名前を指定してください。",
    )
}

fn backup_write_error() -> AppError {
    AppError::new(
        "BK-003",
        "指定したバックアップ先へ書き込めませんでした。",
        "ネットワーク接続と保存先の権限・空き容量を確認するか、別の保存先を選択してください。",
    )
}

fn backup_read_error() -> AppError {
    AppError::new(
        "BK-004",
        "バックアップ対象のデータを読み取れませんでした。",
        "FAQを閉じ、データ保存先へアクセスできることを確認してから再実行してください。",
    )
}

fn backup_invalid_error(message: &str) -> AppError {
    AppError::new(
        "BK-006",
        message,
        "破損していない別の.faqbackupファイルを選択してください。",
    )
}

fn restore_error(message: &str) -> AppError {
    AppError::new(
        "BK-010",
        message,
        "現在のFAQデータは維持されています。保存先の空き容量を確認し、もう一度お試しください。",
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::repositories::database::ArticleRecord;
    use serde_json::json;

    fn test_data() -> (tempfile::TempDir, DataRootService, Database) {
        let directory = tempfile::tempdir().unwrap();
        let root = DataRootService::initialize(directory.path().join("app-data"), &[]).unwrap();
        let mut database = Database::open(&root.database_path()).unwrap();
        let category = database.create_category("Windows", None).unwrap();
        let body = json!({"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"再起動します"}]}]});
        let article_id = Uuid::now_v7().to_string();
        database
            .save_article(ArticleRecord {
                id: &article_id,
                is_new: true,
                category_id: &category.id,
                title: "画面が暗い",
                summary: "画面設定を確認します",
                body_doc: &body,
                body_plain_text: "再起動します",
                status: "published",
                importance: 1,
                new_badge_until: None,
                updated_badge_until: None,
                is_hidden: false,
                attachments: &[],
            })
            .unwrap();
        fs::write(
            root.settings_path().join("window.json"),
            b"{\"width\":1200}",
        )
        .unwrap();
        fs::write(
            root.attachments_path().join("sample.png"),
            b"original-image",
        )
        .unwrap();
        (directory, root, database)
    }

    #[test]
    fn creates_and_inspects_a_complete_backup() {
        let (directory, root, database) = test_data();
        let destination = directory.path().join("My Backup.faqbackup");

        let created =
            create_full_backup(&root, &database, &destination, "My Backup", false).unwrap();
        let inspected = inspect_backup(&root, &destination).unwrap();

        assert_eq!(created.counts.articles, 1);
        assert_eq!(inspected.counts.categories, 1);
        assert_eq!(inspected.display_name, "My Backup");
        assert!(destination.is_file());
        assert!(!destination.with_extension("faqbackup.partial").exists());
    }

    #[test]
    fn restores_database_and_keeps_a_safety_backup() {
        let (directory, root, mut database) = test_data();
        let destination = directory.path().join("restore-source.faqbackup");
        create_full_backup(&root, &database, &destination, "restore-source", false).unwrap();
        database
            .create_category("復元後に消える分類", None)
            .unwrap();
        fs::write(root.settings_path().join("window.json"), b"{\"width\":800}").unwrap();
        fs::write(root.attachments_path().join("sample.png"), b"changed-image").unwrap();
        assert_eq!(database.list_categories().unwrap().len(), 2);

        let result = restore_backup(&root, &mut database, &destination).unwrap();

        assert_eq!(database.list_categories().unwrap().len(), 1);
        assert!(Path::new(&result.safety_backup_path).is_file());
        assert_eq!(
            fs::read(root.settings_path().join("window.json")).unwrap(),
            b"{\"width\":1200}"
        );
        assert_eq!(
            fs::read(root.attachments_path().join("sample.png")).unwrap(),
            b"original-image"
        );
    }

    #[test]
    fn rejects_an_existing_destination_without_confirmation() {
        let (directory, root, database) = test_data();
        let destination = directory.path().join("existing.faqbackup");
        fs::write(&destination, b"existing").unwrap();

        let error =
            create_full_backup(&root, &database, &destination, "existing", false).unwrap_err();

        assert_eq!(error.code, "BK-002");
        assert_eq!(fs::read(destination).unwrap(), b"existing");
    }

    #[test]
    fn rejects_a_backup_destination_inside_a_git_repository() {
        let (directory, root, database) = test_data();
        let repository = directory.path().join("repository");
        fs::create_dir_all(repository.join(".git")).unwrap();
        let destination = repository.join("unsafe.faqbackup");

        let error =
            create_full_backup(&root, &database, &destination, "unsafe", false).unwrap_err();

        assert_eq!(error.code, "BK-008");
        assert!(!destination.exists());
    }

    #[test]
    fn detects_a_corrupted_archive_before_restore() {
        let (directory, root, mut database) = test_data();
        let destination = directory.path().join("corrupted.faqbackup");
        create_full_backup(&root, &database, &destination, "corrupted", false).unwrap();
        let mut bytes = fs::read(&destination).unwrap();
        let middle = bytes.len() / 2;
        bytes[middle] ^= 0xff;
        fs::write(&destination, bytes).unwrap();

        let error = restore_backup(&root, &mut database, &destination).unwrap_err();

        assert_eq!(error.code, "BK-006");
        assert_eq!(database.list_categories().unwrap().len(), 1);
        assert!(!root.safety_backups_path().read_dir().unwrap().any(|_| true));
    }
}
