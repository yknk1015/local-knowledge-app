use std::collections::BTreeMap;

use serde_json::{Map, Value};
use url::Url;
use uuid::Uuid;

use crate::errors::{AppError, AppResult};

const ALLOWED_NODES: &[&str] = &[
    "doc",
    "paragraph",
    "text",
    "hardBreak",
    "heading",
    "bulletList",
    "orderedList",
    "listItem",
    "table",
    "tableRow",
    "tableCell",
    "tableHeader",
    "image",
];

const ALLOWED_MARKS: &[&str] = &["bold", "italic", "link"];

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AttachmentReference {
    pub id: String,
    pub alt_text: String,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ValidatedRichContent {
    pub plain_text: String,
    pub attachments: Vec<AttachmentReference>,
}

#[cfg(test)]
pub fn validate_and_extract(document: &Value) -> AppResult<String> {
    Ok(validate_and_extract_with_attachments(document)?.plain_text)
}

pub fn validate_and_extract_with_attachments(document: &Value) -> AppResult<ValidatedRichContent> {
    if document.get("type").and_then(Value::as_str) != Some("doc") {
        return Err(invalid_document());
    }

    let mut plain_text = String::new();
    let mut attachments = BTreeMap::new();
    validate_node(document, &mut plain_text, &mut attachments)?;
    Ok(ValidatedRichContent {
        plain_text: plain_text.split_whitespace().collect::<Vec<_>>().join(" "),
        attachments: attachments
            .into_iter()
            .map(|(id, alt_text)| AttachmentReference { id, alt_text })
            .collect(),
    })
}

fn validate_node(
    node: &Value,
    plain_text: &mut String,
    attachments: &mut BTreeMap<String, String>,
) -> AppResult<()> {
    let object = node.as_object().ok_or_else(invalid_document)?;
    let node_type = object
        .get("type")
        .and_then(Value::as_str)
        .ok_or_else(invalid_document)?;

    if !ALLOWED_NODES.contains(&node_type) {
        return Err(invalid_document());
    }

    if node_type == "image" {
        let reference = validate_image(object.get("attrs"))?;
        if !attachments.contains_key(&reference.id) {
            if !reference.alt_text.trim().is_empty() {
                plain_text.push_str(&reference.alt_text);
                plain_text.push(' ');
            }
            attachments.insert(reference.id, reference.alt_text);
        }
    } else {
        validate_attributes(node_type, object.get("attrs"))?;
    }
    validate_marks(object.get("marks"))?;

    if node_type == "text" {
        let text = object
            .get("text")
            .and_then(Value::as_str)
            .ok_or_else(invalid_document)?;
        plain_text.push_str(text);
    }

    if let Some(content) = object.get("content") {
        let children = content.as_array().ok_or_else(invalid_document)?;
        for child in children {
            validate_node(child, plain_text, attachments)?;
            if matches!(
                child.get("type").and_then(Value::as_str),
                Some("paragraph" | "heading" | "listItem" | "tableCell" | "tableHeader")
            ) {
                plain_text.push(' ');
            }
        }
    }

    Ok(())
}

fn validate_image(attrs: Option<&Value>) -> AppResult<AttachmentReference> {
    let attrs = attrs.and_then(Value::as_object).ok_or_else(invalid_image)?;
    if attrs
        .keys()
        .any(|key| !["src", "alt", "title", "attachmentId"].contains(&key.as_str()))
    {
        return Err(invalid_image());
    }
    let id = attrs
        .get("attachmentId")
        .and_then(Value::as_str)
        .ok_or_else(invalid_image)?;
    Uuid::parse_str(id).map_err(|_| invalid_image())?;
    let expected_source = format!("knowledge-attachment:{id}");
    if attrs.get("src").and_then(Value::as_str) != Some(expected_source.as_str()) {
        return Err(invalid_image());
    }
    let alt_text = nullable_limited_text(attrs.get("alt"), 500)?;
    let _title = nullable_limited_text(attrs.get("title"), 500)?;
    Ok(AttachmentReference {
        id: id.to_owned(),
        alt_text,
    })
}

fn nullable_limited_text(value: Option<&Value>, maximum: usize) -> AppResult<String> {
    match value {
        None | Some(Value::Null) => Ok(String::new()),
        Some(Value::String(text)) if text.chars().count() <= maximum => Ok(text.clone()),
        _ => Err(invalid_image()),
    }
}

fn validate_attributes(node_type: &str, attrs: Option<&Value>) -> AppResult<()> {
    let Some(attrs) = attrs else { return Ok(()) };
    if attrs.is_null() {
        return Ok(());
    }
    let attrs = attrs.as_object().ok_or_else(invalid_document)?;
    let allowed: &[&str] = match node_type {
        "heading" => &["level"],
        "orderedList" => &["start", "type"],
        "tableCell" | "tableHeader" => &["colspan", "rowspan", "colwidth", "align"],
        _ => &[],
    };

    if attrs.keys().any(|key| !allowed.contains(&key.as_str())) {
        return Err(invalid_document());
    }
    if node_type == "heading" {
        match attrs.get("level").and_then(Value::as_i64) {
            Some(2 | 3) => {}
            _ => return Err(invalid_document()),
        }
    }
    if node_type == "orderedList"
        && attrs
            .get("start")
            .is_some_and(|value| value.as_i64().is_none_or(|start| start < 1))
    {
        return Err(invalid_document());
    }
    if matches!(node_type, "tableCell" | "tableHeader")
        && attrs.get("align").is_some_and(|value| {
            !value.is_null()
                && !matches!(
                    value.as_str(),
                    Some("left" | "center" | "right" | "justify")
                )
        })
    {
        return Err(invalid_document());
    }
    Ok(())
}

fn validate_marks(marks: Option<&Value>) -> AppResult<()> {
    let Some(marks) = marks else { return Ok(()) };
    let marks = marks.as_array().ok_or_else(invalid_document)?;
    for mark in marks {
        let mark_type = mark
            .get("type")
            .and_then(Value::as_str)
            .ok_or_else(invalid_document)?;
        if !ALLOWED_MARKS.contains(&mark_type) {
            return Err(invalid_document());
        }
        if mark_type == "link" {
            validate_link(mark.get("attrs"))?;
        } else if let Some(attrs) = mark.get("attrs") {
            if attrs.as_object().is_some_and(|value| !value.is_empty()) {
                return Err(invalid_document());
            }
        }
    }
    Ok(())
}

fn validate_link(attrs: Option<&Value>) -> AppResult<()> {
    let attrs: &Map<String, Value> = attrs
        .and_then(Value::as_object)
        .ok_or_else(invalid_document)?;
    if attrs
        .keys()
        .any(|key| !["href", "target", "rel", "class", "title"].contains(&key.as_str()))
    {
        return Err(invalid_document());
    }
    let href = attrs
        .get("href")
        .and_then(Value::as_str)
        .ok_or_else(invalid_document)?;
    if href.chars().count() > 2048 {
        return Err(invalid_link());
    }
    if attrs
        .get("target")
        .is_some_and(|value| !value.is_null() && value.as_str() != Some("_blank"))
        || attrs.get("rel").is_some_and(|value| {
            !value.is_null()
                && value.as_str().is_none_or(|rel| {
                    rel.split_whitespace()
                        .any(|token| !matches!(token, "noopener" | "noreferrer" | "nofollow"))
                })
        })
        || attrs
            .get("class")
            .is_some_and(|value| !value.is_null() && value.as_str().is_none())
        || attrs.get("title").is_some_and(|value| {
            !value.is_null()
                && value
                    .as_str()
                    .is_none_or(|title| title.chars().count() > 500)
        })
    {
        return Err(invalid_document());
    }
    let url = Url::parse(href).map_err(|_| invalid_link())?;
    if !matches!(url.scheme(), "http" | "https") {
        return Err(invalid_link());
    }
    Ok(())
}

fn invalid_link() -> AppError {
    AppError::new(
        "ART-005",
        "クリック可能な参考URLは、http:// または https:// で始めてください。",
        "URLを文字として保存する場合は、該当箇所を選択して「リンク解除」を押してから、もう一度保存してください。",
    )
}

fn invalid_document() -> AppError {
    AppError::new(
        "ART-003",
        "回答に利用できない書式が含まれています。",
        "貼り付けた箇所を書式なしで貼り直すか、利用できない書式を解除してから、もう一度保存してください。URL文字列はそのまま保存できます。",
    )
}

fn invalid_image() -> AppError {
    AppError::new(
        "ATT-004",
        "回答内の画像参照が正しくありません。",
        "画像を一度削除し、画像追加ボタンから選び直してください。外部画像URLやBase64画像は使用できません。",
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn extracts_plain_text_from_allowed_document() {
        let document = json!({
            "type": "doc",
            "content": [
                {"type": "heading", "attrs": {"level": 2}, "content": [{"type": "text", "text": "確認手順"}]},
                {"type": "paragraph", "content": [{"type": "text", "text": "PCを再起動します", "marks": [{"type": "bold"}]}]}
            ]
        });

        assert_eq!(
            validate_and_extract(&document).unwrap(),
            "確認手順 PCを再起動します"
        );
    }

    #[test]
    fn rejects_javascript_links() {
        let document = json!({
            "type": "doc",
            "content": [{
                "type": "paragraph",
                "content": [{"type": "text", "text": "危険", "marks": [{"type": "link", "attrs": {"href": "javascript:alert(1)"}}]}]
            }]
        });

        let error = validate_and_extract(&document).unwrap_err();
        assert_eq!(error.code, "ART-005");
        assert!(error.action.contains("リンク解除"));
    }

    #[test]
    fn accepts_url_like_text_without_a_link_mark() {
        let document = json!({
            "type": "doc",
            "content": [{
                "type": "paragraph",
                "content": [{"type": "text", "text": "javascript:alert(1) と file:///C:/manual.html"}]
            }]
        });

        assert_eq!(
            validate_and_extract(&document).unwrap(),
            "javascript:alert(1) と file:///C:/manual.html"
        );
    }

    #[test]
    fn accepts_tiptap_three_link_list_and_table_attributes() {
        let document = json!({
            "type": "doc",
            "content": [
                {
                    "type": "orderedList",
                    "attrs": {"start": 1, "type": null},
                    "content": [{
                        "type": "listItem",
                        "content": [{
                            "type": "paragraph",
                            "content": [{
                                "type": "text",
                                "text": "https://localhost:7100",
                                "marks": [{
                                    "type": "link",
                                    "attrs": {
                                        "href": "https://localhost:7100",
                                        "target": "_blank",
                                        "rel": "noopener noreferrer nofollow",
                                        "class": null,
                                        "title": null
                                    }
                                }]
                            }]
                        }]
                    }]
                },
                {
                    "type": "table",
                    "content": [{
                        "type": "tableRow",
                        "content": [{
                            "type": "tableCell",
                            "attrs": {"colspan": 1, "rowspan": 1, "colwidth": null, "align": null},
                            "content": [{"type": "paragraph", "content": [{"type": "text", "text": "手順"}]}]
                        }]
                    }]
                }
            ]
        });

        assert_eq!(
            validate_and_extract(&document).unwrap(),
            "https://localhost:7100 手順"
        );
    }

    #[test]
    fn rejects_unknown_nodes() {
        let document = json!({"type": "doc", "content": [{"type": "iframe"}]});
        assert!(validate_and_extract(&document).is_err());
    }

    #[test]
    fn accepts_managed_images_and_extracts_alt_text() {
        let id = Uuid::now_v7().to_string();
        let document = json!({
            "type": "doc",
            "content": [{
                "type": "image",
                "attrs": {
                    "src": format!("knowledge-attachment:{id}"),
                    "alt": "設定画面のスクリーンショット",
                    "title": null,
                    "attachmentId": id
                }
            }]
        });

        let validated = validate_and_extract_with_attachments(&document).unwrap();
        assert_eq!(validated.plain_text, "設定画面のスクリーンショット");
        assert_eq!(validated.attachments.len(), 1);
    }

    #[test]
    fn rejects_external_and_base64_images() {
        for source in [
            "https://example.com/image.png",
            "data:image/png;base64,AAAA",
        ] {
            let id = Uuid::now_v7().to_string();
            let document = json!({
                "type": "doc",
                "content": [{
                    "type": "image",
                    "attrs": {"src": source, "alt": null, "title": null, "attachmentId": id}
                }]
            });
            assert_eq!(
                validate_and_extract_with_attachments(&document)
                    .unwrap_err()
                    .code,
                "ATT-004"
            );
        }
    }
}
