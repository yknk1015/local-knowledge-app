// CSVの読込・書出し処理は、DBトランザクションと同じ境界で実装するため
// repositories::database から呼び出す共通の形式変換だけをここへ集約する。
use serde_json::{Value, json};
use sha2::{Digest, Sha256};

pub const FORMAT_VERSION: &str = "2";
pub const HEADERS: [&str; 17] = [
    "形式バージョン",
    "FAQ管理ID",
    "分類管理ID",
    "分類パス",
    "タイトル",
    "概要",
    "回答本文",
    "回答本文ハッシュ",
    "状態",
    "重要度",
    "新着表示終了日",
    "更新表示終了日",
    "非表示",
    "作成者",
    "更新者",
    "作成日時",
    "更新日時",
];

pub fn body_hash(plain_text: &str) -> String {
    Sha256::digest(normalize_newlines(plain_text).as_bytes())
        .iter()
        .map(|byte| format!("{byte:02x}"))
        .collect()
}

pub fn normalize_newlines(value: &str) -> String {
    value.replace("\r\n", "\n").replace('\r', "\n")
}

pub fn plain_text_to_document(plain_text: &str) -> Value {
    let normalized = normalize_newlines(plain_text);
    let content = normalized
        .split('\n')
        .map(|line| {
            if line.is_empty() {
                json!({ "type": "paragraph" })
            } else {
                json!({
                    "type": "paragraph",
                    "content": [{ "type": "text", "text": line }]
                })
            }
        })
        .collect::<Vec<_>>();
    json!({ "type": "doc", "content": content })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn normalizes_line_endings_before_hashing() {
        assert_eq!(body_hash("a\r\nb"), body_hash("a\nb"));
    }

    #[test]
    fn converts_each_line_to_a_safe_paragraph() {
        let document = plain_text_to_document("1行目\n\n3行目");
        assert_eq!(document["content"].as_array().unwrap().len(), 3);
        assert_eq!(document["content"][0]["content"][0]["text"], "1行目");
        assert!(document["content"][1].get("content").is_none());
    }
}
