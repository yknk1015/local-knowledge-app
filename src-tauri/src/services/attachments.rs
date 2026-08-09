use std::{
    collections::{HashMap, HashSet},
    fs,
    path::{Component, Path, PathBuf},
    time::{Duration, SystemTime},
};

use chrono::Utc;
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use uuid::Uuid;

use crate::{
    errors::{AppError, AppResult},
    models::{Article, ArticleAttachment, StagedArticleImage},
    repositories::database::AttachmentRecord,
    services::{data_root::DataRootService, rich_content::AttachmentReference},
};

const MAX_IMAGE_BYTES: usize = 10 * 1024 * 1024;
const STAGING_DIRECTORY: &str = "staged-article-images";

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct StagedImageMetadata {
    id: String,
    original_name: String,
    media_type: String,
    byte_size: i64,
    sha256: String,
    alt_text: String,
    created_at: String,
}

pub struct PreparedAttachments {
    pub records: Vec<AttachmentRecord>,
    created_files: Vec<PathBuf>,
    removed_files: Vec<PathBuf>,
    staged_ids: Vec<String>,
}

struct CreatedFilesGuard {
    paths: Vec<PathBuf>,
    armed: bool,
}

impl CreatedFilesGuard {
    fn new() -> Self {
        Self {
            paths: Vec::new(),
            armed: true,
        }
    }

    fn finish(mut self) -> Vec<PathBuf> {
        self.armed = false;
        std::mem::take(&mut self.paths)
    }
}

impl Drop for CreatedFilesGuard {
    fn drop(&mut self) {
        if self.armed {
            rollback_created_files(&self.paths);
        }
    }
}

pub fn stage_from_path(
    data_root: &DataRootService,
    source_path: &Path,
) -> AppResult<StagedArticleImage> {
    let metadata = fs::symlink_metadata(source_path).map_err(|_| image_read_error())?;
    if !metadata.file_type().is_file() || metadata.file_type().is_symlink() {
        return Err(image_read_error());
    }
    if metadata.len() > MAX_IMAGE_BYTES as u64 {
        return Err(image_too_large());
    }
    let bytes = fs::read(source_path).map_err(|_| image_read_error())?;
    let original_name = source_path
        .file_name()
        .and_then(|value| value.to_str())
        .unwrap_or("image")
        .to_owned();
    stage_bytes(data_root, &original_name, &bytes)
}

pub fn stage_bytes(
    data_root: &DataRootService,
    original_name: &str,
    bytes: &[u8],
) -> AppResult<StagedArticleImage> {
    if bytes.len() > MAX_IMAGE_BYTES {
        return Err(image_too_large());
    }
    let image_type = detect_image_type(bytes).ok_or_else(unsupported_image)?;
    let id = Uuid::now_v7().to_string();
    let original_name = safe_original_name(original_name, image_type.extension);
    let alt_text = Path::new(&original_name)
        .file_stem()
        .and_then(|value| value.to_str())
        .unwrap_or("画像")
        .chars()
        .take(500)
        .collect::<String>();
    let sha256 = sha256_hex(bytes);
    let created_at = Utc::now().to_rfc3339();
    let metadata = StagedImageMetadata {
        id: id.clone(),
        original_name: original_name.clone(),
        media_type: image_type.media_type.to_owned(),
        byte_size: bytes.len() as i64,
        sha256: sha256.clone(),
        alt_text: alt_text.clone(),
        created_at,
    };

    let staging = staging_path(data_root);
    fs::create_dir_all(&staging).map_err(|_| image_write_error())?;
    let image_path = staging.join(format!("{id}.{}", image_type.extension));
    let partial_path = staging.join(format!(".{id}.partial"));
    fs::write(&partial_path, bytes).map_err(|_| image_write_error())?;
    if let Err(error) = fs::rename(&partial_path, &image_path) {
        let _ = fs::remove_file(&partial_path);
        return Err(AppError::new(
            "ATT-003",
            format!("画像を一時保存できませんでした（{error}）。"),
            "保存先へ書き込めることを確認し、画像を選び直してください。",
        ));
    }
    let metadata_path = staging.join(format!("{id}.json"));
    let metadata_bytes = serde_json::to_vec_pretty(&metadata).map_err(|_| image_write_error())?;
    if fs::write(&metadata_path, metadata_bytes).is_err() {
        let _ = fs::remove_file(&image_path);
        return Err(image_write_error());
    }

    Ok(StagedArticleImage {
        id,
        original_name,
        media_type: image_type.media_type.to_owned(),
        byte_size: bytes.len() as i64,
        sha256,
        alt_text,
        asset_path: image_path.display().to_string(),
    })
}

