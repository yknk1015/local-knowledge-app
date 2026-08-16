use std::{
    collections::{HashMap, HashSet},
    fs,
    path::{Path, PathBuf},
};

use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use uuid::Uuid;

use crate::{
    errors::{AppError, AppResult},
    models::{
        Article, Category, CodexDelegationKind, CodexDelegationResult, CodexFaqProposal,
        CodexProposalKind, RejectedCodexProposal,
    },
    services::{data_root::DataRootService, rich_content},
};

const PROPOSAL_SUFFIX: &str = ".knowledge-proposal.json";
const DELEGATION_SUFFIX: &str = ".knowledge-delegation.json";
const MAX_PROPOSAL_BYTES: u64 = 1024 * 1024;
const MAX_DELEGATION_BYTES: usize = 5 * 1024 * 1024;

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct CategoryCatalog<'a> {
    format_version: u32,
    generated_at: String,
    categories: Vec<CategoryCatalogItem<'a>>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct CategoryCatalogItem<'a> {
    id: &'a str,
    parent_id: Option<&'a str>,
    name: &'a str,
    description: &'a str,
    depth: i64,
    path: String,
}

#[derive(Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct CodexDelegation {
    format_version: u32,
    delegation_id: String,
    created_at: String,
    kind: CodexDelegationKind,
    articles: Vec<CodexDelegationArticle>,
}

#[derive(Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct CodexDelegationArticle {
    article_id: String,
    source_updated_at: String,
    category_id: String,
    category_path: String,
    title: String,
    summary: String,
    body_doc: Value,
    status: String,
    importance: i64,
    attachments: Vec<CodexDelegationAttachment>,
}

#[derive(Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct CodexDelegationAttachment {
    attachment_id: String,
    original_name: String,
    alt_text: String,
    media_type: String,
}

pub fn category_catalog_path(data_root: &DataRootService) -> PathBuf {
    data_root.codex_bridge_path().join("categories.json")
}

pub fn write_category_catalog(
    data_root: &DataRootService,
    categories: &[Category],
) -> AppResult<()> {
    let by_id = categories
        .iter()
        .map(|category| (category.id.as_str(), category))
        .collect::<HashMap<_, _>>();
    let items = categories
        .iter()
        .map(|category| CategoryCatalogItem {
            id: &category.id,
            parent_id: category.parent_id.as_deref(),
            name: &category.name,
            description: &category.description,
            depth: category.depth,
            path: category_path(category, &by_id),
        })
        .collect();
    let catalog = CategoryCatalog {
        format_version: 1,
        generated_at: Utc::now().to_rfc3339(),
        categories: items,
    };
    let bytes = serde_json::to_vec_pretty(&catalog).map_err(|_| catalog_error())?;
    let target = category_catalog_path(data_root);
    let partial = target.with_extension("json.partial");
    fs::write(&partial, bytes).map_err(|_| catalog_error())?;
    if target.exists() {
        fs::remove_file(&target).map_err(|_| catalog_error())?;
    }
    fs::rename(&partial, &target).map_err(|_| catalog_error())?;
    Ok(())
}

