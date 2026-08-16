use std::collections::{BTreeMap, HashMap};

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
    "copyBlock",
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

pub fn remap_attachment_ids(
    document: &Value,
    replacements: &HashMap<String, String>,
) -> AppResult<Value> {
    let mut remapped = document.clone();
    remap_node(&mut remapped, replacements)?;
    Ok(remapped)
}

pub fn without_images(document: &Value) -> AppResult<Value> {
    validate_and_extract_with_attachments(document)?;
    let mut sanitized = document.clone();
    remove_image_nodes(&mut sanitized)?;
    validate_and_extract_with_attachments(&sanitized)?;
    Ok(sanitized)
}

fn remove_image_nodes(node: &mut Value) -> AppResult<()> {
    let object = node.as_object_mut().ok_or_else(invalid_document)?;
    if let Some(content) = object.get_mut("content") {
        let children = content.as_array_mut().ok_or_else(invalid_document)?;
        children.retain(|child| child.get("type").and_then(Value::as_str) != Some("image"));
        for child in children {
            remove_image_nodes(child)?;
        }
    }
    Ok(())
}

fn remap_node(node: &mut Value, replacements: &HashMap<String, String>) -> AppResult<()> {
    let object = node.as_object_mut().ok_or_else(invalid_document)?;
    if object.get("type").and_then(Value::as_str) == Some("image") {
        let attrs = object
            .get_mut("attrs")
            .and_then(Value::as_object_mut)
            .ok_or_else(invalid_image)?;
        let old_id = attrs
            .get("attachmentId")
            .and_then(Value::as_str)
            .ok_or_else(invalid_image)?;
        let new_id = replacements.get(old_id).ok_or_else(invalid_image)?.clone();
        attrs.insert("attachmentId".into(), Value::String(new_id.clone()));
        attrs.insert(
            "src".into(),
            Value::String(format!("knowledge-attachment:{new_id}")),
        );
    }
    if let Some(content) = object.get_mut("content") {
        for child in content.as_array_mut().ok_or_else(invalid_document)? {
            remap_node(child, replacements)?;
        }
    }
    Ok(())
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
        if let std::collections::btree_map::Entry::Vacant(entry) = attachments.entry(reference.id) {
            if !reference.alt_text.trim().is_empty() {
                plain_text.push_str(&reference.alt_text);
                plain_text.push(' ');
            }
            entry.insert(reference.alt_text);
        }
    } else if node_type == "copyBlock" {
        let copy_text = validate_copy_block(object)?;
        plain_text.push_str(copy_text);
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

fn validate_copy_block(object: &Map<String, Value>) -> AppResult<&str> {
    if object.get("content").is_some() || object.get("marks").is_some() {
        return Err(invalid_document());
    }
    let attrs = object
        .get("attrs")
        .and_then(Value::as_object)
        .ok_or_else(invalid_document)?;
    if attrs.keys().any(|key| key != "text") {
        return Err(invalid_document());
    }
    let text = attrs
        .get("text")
        .and_then(Value::as_str)
        .ok_or_else(invalid_document)?;
    if text.is_empty() || text.chars().count() > 4_000 || text.contains('\0') {
        return Err(invalid_document());
    }
    Ok(text)
}

fn validate_image(attrs: Option<&Value>) -> AppResult<AttachmentReference> {
    let attrs = attrs.and_then(Value::as_object).ok_or_else(|| {
        invalid_image_diagnostic(
            "A01",
            "画像の属性情報がないか、オブジェクト形式ではありません。",
        )
    })?;
    let mut unexpected_attributes = attrs
        .keys()
        .filter(|key| !["src", "alt", "title", "attachmentId"].contains(&key.as_str()))
        .map(|key| diagnostic_attribute_name(key))
        .collect::<Vec<_>>();
    if !unexpected_attributes.is_empty() {
        unexpected_attributes.sort();
        let omitted_count = unexpected_attributes.len().saturating_sub(8);
        unexpected_attributes.truncate(8);
        let omitted = if omitted_count == 0 {
            String::new()
        } else {
            format!("、ほか{omitted_count}件")
        };
        return Err(invalid_image_diagnostic(
            "A02",
            format!(
                "許可されていない画像属性があります（属性名: {}{}）。",
                unexpected_attributes.join(", "),
                omitted
            ),
        ));
    }
    let id_value = attrs
        .get("attachmentId")
        .ok_or_else(|| invalid_image_diagnostic("I01", "画像の添付IDがありません。"))?;
    let id = id_value.as_str().ok_or_else(|| {
        invalid_image_diagnostic("I02", "画像の添付IDが文字列形式ではありません。")
    })?;
    Uuid::parse_str(id)
        .map_err(|_| invalid_image_diagnostic("I03", "画像の添付IDがUUID形式ではありません。"))?;
    let expected_source = format!("knowledge-attachment:{id}");
    let source_value = attrs
        .get("src")
        .ok_or_else(|| invalid_image_diagnostic("S01", "画像の保存用参照がありません。"))?;
    let source = source_value.as_str().ok_or_else(|| {
        invalid_image_diagnostic("S02", "画像の保存用参照が文字列形式ではありません。")
    })?;
    if source != expected_source {
        return Err(invalid_image_diagnostic(
            "S03",
            "画像の保存用参照と添付IDが一致しません。",
        ));
    }
    let alt_text =
        nullable_limited_image_text(attrs.get("alt"), 500, "T01", "T02", "代替テキスト")?;
    let _title =
        nullable_limited_image_text(attrs.get("title"), 500, "T03", "T04", "画像タイトル")?;
    Ok(AttachmentReference {
        id: id.to_owned(),
        alt_text,
    })
}

fn nullable_limited_image_text(
    value: Option<&Value>,
    maximum: usize,
    type_code: &str,
    length_code: &str,
    field_name: &str,
) -> AppResult<String> {
    match value {
        None | Some(Value::Null) => Ok(String::new()),
        Some(Value::String(text)) if text.chars().count() <= maximum => Ok(text.clone()),
        Some(Value::String(text)) => Err(invalid_image_diagnostic(
            length_code,
            format!(
                "{field_name}が{maximum}文字を超えています（文字数: {}）。",
                text.chars().count()
            ),
        )),
        _ => Err(invalid_image_diagnostic(
            type_code,
            format!("{field_name}が文字列形式ではありません。"),
        )),
    }
}

fn diagnostic_attribute_name(name: &str) -> String {
    let character_count = name.chars().count();
    let mut sanitized = name
        .chars()
        .take(32)
        .map(|character| {
            if character.is_ascii_alphanumeric() || matches!(character, '-' | '_' | ':') {
                character
            } else {
                '?'
            }
        })
        .collect::<String>();
    if character_count > 32 {
        sanitized.push('…');
    }
    sanitized
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
    if !matches!(url.scheme(), "http" | "https")
        || url.host_str().is_none()
        || !url.username().is_empty()
        || url.password().is_some()
    {
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

fn invalid_image_diagnostic(reason: &str, detail: impl Into<String>) -> AppError {
    AppError::new(
        &format!("ATT-004-{reason}"),
        "回答内の画像参照が正しくありません。",
        format!(
            "画像診断 IMG-20260816-01/{reason}: {} この診断番号と説明だけを開発側へ連絡してください。FAQ本文、画像、ファイルパス、利用者データは送らないでください。",
            detail.into()
        ),
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
    fn accepts_copy_blocks_and_extracts_the_exact_text() {
        let copy_text =
            r"\\192.168.1.250\業務用フォルダ\情報があり得ないほど詰まった古いファイル.xlsx";
        let document = json!({
            "type": "doc",
            "content": [{
                "type": "copyBlock",
                "attrs": {"text": copy_text}
            }]
        });

        assert_eq!(validate_and_extract(&document).unwrap(), copy_text);
    }

    #[test]
    fn rejects_copy_blocks_with_unknown_attributes_or_excessive_text() {
        for document in [
            json!({
                "type": "doc",
                "content": [{"type": "copyBlock", "attrs": {"text": "path", "onclick": "run"}}]
            }),
            json!({
                "type": "doc",
                "content": [{"type": "copyBlock", "attrs": {"text": "a".repeat(4_001)}}]
            }),
        ] {
            assert_eq!(validate_and_extract(&document).unwrap_err().code, "ART-003");
        }
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
    fn reports_one_safe_diagnostic_code_for_each_image_validation_branch() {
        let id = Uuid::now_v7().to_string();
        let marker = format!("knowledge-attachment:{id}");
        let cases = [
            ("A01", Value::Null),
            (
                "A02",
                json!({
                    "src": marker,
                    "alt": null,
                    "title": null,
                    "attachmentId": id,
                    "width": 640,
                    "style": r"background:url(C:\secret\image.png)"
                }),
            ),
            ("I01", json!({"src": marker, "alt": null, "title": null})),
            (
                "I02",
                json!({"src": marker, "alt": null, "title": null, "attachmentId": 1}),
            ),
            (
                "I03",
                json!({"src": marker, "alt": null, "title": null, "attachmentId": "not-a-uuid"}),
            ),
            (
                "S01",
                json!({"alt": null, "title": null, "attachmentId": id}),
            ),
            (
                "S02",
                json!({"src": 1, "alt": null, "title": null, "attachmentId": id}),
            ),
            (
                "S03",
                json!({"src": r"C:\secret\image.png", "alt": null, "title": null, "attachmentId": id}),
            ),
            (
                "T01",
                json!({"src": marker, "alt": {"secret": "value"}, "title": null, "attachmentId": id}),
            ),
            (
                "T02",
                json!({"src": marker, "alt": "x".repeat(501), "title": null, "attachmentId": id}),
            ),
            (
                "T03",
                json!({"src": marker, "alt": null, "title": false, "attachmentId": id}),
            ),
            (
                "T04",
                json!({"src": marker, "alt": null, "title": "x".repeat(501), "attachmentId": id}),
            ),
        ];

        for (reason, attrs) in cases {
            let document = json!({
                "type": "doc",
                "content": [{"type": "image", "attrs": attrs}]
            });
            let error = validate_and_extract_with_attachments(&document).unwrap_err();
            assert_eq!(error.code, format!("ATT-004-{reason}"));
            assert!(error.action.contains(&format!("IMG-20260816-01/{reason}")));
            assert!(!error.action.contains("secret"));
            assert!(!error.action.contains("image.png"));
        }
    }

    #[test]
    fn json_export_removes_images_but_preserves_other_content() {
        let id = Uuid::now_v7().to_string();
        let document = json!({
            "type": "doc",
            "content": [
                {"type":"paragraph","content":[{"type":"text","text":"手順"}]},
                {"type":"image","attrs":{
                    "src": format!("knowledge-attachment:{id}"),
                    "alt": "画像",
                    "title": null,
                    "attachmentId": id
                }},
                {"type":"copyBlock","attrs":{"text":r"C:\manual.xlsx"}}
            ]
        });

        let sanitized = without_images(&document).unwrap();
        assert_eq!(sanitized["content"].as_array().unwrap().len(), 2);
        assert_eq!(
            validate_and_extract_with_attachments(&sanitized)
                .unwrap()
                .plain_text,
            r"手順 C:\manual.xlsx"
        );
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
                "ATT-004-S03"
            );
        }
    }

    #[test]
    fn remaps_managed_image_ids_for_an_independent_copy() {
        let old_id = Uuid::now_v7().to_string();
        let new_id = Uuid::now_v7().to_string();
        let document = json!({
            "type": "doc",
            "content": [{
                "type": "image",
                "attrs": {
                    "src": format!("knowledge-attachment:{old_id}"),
                    "alt": "設定画面",
                    "title": null,
                    "attachmentId": old_id
                }
            }]
        });
        let remapped = remap_attachment_ids(
            &document,
            &HashMap::from([(old_id.clone(), new_id.clone())]),
        )
        .unwrap();
        let validated = validate_and_extract_with_attachments(&remapped).unwrap();
        assert_eq!(validated.attachments[0].id, new_id);
        assert!(!remapped.to_string().contains(&old_id));
    }
}