pub fn stage_copy_of_attachment(
    data_root: &DataRootService,
    attachment: &ArticleAttachment,
) -> AppResult<StagedArticleImage> {
    let bytes = verified_existing_bytes(data_root, attachment)?;
    stage_bytes(data_root, &attachment.original_name, &bytes)
}

pub fn prepare(
    data_root: &DataRootService,
    article_id: &str,
    references: &[AttachmentReference],
    existing: &[ArticleAttachment],
) -> AppResult<PreparedAttachments> {
    Uuid::parse_str(article_id).map_err(|_| attachment_reference_error())?;
    let existing_by_id = existing
        .iter()
        .map(|attachment| (attachment.id.as_str(), attachment))
        .collect::<HashMap<_, _>>();
    let mut seen = HashSet::new();
    let mut records = Vec::with_capacity(references.len());
    let mut created_files = CreatedFilesGuard::new();
    let mut staged_ids = Vec::new();

    for reference in references {
        if !seen.insert(reference.id.as_str()) {
            continue;
        }
        if let Some(attachment) = existing_by_id.get(reference.id.as_str()) {
            verified_existing_bytes(data_root, attachment)?;
            records.push(AttachmentRecord {
                id: attachment.id.clone(),
                relative_path: attachment.asset_path.clone(),
                original_name: attachment.original_name.clone(),
                media_type: attachment.media_type.clone(),
                byte_size: attachment.byte_size,
                sha256: attachment.sha256.clone(),
                alt_text: reference.alt_text.clone(),
                created_at: attachment.created_at.clone(),
            });
            continue;
        }

        let staged = load_and_verify_stage(data_root, &reference.id)?;
        let image_type =
            image_type_for_media(&staged.metadata.media_type).ok_or_else(unsupported_image)?;
        let destination_directory = data_root.attachments_path().join(article_id);
        fs::create_dir_all(&destination_directory).map_err(|_| image_write_error())?;
        let destination =
            destination_directory.join(format!("{}.{}", reference.id, image_type.extension));
        let relative_path = format!("{article_id}/{}.{}", reference.id, image_type.extension);

        if destination.exists() {
            let bytes = fs::read(&destination).map_err(|_| image_write_error())?;
            if sha256_hex(&bytes) != staged.metadata.sha256 {
                return Err(image_write_error());
            }
        } else {
            let partial = destination_directory.join(format!(".{}.partial", reference.id));
            if fs::copy(&staged.image_path, &partial).is_err()
                || fs::rename(&partial, &destination).is_err()
            {
                let _ = fs::remove_file(&partial);
                return Err(image_write_error());
            }
            created_files.paths.push(destination);
        }

        records.push(AttachmentRecord {
            id: reference.id.clone(),
            relative_path,
            original_name: staged.metadata.original_name,
            media_type: staged.metadata.media_type,
            byte_size: staged.metadata.byte_size,
            sha256: staged.metadata.sha256,
            alt_text: reference.alt_text.clone(),
            created_at: staged.metadata.created_at,
        });
        staged_ids.push(reference.id.clone());
    }

    let removed_files = existing
        .iter()
        .filter(|attachment| !seen.contains(attachment.id.as_str()))
        .filter_map(|attachment| resolve_managed_attachment(data_root, &attachment.asset_path).ok())
        .collect();

    Ok(PreparedAttachments {
        records,
        created_files: created_files.finish(),
        removed_files,
        staged_ids,
    })
}

fn verified_existing_bytes(
    data_root: &DataRootService,
    attachment: &ArticleAttachment,
) -> AppResult<Vec<u8>> {
    let managed_path = resolve_managed_attachment(data_root, &attachment.asset_path)?;
    let bytes = fs::read(managed_path).map_err(|_| staged_image_missing())?;
    let detected = detect_image_type(&bytes).ok_or_else(unsupported_image)?;
    if detected.media_type != attachment.media_type
        || bytes.len() as i64 != attachment.byte_size
        || sha256_hex(&bytes) != attachment.sha256
    {
        return Err(staged_image_missing());
    }
    Ok(bytes)
}