pub fn write_delegation(
    data_root: &DataRootService,
    kind: CodexDelegationKind,
    articles: &[Article],
    categories: &[Category],
) -> AppResult<CodexDelegationResult> {
    let delegation_id = Uuid::now_v7().to_string();
    let by_id = categories
        .iter()
        .map(|category| (category.id.as_str(), category))
        .collect::<HashMap<_, _>>();
    let delegated_articles = articles
        .iter()
        .map(|article| {
            let category = by_id.get(article.category_id.as_str()).ok_or_else(|| {
                AppError::new(
                    "CDX-020",
                    "Codexへ委譲するFAQの分類を確認できませんでした。",
                    "FAQと分類を更新してから、もう一度委譲してください。",
                )
            })?;
            Ok(CodexDelegationArticle {
                article_id: article.id.clone(),
                source_updated_at: article.updated_at.clone(),
                category_id: article.category_id.clone(),
                category_path: category_path(category, &by_id),
                title: article.title.clone(),
                summary: article.summary.clone(),
                body_doc: article.body_doc.clone(),
                status: article.status.clone(),
                importance: article.importance,
                attachments: article
                    .attachments
                    .iter()
                    .map(|attachment| CodexDelegationAttachment {
                        attachment_id: attachment.id.clone(),
                        original_name: attachment.original_name.clone(),
                        alt_text: attachment.alt_text.clone(),
                        media_type: attachment.media_type.clone(),
                    })
                    .collect(),
            })
        })
        .collect::<AppResult<Vec<_>>>()?;
    let delegation = CodexDelegation {
        format_version: 1,
        delegation_id: delegation_id.clone(),
        created_at: Utc::now().to_rfc3339(),
        kind,
        articles: delegated_articles,
    };
    let bytes = serde_json::to_vec_pretty(&delegation).map_err(|_| delegation_write_error())?;
    if bytes.len() > MAX_DELEGATION_BYTES {
        return Err(AppError::new(
            "CDX-023",
            "選択したFAQの委譲内容が5MBの上限を超えています。",
            "統合対象を減らすか、長いFAQを分けて委譲してください。",
        ));
    }
    let target = data_root
        .codex_delegations_path()
        .join(format!("{delegation_id}{DELEGATION_SUFFIX}"));
    let partial = target.with_extension(format!("json.{}.partial", Uuid::now_v7().as_simple()));
    fs::write(&partial, bytes).map_err(|_| delegation_write_error())?;
    fs::rename(&partial, &target).map_err(|_| delegation_write_error())?;
    let action = match kind {
        CodexDelegationKind::Revise => "推敲・修正",
        CodexDelegationKind::Merge => "統合",
    };
    Ok(CodexDelegationResult {
        delegation_id: delegation_id.clone(),
        prompt: format!(
            "KnowledgeAppの委譲番号 {delegation_id} のFAQを{action}し、確認待ち提案へ送ってください。"
        ),
        file_path: target.display().to_string(),
    })
}

pub fn validate_delegated_sources(
    data_root: &DataRootService,
    proposal: &CodexFaqProposal,
) -> AppResult<()> {
    if proposal.proposal_kind == CodexProposalKind::Create {
        return Ok(());
    }
    let delegation_id = proposal
        .series_id
        .as_deref()
        .ok_or_else(|| invalid_proposal("既存FAQの提案にKnowledgeAppの委譲番号がありません。"))?;
    validate_request_id(delegation_id)?;
    let path = data_root
        .codex_delegations_path()
        .join(format!("{delegation_id}{DELEGATION_SUFFIX}"));
    let metadata = fs::symlink_metadata(&path).map_err(|_| {
        AppError::new(
            "CDX-022",
            "Codex提案に対応する委譲情報が見つかりません。",
            "KnowledgeAppから新しい委譲番号を作成し、Codexへ再依頼してください。",
        )
    })?;
    if !metadata.file_type().is_file()
        || metadata.file_type().is_symlink()
        || metadata.len() > MAX_DELEGATION_BYTES as u64
    {
        return Err(invalid_proposal(
            "Codex委譲ファイルを安全に読み取れません。",
        ));
    }
    let delegation: CodexDelegation =
        serde_json::from_slice(&fs::read(path).map_err(|_| delegation_write_error())?)
            .map_err(|_| invalid_proposal("Codex委譲ファイルの形式が正しくありません。"))?;
    if delegation.format_version != 1 || delegation.delegation_id != delegation_id {
        return Err(invalid_proposal("Codex委譲番号が一致しません。"));
    }
    let expected_kind = match proposal.proposal_kind {
        CodexProposalKind::Revise => CodexDelegationKind::Revise,
        CodexProposalKind::Merge => CodexDelegationKind::Merge,
        CodexProposalKind::Create => unreachable!(),
    };
    if delegation.kind != expected_kind {
        return Err(invalid_proposal(
            "Codex委譲の種類と提案の種類が一致しません。",
        ));
    }
    let delegated = delegation
        .articles
        .iter()
        .map(|article| {
            (
                article.article_id.as_str(),
                article.source_updated_at.as_str(),
            )
        })
        .collect::<HashSet<_>>();
    let proposed = proposal
        .source_articles
        .iter()
        .map(|article| {
            (
                article.article_id.as_str(),
                article.source_updated_at.as_str(),
            )
        })
        .collect::<HashSet<_>>();
    if delegated != proposed {
        return Err(invalid_proposal(
            "Codex提案の元FAQが、KnowledgeAppで委譲した対象と一致しません。",
        ));
    }
    Ok(())
}

fn category_path(category: &Category, by_id: &HashMap<&str, &Category>) -> String {
    let mut names = vec![category.name.as_str()];
    let mut parent_id = category.parent_id.as_deref();
    while let Some(id) = parent_id {
        let Some(parent) = by_id.get(id) else { break };
        names.push(parent.name.as_str());
        parent_id = parent.parent_id.as_deref();
    }
    names.reverse();
    names.join(" > ")
}

