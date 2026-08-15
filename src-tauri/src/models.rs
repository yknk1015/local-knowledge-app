use serde::{Deserialize, Serialize};
use serde_json::Value;

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Category {
    pub id: String,
    pub parent_id: Option<String>,
    pub name: String,
    pub description: String,
    pub depth: i64,
    pub sort_order: i64,
    pub article_count: i64,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CreateCategoryInput {
    pub name: String,
    #[serde(default)]
    pub description: String,
    pub parent_id: Option<String>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct UpdateCategoryInput {
    pub id: String,
    pub name: String,
    #[serde(default)]
    pub description: String,
    pub parent_id: Option<String>,
}

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct CodexCategoryCandidate {
    pub category_id: String,
    pub category_path: String,
    pub reason: String,
}

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct CodexNewCategoryProposal {
    pub parent_category_id: Option<String>,
    pub parent_category_path: Option<String>,
    pub name: String,
    #[serde(default)]
    pub description: String,
    pub reason: String,
}

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct CodexFaqDraft {
    pub title: String,
    #[serde(default)]
    pub summary: String,
    pub body_doc: Value,
    #[serde(default = "default_importance")]
    pub importance: i64,
}

fn default_importance() -> i64 {
    1
}

#[derive(Debug, Clone, Copy, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "lowercase")]
pub enum CodexProposalKind {
    Create,
    Revise,
    Merge,
}

impl CodexProposalKind {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Create => "create",
            Self::Revise => "revise",
            Self::Merge => "merge",
        }
    }
}

fn default_codex_proposal_kind() -> CodexProposalKind {
    CodexProposalKind::Create
}

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct CodexSourceArticle {
    pub article_id: String,
    pub source_updated_at: String,
}

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct CodexFaqProposal {
    pub format_version: u32,
    pub request_id: String,
    #[serde(default)]
    pub series_id: Option<String>,
    pub created_at: String,
    #[serde(default = "default_codex_proposal_kind")]
    pub proposal_kind: CodexProposalKind,
    #[serde(default)]
    pub source_articles: Vec<CodexSourceArticle>,
    pub faq: CodexFaqDraft,
    #[serde(default)]
    pub existing_category_candidates: Vec<CodexCategoryCandidate>,
    pub new_category_proposal: Option<CodexNewCategoryProposal>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RejectedCodexProposal {
    pub file_name: String,
    pub message: String,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CodexProposalInbox {
    pub proposals: Vec<CodexFaqProposal>,
    pub history: Vec<CodexProposalHistoryItem>,
    pub rejected: Vec<RejectedCodexProposal>,
    pub inbox_path: String,
    pub category_catalog_path: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CodexProposalHistoryItem {
    pub history_id: i64,
    pub proposal: CodexFaqProposal,
    pub status: String,
    pub received_at: String,
    pub decided_at: Option<String>,
    pub accepted_article_id: Option<String>,
    pub can_reopen: bool,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AcceptCodexProposalInput {
    pub request_id: String,
    pub category_id: Option<String>,
    pub create_proposed_category: bool,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AcceptCodexProposalResult {
    pub article: Article,
    pub created_category: Option<Category>,
}

#[derive(Debug, Clone, Copy, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "lowercase")]
pub enum CodexDelegationKind {
    Revise,
    Merge,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CreateCodexDelegationInput {
    pub kind: CodexDelegationKind,
    pub article_ids: Vec<String>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CodexDelegationResult {
    pub delegation_id: String,
    pub prompt: String,
    pub file_path: String,
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

#[derive(Debug, Clone, Copy, Default, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "lowercase")]
pub enum ColorTheme {
    #[default]
    Green,
    Blue,
}

fn default_show_top_category_in_title() -> bool {
    true
}

#[derive(Debug, Clone, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct AppSettings {
    pub color_theme: ColorTheme,
    #[serde(default = "default_show_top_category_in_title")]
    pub show_top_category_in_title: bool,
}

impl Default for AppSettings {
    fn default() -> Self {
        Self {
            color_theme: ColorTheme::Green,
            show_top_category_in_title: true,
        }
    }
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