pub fn rollback(prepared: &PreparedAttachments) {
    rollback_created_files(&prepared.created_files);
}

pub fn commit(data_root: &DataRootService, prepared: &PreparedAttachments) {
    for id in &prepared.staged_ids {
        discard_stage(data_root, id);
    }
    for path in &prepared.removed_files {
        let _ = fs::remove_file(path);
        remove_empty_parent(path, &data_root.attachments_path());
    }
}

pub fn discard_stage(data_root: &DataRootService, id: &str) {
    let Ok(uuid) = Uuid::parse_str(id) else {
        return;
    };
    let id = uuid.to_string();
    let staging = staging_path(data_root);
    let _ = fs::remove_file(staging.join(format!("{id}.json")));
    for extension in ["png", "jpg", "webp", "gif"] {
        let _ = fs::remove_file(staging.join(format!("{id}.{extension}")));
    }
}

pub fn cleanup_stale_stages(data_root: &DataRootService) {
    let staging = staging_path(data_root);
    let Ok(entries) = fs::read_dir(&staging) else {
        return;
    };
    let cutoff = SystemTime::now()
        .checked_sub(Duration::from_secs(24 * 60 * 60))
        .unwrap_or(SystemTime::UNIX_EPOCH);
    for entry in entries.flatten() {
        let path = entry.path();
        let is_stale_file = entry
            .metadata()
            .ok()
            .filter(|metadata| metadata.is_file())
            .and_then(|metadata| metadata.modified().ok())
            .is_some_and(|modified| modified < cutoff);
        if is_stale_file {
            let _ = fs::remove_file(path);
        }
    }
}

pub fn hydrate_article_paths(data_root: &DataRootService, article: &mut Article) -> AppResult<()> {
    for attachment in &mut article.attachments {
        let path = resolve_managed_attachment(data_root, &attachment.asset_path)?;
        attachment.asset_path = path.display().to_string();
    }
    Ok(())
}

fn resolve_managed_attachment(data_root: &DataRootService, relative: &str) -> AppResult<PathBuf> {
    let relative_path = Path::new(relative);
    if relative_path.is_absolute()
        || relative_path.components().any(|component| {
            matches!(
                component,
                Component::ParentDir | Component::RootDir | Component::Prefix(_)
            )
        })
    {
        return Err(attachment_reference_error());
    }
    Ok(data_root.attachments_path().join(relative_path))
}

struct LoadedStage {
    metadata: StagedImageMetadata,
    image_path: PathBuf,
}

fn load_and_verify_stage(data_root: &DataRootService, id: &str) -> AppResult<LoadedStage> {
    let id = Uuid::parse_str(id)
        .map_err(|_| attachment_reference_error())?
        .to_string();
    let staging = staging_path(data_root);
    let metadata_bytes =
        fs::read(staging.join(format!("{id}.json"))).map_err(|_| staged_image_missing())?;
    let metadata: StagedImageMetadata =
        serde_json::from_slice(&metadata_bytes).map_err(|_| staged_image_missing())?;
    if metadata.id != id || metadata.byte_size < 0 || metadata.byte_size as usize > MAX_IMAGE_BYTES
    {
        return Err(staged_image_missing());
    }
    let image_type = image_type_for_media(&metadata.media_type).ok_or_else(unsupported_image)?;
    let image_path = staging.join(format!("{id}.{}", image_type.extension));
    let bytes = fs::read(&image_path).map_err(|_| staged_image_missing())?;
    let detected = detect_image_type(&bytes).ok_or_else(unsupported_image)?;
    if detected.media_type != metadata.media_type
        || bytes.len() as i64 != metadata.byte_size
        || sha256_hex(&bytes) != metadata.sha256
    {
        return Err(staged_image_missing());
    }
    Ok(LoadedStage {
        metadata,
        image_path,
    })
}

fn staging_path(data_root: &DataRootService) -> PathBuf {
    data_root.temp_path().join(STAGING_DIRECTORY)
}

fn rollback_created_files(paths: &[PathBuf]) {
    for path in paths {
        let _ = fs::remove_file(path);
    }
}

fn remove_empty_parent(path: &Path, attachments_root: &Path) {
    let Some(parent) = path.parent() else { return };
    if parent != attachments_root
        && parent.starts_with(attachments_root)
        && parent
            .read_dir()
            .is_ok_and(|mut entries| entries.next().is_none())
    {
        let _ = fs::remove_dir(parent);
    }
}