pub fn list_proposals(
    data_root: &DataRootService,
) -> AppResult<(Vec<CodexFaqProposal>, Vec<RejectedCodexProposal>)> {
    let mut proposals = Vec::new();
    let mut rejected = Vec::new();
    let entries = fs::read_dir(data_root.codex_inbox_path()).map_err(|_| inbox_read_error())?;

    for entry in entries {
        let Ok(entry) = entry else { continue };
        let path = entry.path();
        let Some(file_name) = path.file_name().and_then(|name| name.to_str()) else {
            continue;
        };
        if !file_name.ends_with(PROPOSAL_SUFFIX) {
            continue;
        }
        match read_and_validate(&path) {
            Ok(proposal) => proposals.push(proposal),
            Err(error) => rejected.push(RejectedCodexProposal {
                file_name: file_name.to_owned(),
                message: error.message,
            }),
        }
    }

    proposals.sort_by(|left, right| right.created_at.cmp(&left.created_at));
    rejected.sort_by(|left, right| left.file_name.cmp(&right.file_name));
    Ok((proposals, rejected))
}

pub fn read_proposal(data_root: &DataRootService, request_id: &str) -> AppResult<CodexFaqProposal> {
    validate_request_id(request_id)?;
    read_and_validate(&proposal_path(data_root, request_id))
}

pub fn discard_proposal(data_root: &DataRootService, request_id: &str) -> AppResult<()> {
    validate_request_id(request_id)?;
    let path = proposal_path(data_root, request_id);
    if !path.is_file() {
        return Err(proposal_not_found());
    }
    fs::remove_file(path).map_err(|_| {
        AppError::new(
            "CDX-008",
            "Codexの提案を破棄できませんでした。",
            "ファイルが他のアプリで使用中でないか確認し、もう一度お試しください。",
        )
    })
}

fn proposal_path(data_root: &DataRootService, request_id: &str) -> PathBuf {
    data_root
        .codex_inbox_path()
        .join(format!("{request_id}{PROPOSAL_SUFFIX}"))
}

fn read_and_validate(path: &Path) -> AppResult<CodexFaqProposal> {
    let metadata = fs::symlink_metadata(path).map_err(|_| proposal_not_found())?;
    if !metadata.file_type().is_file() || metadata.file_type().is_symlink() {
        return Err(invalid_proposal(
            "通常ファイルではないCodex提案は読み込めません。",
        ));
    }
    if metadata.len() > MAX_PROPOSAL_BYTES {
        return Err(invalid_proposal("Codex提案が1MBの上限を超えています。"));
    }
    let bytes = fs::read(path).map_err(|_| inbox_read_error())?;
    let proposal: CodexFaqProposal = serde_json::from_slice(&bytes)
        .map_err(|_| invalid_proposal("Codex提案のJSON形式が正しくありません。"))?;
    let request_id_from_name = path
        .file_name()
        .and_then(|name| name.to_str())
        .and_then(|name| name.strip_suffix(PROPOSAL_SUFFIX))
        .ok_or_else(|| invalid_proposal("Codex提案のファイル名が正しくありません。"))?;
    if proposal.request_id != request_id_from_name {
        return Err(invalid_proposal(
            "Codex提案の受付番号とファイル名が一致しません。",
        ));
    }
    validate_proposal(&proposal)?;
    Ok(proposal)
}

