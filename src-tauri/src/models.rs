use serde::{Deserialize, Serialize};
use serde_json::Value;

#[derive(Debug, Clone, Serialize)]
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
    pub created_at: String,
    pub updated_at: String,
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
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SearchArticlesInput {
    pub query: String,
    pub category_id: Option<String>,
    pub include_drafts: bool,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SystemInfo {
    pub app_version: String,
    pub data_root: String,
    pub database_path: String,
}