#[derive(Clone, Copy)]
struct ImageType {
    media_type: &'static str,
    extension: &'static str,
}

fn detect_image_type(bytes: &[u8]) -> Option<ImageType> {
    if bytes.starts_with(&[0x89, b'P', b'N', b'G', 0x0d, 0x0a, 0x1a, 0x0a]) {
        Some(ImageType {
            media_type: "image/png",
            extension: "png",
        })
    } else if bytes.starts_with(&[0xff, 0xd8, 0xff]) {
        Some(ImageType {
            media_type: "image/jpeg",
            extension: "jpg",
        })
    } else if bytes.starts_with(b"GIF87a") || bytes.starts_with(b"GIF89a") {
        Some(ImageType {
            media_type: "image/gif",
            extension: "gif",
        })
    } else if bytes.len() >= 12 && &bytes[0..4] == b"RIFF" && &bytes[8..12] == b"WEBP" {
        Some(ImageType {
            media_type: "image/webp",
            extension: "webp",
        })
    } else {
        None
    }
}

fn image_type_for_media(media_type: &str) -> Option<ImageType> {
    match media_type {
        "image/png" => Some(ImageType {
            media_type: "image/png",
            extension: "png",
        }),
        "image/jpeg" => Some(ImageType {
            media_type: "image/jpeg",
            extension: "jpg",
        }),
        "image/webp" => Some(ImageType {
            media_type: "image/webp",
            extension: "webp",
        }),
        "image/gif" => Some(ImageType {
            media_type: "image/gif",
            extension: "gif",
        }),
        _ => None,
    }
}

fn safe_original_name(name: &str, extension: &str) -> String {
    let name = Path::new(name)
        .file_name()
        .and_then(|value| value.to_str())
        .unwrap_or("image")
        .trim();
    let stem = Path::new(name)
        .file_stem()
        .and_then(|value| value.to_str())
        .filter(|value| !value.trim().is_empty())
        .unwrap_or("image")
        .chars()
        .take(200)
        .collect::<String>();
    format!("{stem}.{extension}")
}

fn sha256_hex(bytes: &[u8]) -> String {
    Sha256::digest(bytes)
        .iter()
        .map(|byte| format!("{byte:02x}"))
        .collect()
}

fn image_too_large() -> AppError {
    AppError::new(
        "ATT-001",
        "画像のサイズが10MBを超えています。",
        "画像を圧縮または縮小してから、もう一度追加してください。",
    )
}

fn unsupported_image() -> AppError {
    AppError::new(
        "ATT-002",
        "このファイルは対応している画像形式ではありません。",
        "PNG、JPEG、WebP、GIFのいずれかを選択してください。拡張子だけを変更したファイルは使用できません。",
    )
}

fn image_read_error() -> AppError {
    AppError::new(
        "ATT-003",
        "選択した画像を読み込めませんでした。",
        "画像が移動・削除されていないことを確認して、もう一度選択してください。",
    )
}

fn image_write_error() -> AppError {
    AppError::new(
        "ATT-003",
        "画像をアプリの管理フォルダへ保存できませんでした。",
        "データ保存先の空き容量と書き込み権限を確認して、もう一度追加してください。",
    )
}

fn staged_image_missing() -> AppError {
    AppError::new(
        "ATT-005",
        "保存前の画像が見つからないか、内容が変更されています。",
        "回答内の画像を削除し、画像追加ボタンから選び直してください。",
    )
}