pub fn validate_proposal(proposal: &CodexFaqProposal) -> AppResult<()> {
    if !matches!(proposal.format_version, 1 | 2) {
        return Err(invalid_proposal(
            "このCodex提案は現在のアプリで扱えない形式です。",
        ));
    }
    validate_request_id(&proposal.request_id)?;
    if proposal.format_version == 1 {
        if proposal.series_id.is_some()
            || proposal.proposal_kind != CodexProposalKind::Create
            || !proposal.source_articles.is_empty()
        {
            return Err(invalid_proposal(
                "形式第1版のCodex提案には既存FAQの委譲情報を含められません。",
            ));
        }
    } else {
        let series_id = proposal
            .series_id
            .as_deref()
            .ok_or_else(|| invalid_proposal("形式第2版のCodex提案には依頼系列番号が必要です。"))?;
        validate_request_id(series_id)?;
    }
    DateTime::parse_from_rfc3339(&proposal.created_at)
        .map_err(|_| invalid_proposal("Codex提案の作成日時が正しくありません。"))?;

    let expected_sources = match proposal.proposal_kind {
        CodexProposalKind::Create => 0..=0,
        CodexProposalKind::Revise => 1..=1,
        CodexProposalKind::Merge => 2..=10,
    };
    if !expected_sources.contains(&proposal.source_articles.len()) {
        return Err(invalid_proposal(match proposal.proposal_kind {
            CodexProposalKind::Create => "新規FAQ提案に元FAQを指定できません。",
            CodexProposalKind::Revise => "修正提案には元FAQを1件指定してください。",
            CodexProposalKind::Merge => "統合提案には元FAQを2～10件指定してください。",
        }));
    }
    let mut source_ids = HashSet::new();
    for source in &proposal.source_articles {
        validate_request_id(&source.article_id)?;
        DateTime::parse_from_rfc3339(&source.source_updated_at)
            .map_err(|_| invalid_proposal("元FAQの更新日時が正しくありません。"))?;
        if !source_ids.insert(source.article_id.as_str()) {
            return Err(invalid_proposal("同じ元FAQが重複しています。"));
        }
    }

    let title = proposal.faq.title.trim();
    if title.is_empty() || title.chars().count() > 200 {
        return Err(invalid_proposal(
            "Codex提案のタイトルは1～200文字である必要があります。",
        ));
    }
    if proposal.faq.summary.chars().count() > 500 {
        return Err(invalid_proposal(
            "Codex提案の概要は500文字以内である必要があります。",
        ));
    }
    if !(1..=3).contains(&proposal.faq.importance) {
        return Err(invalid_proposal(
            "Codex提案の重要度は1～3である必要があります。",
        ));
    }
    if proposal.proposal_kind != CodexProposalKind::Revise
        && contains_node_type(&proposal.faq.body_doc, "image")
    {
        return Err(invalid_proposal(
            "新規・統合提案から画像は取り込めません。下書き取込後に追加してください。",
        ));
    }
    let content = rich_content::validate_and_extract_with_attachments(&proposal.faq.body_doc)?;
    if content.plain_text.trim().is_empty() {
        return Err(invalid_proposal("Codex提案の回答が空です。"));
    }

    if proposal.existing_category_candidates.len() > 3 {
        return Err(invalid_proposal(
            "既存分類の候補は3件以内である必要があります。",
        ));
    }
    let mut ids = HashSet::new();
    for candidate in &proposal.existing_category_candidates {
        Uuid::parse_str(&candidate.category_id)
            .map_err(|_| invalid_proposal("既存分類候補のIDが正しくありません。"))?;
        if !ids.insert(candidate.category_id.as_str()) {
            return Err(invalid_proposal("同じ既存分類候補が重複しています。"));
        }
        validate_limited_text(&candidate.category_path, 1000, "分類候補の階層")?;
        validate_limited_text(&candidate.reason, 500, "分類候補の理由")?;
    }

    if let Some(category) = &proposal.new_category_proposal {
        if let Some(parent_id) = category.parent_category_id.as_deref() {
            Uuid::parse_str(parent_id)
                .map_err(|_| invalid_proposal("新規分類案の親分類IDが正しくありません。"))?;
        }
        if category
            .parent_category_path
            .as_ref()
            .is_some_and(|value| value.chars().count() > 1000)
        {
            return Err(invalid_proposal("新規分類案の親分類階層が長すぎます。"));
        }
        validate_limited_text(&category.name, 100, "新規分類案の名前")?;
        if category.description.chars().count() > 500 {
            return Err(invalid_proposal(
                "新規分類案の説明は500文字以内である必要があります。",
            ));
        }
        validate_limited_text(&category.reason, 500, "新規分類案の理由")?;
    }

    if proposal.proposal_kind == CodexProposalKind::Revise {
        if !proposal.existing_category_candidates.is_empty()
            || proposal.new_category_proposal.is_some()
        {
            return Err(invalid_proposal(
                "既存FAQの修正提案では所属分類を変更できません。",
            ));
        }
    } else if proposal.existing_category_candidates.is_empty()
        && proposal.new_category_proposal.is_none()
    {
        return Err(invalid_proposal("既存分類候補または新規分類案が必要です。"));
    }
    Ok(())
}

fn validate_limited_text(value: &str, maximum: usize, label: &str) -> AppResult<()> {
    if value.trim().is_empty() || value.chars().count() > maximum {
        return Err(invalid_proposal(&format!(
            "{label}は1～{maximum}文字である必要があります。"
        )));
    }
    Ok(())
}

