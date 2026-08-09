use serde::{Deserialize, Serialize};
use serde_json::Value;

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Category {
    pub id: String,
    pub parent_id: Option<String>,
    pub name: String,
    pub depth: i64,
    pub sort_order: i64,
    pub article_count: i64,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CreateCategoryInput {
    pub name: String,
    pub parent_id: Option<String>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct UpdateCategoryInput {
    pub id: String,
    pub name: String,
    pub parent_id: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ArticleListItem {
    pub id: String,
    pub category_id: String,
    pub category_name: String,
    pub title: String,
    pub summary: String,
    pub status: String,
    pub importance: i64,
    pub new_badge_until: Option<String>,
    pub updated_badge_until: Option<String>,
    pub is_hidden: bool,
    pub updated_at: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Article {
    pub id: String,
    pub category_id: String,
    pub category_name: String,
    pub title: String,
    pub summary: String,
    pub body_doc: Value,
    pub body_plain_text: String,
    pub status: String,
    pub importance: i64,
    pub new_badge_until: Option<String>,
    pub updated_badge_until: Option<String>,
    pub is_hidden: bool,
    pub created_at: String,
    pub updated_at: String,
    pub deleted_at: Option<String>,
    pub attachments: Vec<ArticleAttachment>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ArticleAttachment {
    pub id: String,
    pub original_name: String,
    pub media_type: String,
    pub byte_size: i64,
    pub sha256: String,
    pub alt_text: String,
    pub asset_path: String,
    pub created_at: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct StagedArticleImage {
    pub id: String,
    pub original_name: String,
    pub media_type: String,
    pub byte_size: i64,
    pub sha256: String,
    pub alt_text: String,
    pub asset_path: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct StageArticleImageBytesInput {
    pub original_name: String,
    pub bytes: Vec<u8>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SaveArticleInput {
    pub id: Option<String>,
    pub category_id: String,
    pub title: String,
    pub summary: String,
    pub body_doc: Value,
    pub status: String,
    pub importance: i64,
    pub new_badge_until: Option<String>,
    pub updated_badge_until: Option<String>,
    pub is_hidden: bool,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SearchArticlesInput {
    pub query: String,
    pub category_id: Option<String>,
    pub include_drafts: bool,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ManagementArticlesInput {
    pub query: String,
    pub category_id: Option<String>,
    pub status: Option<String>,
    pub deleted: bool,
    pub page: i64,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ManagementArticleListItem {
    pub id: String,
    pub category_id: String,
    pub category_name: String,
    pub title: String,
    pub summary: String,
    pub status: String,
    pub importance: i64,
    pub new_badge_until: Option<String>,
    pub updated_badge_until: Option<String>,
    pub is_hidden: bool,
    pub updated_at: String,
    pub deleted_at: Option<String>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ManagementArticlePage {
    pub items: Vec<ManagementArticleListItem>,
    pub total: i64,
    pub page: i64,
    pub page_size: i64,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SystemInfo {
    pub app_version: String,
    pub data_root: String,
    pub database_path: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CreateFullBackupInput {
    pub destination_path: String,
    pub display_name: String,
    pub overwrite: bool,
}

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BackupCounts {
    pub articles: i64,
    pub categories: i64,
    pub attachments: i64,
    pub manuals: i64,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BackupOverview {
    pub estimated_bytes: u64,
    pub counts: BackupCounts,
    pub default_directory: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BackupPreview {
    pub source_path: String,
    pub display_name: String,
    pub created_at: String,
    pub app_version: String,
    pub schema_version: i64,
    pub backup_format_version: u32,
    pub total_bytes: u64,
    pub counts: BackupCounts,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BackupResult {
    pub destination_path: String,
    pub display_name: String,
    pub created_at: String,
    pub total_bytes: u64,
    pub counts: BackupCounts,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RestoreResult {
    pub source_path: String,
    pub safety_backup_path: String,
    pub restored_at: String,
    pub counts: BackupCounts,
}