fn attachment_reference_error() -> AppError {
    AppError::new(
        "ATT-004",
        "FAQの画像参照が正しくありません。",
        "画像を一度削除し、画像追加ボタンから選び直してください。",
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{
        repositories::database::{ArticleRecord, Database},
        services::{data_root::DataRootService, rich_content},
    };
    use serde_json::json;

    const PNG: &[u8] = &[0x89, b'P', b'N', b'G', 0x0d, 0x0a, 0x1a, 0x0a, 0, 0, 0, 0];

    #[test]
    fn stages_verified_image_outside_repository() {
        let temp = tempfile::tempdir().unwrap();
        let root = DataRootService::initialize(temp.path().join("app-data"), &[]).unwrap();
        let staged = stage_bytes(&root, "screen.fake", PNG).unwrap();

        assert_eq!(staged.media_type, "image/png");
        assert!(staged.original_name.ends_with(".png"));
        assert!(Path::new(&staged.asset_path).starts_with(root.temp_path()));
        assert!(!root.attachments_path().read_dir().unwrap().any(|_| true));
    }

    #[test]
    fn rejects_extension_only_and_oversized_files() {
        let temp = tempfile::tempdir().unwrap();
        let root = DataRootService::initialize(temp.path().join("app-data"), &[]).unwrap();
        assert_eq!(
            stage_bytes(&root, "fake.png", b"not an image")
                .unwrap_err()
                .code,
            "ATT-002"
        );
        assert_eq!(
            stage_bytes(&root, "large.png", &vec![0_u8; MAX_IMAGE_BYTES + 1])
                .unwrap_err()
                .code,
            "ATT-001"
        );
    }

    #[test]
    fn finalizes_and_removes_only_after_commit() {
        let temp = tempfile::tempdir().unwrap();
        let root = DataRootService::initialize(temp.path().join("app-data"), &[]).unwrap();
        let staged = stage_bytes(&root, "screen.png", PNG).unwrap();
        let references = vec![AttachmentReference {
            id: staged.id.clone(),
            alt_text: "画面".into(),
        }];
        let prepared = prepare(&root, &Uuid::now_v7().to_string(), &references, &[]).unwrap();
        let final_path = root
            .attachments_path()
            .join(&prepared.records[0].relative_path);

        assert!(final_path.exists());
        rollback(&prepared);
        assert!(!final_path.exists());
        assert!(Path::new(&staged.asset_path).exists());
    }

    #[test]
    fn persists_attachment_metadata_and_deletes_unreferenced_file_after_article_save() {
        let temp = tempfile::tempdir().unwrap();
        let root = DataRootService::initialize(temp.path().join("app-data"), &[]).unwrap();
        let mut database = Database::open(&root.database_path()).unwrap();
        let category = database.create_category("設定", None).unwrap();
        let staged = stage_bytes(&root, "setting.png", PNG).unwrap();
        let article_id = Uuid::now_v7().to_string();
        let body = json!({
            "type": "doc",
            "content": [{
                "type": "image",
                "attrs": {
                    "src": format!("knowledge-attachment:{}", staged.id),
                    "alt": "設定画面",
                    "title": null,
                    "attachmentId": staged.id
                }
            }]
        });
        let validated = rich_content::validate_and_extract_with_attachments(&body).unwrap();
        let prepared = prepare(&root, &article_id, &validated.attachments, &[]).unwrap();
        let final_path = root
            .attachments_path()
            .join(&prepared.records[0].relative_path);
        let saved = database
            .save_article(ArticleRecord {
                id: &article_id,
                is_new: true,
                category_id: &category.id,
                title: "設定画面の見方",
                summary: "画像で説明します",
                body_doc: &body,
                body_plain_text: &validated.plain_text,
                status: "published",
                importance: 1,
                new_badge_until: None,
                updated_badge_until: None,
                is_hidden: false,
                attachments: &prepared.records,
            })
            .unwrap();
        commit(&root, &prepared);

        assert_eq!(saved.attachments.len(), 1);
        assert_eq!(database.backup_counts().unwrap().attachments, 1);
        assert!(final_path.exists());
        assert!(!Path::new(&staged.asset_path).exists());

        let copied_stage = stage_copy_of_attachment(&root, &saved.attachments[0]).unwrap();
        assert_ne!(copied_stage.id, saved.attachments[0].id);
        assert!(Path::new(&copied_stage.asset_path).exists());
        assert!(final_path.exists(), "複製準備で元画像を変更しない");
        discard_stage(&root, &copied_stage.id);

        let empty_body = json!({"type": "doc", "content": [{"type": "paragraph"}]});
        let prepared_removal = prepare(&root, &article_id, &[], &saved.attachments).unwrap();
        database
            .save_article(ArticleRecord {
                id: &article_id,
                is_new: false,
                category_id: &category.id,
                title: "設定画面の見方",
                summary: "画像を削除しました",
                body_doc: &empty_body,
                body_plain_text: "",
                status: "draft",
                importance: 1,
                new_badge_until: None,
                updated_badge_until: None,
                is_hidden: false,
                attachments: &prepared_removal.records,
            })
            .unwrap();
        assert!(final_path.exists(), "DB保存前は既存画像を保持する");
        commit(&root, &prepared_removal);
        assert!(!final_path.exists());
        assert_eq!(database.backup_counts().unwrap().attachments, 0);
    }
}