fn contains_node_type(value: &Value, node_type: &str) -> bool {
    match value {
        Value::Object(object) => {
            object.get("type").and_then(Value::as_str) == Some(node_type)
                || object
                    .values()
                    .any(|child| contains_node_type(child, node_type))
        }
        Value::Array(values) => values
            .iter()
            .any(|child| contains_node_type(child, node_type)),
        _ => false,
    }
}

fn validate_request_id(request_id: &str) -> AppResult<()> {
    Uuid::parse_str(request_id)
        .map(|_| ())
        .map_err(|_| invalid_proposal("Codex提案の受付番号が正しくありません。"))
}

fn invalid_proposal(message: &str) -> AppError {
    AppError::new(
        "CDX-002",
        message,
        "Codexへもう一度下書き作成を依頼してください。データベースは変更されていません。",
    )
}

fn proposal_not_found() -> AppError {
    AppError::new(
        "CDX-003",
        "指定したCodex提案が見つかりません。",
        "提案一覧を更新し、もう一度選択してください。",
    )
}

fn inbox_read_error() -> AppError {
    AppError::new(
        "CDX-001",
        "Codexからの提案を確認できませんでした。",
        "提案フォルダを確認してから、もう一度お試しください。",
    )
}

fn catalog_error() -> AppError {
    AppError::new(
        "CDX-010",
        "Codex用の分類一覧を更新できませんでした。",
        "データ保存先の空き容量とアクセス権を確認してください。",
    )
}

