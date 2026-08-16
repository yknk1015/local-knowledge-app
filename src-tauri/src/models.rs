use serde::{Deserialize, Serialize};
use serde_json::Value;

#[derive(Debug, Clone, Copy, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "lowercase")]
pub enum UserRole {
    Admin,
    User,
}

impl UserRole {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Admin => "admin",
            Self::User => "user",
        }
    }
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AuthenticatedUser {
    pub id: String,
    pub login_id: String,
    pub display_name: String,
    pub role: UserRole,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct UserSummary {
    pub id: String,
    pub login_id: String,
    pub display_name: String,
    pub role: UserRole,
    pub is_active: bool,
    pub created_at: String,
    pub updated_at: String,
    pub last_login_at: Option<String>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LoginInput {
    pub login_id: String,
    #[serde(default)]
    pub password: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CreateUserInput {
    pub login_id: String,
    pub display_name: String,
    #[serde(default)]
    pub password: String,
    pub role: UserRole,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SetUserActiveInput {
    pub id: String,
    pub is_active: bool,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ResetUserPasswordInput {
    pub id: String,
    #[serde(default)]
    pub password: String,
}

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

#[derive(Debug, Clone, Copy, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum CategoryMoveDirection {
    Up,
    Down,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ReorderCategoryInput {
    pub id: String,
    pub direction: CategoryMoveDirection,
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

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CodexMergeSourcePreview {
    pub article_id: String,
    pub title: String,
    pub status: Option<String>,
    pub source_updated_at: String,
    pub current_updated_at: Option<String>,
    pub deleted_at: Option<String>,
    pub is_current: bool,
    pub is_merged: bool,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CodexMergePublicationContext {
    pub target_article_id: String,
    pub source_articles: Vec<CodexMergeSourcePreview>,
    pub can_mark_merged: bool,
    pub all_sources_merged: bool,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct MarkCodexMergeSourcesResult {
    pub target_article_id: String,
    pub marked_count: usize,
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
    pub tags: Vec<String>,
    pub match_reasons: Vec<String>,
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
    pub created_by_user_id: String,
    pub created_by_display_name: String,
    pub updated_by_user_id: String,
    pub updated_by_display_name: String,
    pub deleted_at: Option<String>,
    pub merge_info: Option<ArticleMergeInfo>,
    pub attachments: Vec<ArticleAttachment>,
    pub symptoms: Vec<String>,
    pub causes: Vec<String>,
    pub targets: Vec<String>,
    pub error_codes: Vec<String>,
    pub procedures: Vec<String>,
    pub cautions: Vec<String>,
    pub tags: Vec<String>,
    pub search_terms: Vec<String>,
    pub related_articles: Vec<RelatedArticleSummary>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RelatedArticleSummary {
    pub id: String,
    pub title: String,
    pub status: String,
    pub deleted_at: Option<String>,
    pub is_merged: bool,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RelatedArticleCandidate {
    pub id: String,
    pub title: String,
    pub status: String,
    pub is_related: bool,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ArticleMergeInfo {
    pub target_article_id: String,
    pub target_article_title: String,
    pub merged_at: String,
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
    #[serde(default)]
    pub symptoms: Vec<String>,
    #[serde(default)]
    pub causes: Vec<String>,
    #[serde(default)]
    pub targets: Vec<String>,
    #[serde(default)]
    pub error_codes: Vec<String>,
    #[serde(default)]
    pub procedures: Vec<String>,
    #[serde(default)]
    pub cautions: Vec<String>,
    #[serde(default)]
    pub tags: Vec<String>,
    #[serde(default)]
    pub search_terms: Vec<String>,
    #[serde(default)]
    pub related_article_ids: Vec<String>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SearchRelatedArticlesInput {
    pub article_id: Option<String>,
    #[serde(default)]
    pub query: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SearchArticlesInput {
    pub query: String,
    pub category_id: Option<String>,
    pub scope: SearchScope,
    pub include_drafts: bool,
    pub page: i64,
    pub sort: SearchSort,
}

#[derive(Debug, Clone, Copy, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "lowercase")]
pub enum SearchScope {
    Descendants,
    Current,
    All,
}

impl SearchScope {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Descendants => "descendants",
            Self::Current => "current",
            Self::All => "all",
        }
    }
}

#[derive(Debug, Clone, Copy, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum SearchSort {
    UpdatedDesc,
    UpdatedAsc,
    ImportanceDesc,
    ImportanceAsc,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SearchArticlePage {
    pub items: Vec<ArticleListItem>,
    pub total: i64,
    pub page: i64,
    pub page_size: i64,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RecordSearchLogInput {
    #[serde(default)]
    pub query: String,
    pub category_id: Option<String>,
    pub scope: SearchScope,
    pub result_count: i64,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RecordArticleViewInput {
    pub article_id: String,
    pub source_search_log_id: Option<String>,
}

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct SynonymGroup {
    pub id: String,
    pub display_name: String,
    pub terms: Vec<String>,
    pub updated_at: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SaveSynonymGroupInput {
    pub id: Option<String>,
    pub display_name: String,
    #[serde(default)]
    pub terms: Vec<String>,
    #[serde(default)]
    pub allow_conflicts: bool,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ListSearchLogsInput {
    #[serde(default)]
    pub query: String,
    pub start_date: Option<String>,
    pub end_date: Option<String>,
    #[serde(default)]
    pub zero_results_only: bool,
    pub page: i64,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SearchLogItem {
    pub id: String,
    pub query_text: String,
    pub normalized_query: String,
    pub scope: String,
    pub category_name: Option<String>,
    pub result_count: i64,
    pub created_at: String,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SearchLogPage {
    pub items: Vec<SearchLogItem>,
    pub total: i64,
    pub page: i64,
    pub page_size: i64,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ListViewLogsInput {
    #[serde(default)]
    pub query: String,
    pub start_date: Option<String>,
    pub end_date: Option<String>,
    pub page: i64,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ViewLogItem {
    pub id: String,
    pub article_id: String,
    pub article_title: String,
    pub source_query_text: Option<String>,
    pub viewed_at: String,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ViewLogPage {
    pub items: Vec<ViewLogItem>,
    pub total: i64,
    pub page: i64,
    pub page_size: i64,
}

#[derive(Debug, Clone, Copy, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum HistoryTarget {
    Search,
    View,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DeleteHistoryInput {
    pub target: HistoryTarget,
    pub start_date: Option<String>,
    pub end_date: Option<String>,
    #[serde(default)]
    pub delete_all: bool,
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
    pub created_by_display_name: String,
    pub updated_by_display_name: String,
    pub deleted_at: Option<String>,
    pub merge_info: Option<ArticleMergeInfo>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ManagementArticlePage {
    pub items: Vec<ManagementArticleListItem>,
    pub total: i64,
    pub page: i64,
    pub page_size: i64,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ExportFaqCsvInput {
    pub destination_path: String,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CsvExportResult {
    pub destination_path: String,
    pub exported_count: usize,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CsvImportPreviewRow {
    pub line: usize,
    pub action: String,
    pub faq_management_id: Option<String>,
    pub title: String,
    pub body_will_be_replaced: bool,
    pub stale_update_will_overwrite: bool,
    pub messages: Vec<String>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CsvImportPreview {
    pub source_path: String,
    pub file_sha256: String,
    pub total_rows: usize,
    pub create_count: usize,
    pub update_count: usize,
    pub unchanged_count: usize,
    pub body_replacement_count: usize,
    pub stale_overwrite_count: usize,
    pub error_count: usize,
    pub rows: Vec<CsvImportPreviewRow>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ImportFaqCsvInput {
    pub source_path: String,
    pub expected_file_sha256: String,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CsvImportResult {
    pub source_path: String,
    pub safety_backup_path: String,
    pub created_count: usize,
    pub updated_count: usize,
    pub unchanged_count: usize,
}

#[derive(Debug, Clone, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct JsonCategoryData {
    pub id: String,
    pub management_code: String,
    pub parent_id: Option<String>,
    pub name: String,
    #[serde(default)]
    pub description: String,
    pub sort_order: i64,
    pub created_at: String,
    pub updated_at: String,
}

#[derive(Debug, Clone, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct JsonTagData {
    pub id: String,
    pub name: String,
}

#[derive(Debug, Clone, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct JsonSynonymGroupData {
    pub id: String,
    pub display_name: String,
    pub terms: Vec<String>,
}

#[derive(Debug, Clone, Deserialize, Serialize, PartialEq)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct JsonArticleData {
    pub id: String,
    pub management_code: String,
    pub category_id: String,
    pub title: String,
    #[serde(default)]
    pub summary: String,
    pub body_doc: Value,
    pub status: String,
    pub importance: i64,
    pub new_badge_until: Option<String>,
    pub updated_badge_until: Option<String>,
    #[serde(default)]
    pub is_hidden: bool,
    pub created_at: String,
    pub updated_at: String,
    pub deleted_at: Option<String>,
    #[serde(default)]
    pub symptoms: Vec<String>,
    #[serde(default)]
    pub causes: Vec<String>,
    #[serde(default)]
    pub targets: Vec<String>,
    #[serde(default)]
    pub error_codes: Vec<String>,
    #[serde(default)]
    pub procedures: Vec<String>,
    #[serde(default)]
    pub cautions: Vec<String>,
    #[serde(default)]
    pub tag_ids: Vec<String>,
    #[serde(default)]
    pub search_terms: Vec<String>,
}

#[derive(Debug, Clone, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct JsonArticleRelationData {
    pub source_article_id: String,
    pub target_article_id: String,
    pub sort_order: i64,
}

#[derive(Debug, Clone, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct JsonMergeRelationData {
    pub source_article_id: String,
    pub target_article_id: String,
    pub source_updated_at: String,
    pub merged_at: String,
}

#[derive(Debug, Clone, Deserialize, Serialize, PartialEq)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct KnowledgeJsonDocument {
    pub format_version: u32,
    pub exported_at: String,
    pub categories: Vec<JsonCategoryData>,
    pub tags: Vec<JsonTagData>,
    pub synonym_groups: Vec<JsonSynonymGroupData>,
    pub articles: Vec<JsonArticleData>,
    pub relations: Vec<JsonArticleRelationData>,
    pub merge_relations: Vec<JsonMergeRelationData>,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct JsonEntityCounts {
    pub categories: usize,
    pub articles: usize,
    pub tags: usize,
    pub synonym_groups: usize,
    pub relations: usize,
    pub merge_relations: usize,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ExportJsonInput {
    pub destination_path: String,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct JsonExportResult {
    pub destination_path: String,
    pub counts: JsonEntityCounts,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct JsonImportPreview {
    pub source_path: String,
    pub file_sha256: String,
    pub counts: JsonEntityCounts,
    pub create_count: usize,
    pub update_count: usize,
    pub unchanged_count: usize,
    pub error_count: usize,
    pub errors: Vec<String>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ImportJsonInput {
    pub source_path: String,
    pub expected_file_sha256: String,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct JsonImportResult {
    pub source_path: String,
    pub safety_backup_path: String,
    pub created_count: usize,
    pub updated_count: usize,
    pub unchanged_count: usize,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SystemInfo {
    pub app_version: String,
    pub data_root: String,
    pub database_path: String,
    pub codex_category_catalog_path: String,
    pub codex_inbox_path: String,
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

fn default_show_mascot() -> bool {
    true
}

#[derive(Debug, Clone, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct AppSettings {
    pub color_theme: ColorTheme,
    #[serde(default = "default_show_top_category_in_title")]
    pub show_top_category_in_title: bool,
    #[serde(default = "default_show_mascot")]
    pub show_mascot: bool,
}

impl Default for AppSettings {
    fn default() -> Self {
        Self {
            color_theme: ColorTheme::Green,
            show_top_category_in_title: true,
            show_mascot: true,
        }
    }
}

#[derive(Debug, Clone, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct PasswordPolicySettings {
    pub allow_empty_passwords: bool,
}

impl Default for PasswordPolicySettings {
    fn default() -> Self {
        Self {
            allow_empty_passwords: true,
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