fn delegation_write_error() -> AppError {
    AppError::new(
        "CDX-020",
        "Codexへの委譲ファイルを作成できませんでした。",
        "利用者データフォルダの空き容量とアクセス権を確認してください。",
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn proposal() -> CodexFaqProposal {
        CodexFaqProposal {
            format_version: 2,
            request_id: Uuid::now_v7().to_string(),
            series_id: Some(Uuid::now_v7().to_string()),
            created_at: Utc::now().to_rfc3339(),
            proposal_kind: CodexProposalKind::Create,
            source_articles: vec![],
            faq: crate::models::CodexFaqDraft {
                title: "Windowsを再起動するには？".into(),
                summary: "通常の再起動手順です。".into(),
                body_doc: json!({"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"スタートメニューから再起動します。"}]}]}),
                importance: 1,
            },
            existing_category_candidates: vec![],
            new_category_proposal: Some(crate::models::CodexNewCategoryProposal {
                parent_category_id: None,
                parent_category_path: None,
                name: "Windows".into(),
                description: "Windowsの基本操作".into(),
                reason: "OS操作に関するFAQだからです。".into(),
            }),
        }
    }

    #[test]
    fn rejects_images_and_more_than_three_candidates() {
        let mut with_image = proposal();
        with_image.faq.body_doc = json!({"type":"doc","content":[{"type":"image","attrs":{"id":"00000000-0000-0000-0000-000000000000","src":"knowledge-attachment:00000000-0000-0000-0000-000000000000"}}]});
        assert_eq!(validate_proposal(&with_image).unwrap_err().code, "CDX-002");

        let mut too_many = proposal();
        too_many.new_category_proposal = None;
        too_many.existing_category_candidates = (0..4)
            .map(|index| crate::models::CodexCategoryCandidate {
                category_id: Uuid::now_v7().to_string(),
                category_path: format!("分類{index}"),
                reason: "候補".into(),
            })
            .collect();
        assert_eq!(validate_proposal(&too_many).unwrap_err().code, "CDX-002");
    }

    #[test]
    fn catalog_contains_only_category_metadata() {
        let directory = tempfile::tempdir().unwrap();
        let root = DataRootService::initialize(directory.path().join("app"), &[]).unwrap();
        let categories = vec![Category {
            id: Uuid::now_v7().to_string(),
            parent_id: None,
            name: "PC".into(),
            description: "PC全般".into(),
            depth: 1,
            sort_order: 0,
            article_count: 99,
        }];
        write_category_catalog(&root, &categories).unwrap();
        let catalog = fs::read_to_string(category_catalog_path(&root)).unwrap();
        let parsed: Value = serde_json::from_str(&catalog).unwrap();
        let item = parsed["categories"][0].as_object().unwrap();
        assert_eq!(item["description"], "PC全般");
        assert_eq!(item.len(), 6);
        for key in ["id", "parentId", "name", "description", "depth", "path"] {
            assert!(item.contains_key(key));
        }
        assert!(!item.contains_key("articleCount"));
    }

    #[test]
    fn inbox_rejects_unknown_fields_without_hiding_other_files() {
        let directory = tempfile::tempdir().unwrap();
        let root = DataRootService::initialize(directory.path().join("app"), &[]).unwrap();
        let valid = proposal();
        let valid_path = proposal_path(&root, &valid.request_id);
        fs::write(&valid_path, serde_json::to_vec(&valid).unwrap()).unwrap();

        let invalid = proposal();
        let mut invalid_json = serde_json::to_value(&invalid).unwrap();
        invalid_json
            .as_object_mut()
            .unwrap()
            .insert("unexpected".into(), json!(true));
        fs::write(
            proposal_path(&root, &invalid.request_id),
            serde_json::to_vec(&invalid_json).unwrap(),
        )
        .unwrap();

        let (proposals, rejected) = list_proposals(&root).unwrap();
        assert_eq!(proposals.len(), 1);
        assert_eq!(rejected.len(), 1);
        assert!(rejected[0].message.contains("JSON形式"));
    }

    #[test]
    fn delegated_revision_must_match_the_explicit_delegation_file() {
        let directory = tempfile::tempdir().unwrap();
        let root = DataRootService::initialize(directory.path().join("app"), &[]).unwrap();
        let category_id = Uuid::now_v7().to_string();
        let article_id = Uuid::now_v7().to_string();
        let updated_at = Utc::now().to_rfc3339();
        let article = Article {
            id: article_id.clone(),
            category_id: category_id.clone(),
            category_name: "PC".into(),
            title: "元FAQ".into(),
            summary: "概要".into(),
            body_doc: json!({"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"回答"}]}]}),
            body_plain_text: "回答".into(),
            status: "published".into(),
            importance: 1,
            new_badge_until: None,
            updated_badge_until: None,
            is_hidden: false,
            created_at: updated_at.clone(),
            updated_at: updated_at.clone(),
            created_by_user_id: "user-1".into(),
            created_by_display_name: "利用者".into(),
            updated_by_user_id: "user-1".into(),
            updated_by_display_name: "利用者".into(),
            deleted_at: None,
            merge_info: None,
            attachments: vec![crate::models::ArticleAttachment {
                id: Uuid::now_v7().to_string(),
                original_name: "screen.png".into(),
                media_type: "image/png".into(),
                byte_size: 123,
                sha256: "secret-hash".into(),
                alt_text: "画面".into(),
                asset_path: "C:\\secret\\screen.png".into(),
                created_at: updated_at.clone(),
            }],
            symptoms: Vec::new(),
            causes: Vec::new(),
            targets: Vec::new(),
            error_codes: Vec::new(),
            procedures: Vec::new(),
            cautions: Vec::new(),
            tags: Vec::new(),
            search_terms: Vec::new(),
            related_articles: Vec::new(),
        };
        let category = Category {
            id: category_id,
            parent_id: None,
            name: "PC".into(),
            description: String::new(),
            depth: 1,
            sort_order: 0,
            article_count: 1,
        };
        let delegation =
            write_delegation(&root, CodexDelegationKind::Revise, &[article], &[category]).unwrap();
        let delegation_json = fs::read_to_string(&delegation.file_path).unwrap();
        assert!(delegation_json.contains("screen.png"));
        assert!(!delegation_json.contains("C:\\\\secret"));
        assert!(!delegation_json.contains("secret-hash"));
        assert!(!delegation_json.contains("bodyPlainText"));
        assert!(!delegation_json.contains("password"));
        assert!(!delegation_json.contains("createdByUser"));
        assert!(!delegation_json.contains("updatedByUser"));
        let proposal = CodexFaqProposal {
            format_version: 2,
            request_id: Uuid::now_v7().to_string(),
            series_id: Some(delegation.delegation_id),
            created_at: Utc::now().to_rfc3339(),
            proposal_kind: CodexProposalKind::Revise,
            source_articles: vec![crate::models::CodexSourceArticle {
                article_id,
                source_updated_at: updated_at,
            }],
            faq: crate::models::CodexFaqDraft {
                title: "修正案".into(),
                summary: String::new(),
                body_doc: json!({"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"修正回答"}]}]}),
                importance: 1,
            },
            existing_category_candidates: vec![],
            new_category_proposal: None,
        };
        validate_delegated_sources(&root, &proposal).unwrap();

        let mut swapped = proposal;
        swapped.source_articles[0].article_id = Uuid::now_v7().to_string();
        assert_eq!(
            validate_delegated_sources(&root, &swapped)
                .unwrap_err()
                .code,
            "CDX-002"
        );
    }
}
