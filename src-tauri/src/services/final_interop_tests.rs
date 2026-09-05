// Invoked only by the C# owner of a fresh synthetic TEMP root. No real app root.
use super::{backup, data_root::DataRootService};
use crate::{models::*, repositories::database::Database};
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use std::{fs, path::Path};
use uuid::Uuid;

fn reject_links(path: &Path) {
    for ancestor in path.ancestors() {
        let metadata =
            fs::symlink_metadata(ancestor).expect("Existing synthetic ancestors required");
        assert!(!metadata.file_type().is_symlink());
        #[cfg(windows)]
        {
            use std::os::windows::fs::MetadataExt;
            assert_eq!(metadata.file_attributes() & 0x400, 0, "No reparse points");
        }
    }
}

fn hash(path: &Path) -> String {
    Sha256::digest(fs::read(path).unwrap())
        .iter()
        .map(|value| format!("{value:02x}"))
        .collect()
}

#[test]
#[ignore = "Only run through KnowledgeApp.FinalInteropCheck with its owned synthetic marker and archive"]
fn final_interop_backup_csharp_roundtrip() {
    let candidate = std::path::PathBuf::from(
        std::env::var("KNOWLEDGEAPP_FINAL_INTEROP_ROOT")
            .expect("Explicit synthetic owner required"),
    );
    reject_links(&candidate);
    let owner = candidate.canonicalize().unwrap();
    let temp = std::env::temp_dir().canonicalize().unwrap();
    assert_eq!(owner.parent(), Some(temp.as_path()));
    let suffix = owner
        .file_name()
        .unwrap()
        .to_str()
        .unwrap()
        .strip_prefix("knowledgeapp-data-check-")
        .expect("Exact synthetic owner prefix");
    assert!(Uuid::parse_str(suffix).is_ok());
    let marker = owner.join("final-interop.synthetic-marker");
    reject_links(&marker);
    assert_eq!(
        fs::read_to_string(marker).unwrap(),
        "KnowledgeApp final backup cross-runtime v1"
    );
    let input_path = owner.join("final-interop.synthetic-input.json");
    let source = owner.join("Synthetic_CSharp_to_Rust.faqbackup");
    reject_links(&input_path);
    reject_links(&source);
    assert!(fs::metadata(&input_path).unwrap().len() < 1024 * 1024);
    let expected: Value = serde_json::from_slice(&fs::read(input_path).unwrap()).unwrap();
    assert_eq!(hash(&source), expected["backupSha256"].as_str().unwrap());
    let root_path = owner.join("rust-restored-synthetic");
    assert!(!root_path.exists());
    let root = DataRootService::initialize(root_path, &[]).unwrap();
    let mut database = Database::open(&root.database_path()).unwrap();
    let preview = backup::inspect_backup(&root, &source).unwrap();
    assert_eq!(preview.schema_version, 7);
    assert_eq!(preview.counts.articles, 1);
    assert_eq!(preview.counts.categories, 1);
    assert_eq!(preview.counts.attachments, 1);
    let restored = backup::restore_backup(&root, &mut database, &source).unwrap();
    assert!(Path::new(&restored.safety_backup_path).is_file());
    assert_eq!(database.schema_version().unwrap(), 7);
    let article_id = expected["article"]["id"].as_str().unwrap();
    assert!(Uuid::parse_str(article_id).is_ok());
    let article = database.get_article(article_id).unwrap();
    let actual = serde_json::to_value(&article).unwrap();
    for key in [
        "id",
        "categoryId",
        "title",
        "summary",
        "bodyDoc",
        "bodyPlainText",
        "status",
        "importance",
        "createdAt",
        "updatedAt",
        "createdByUserId",
        "updatedByUserId",
    ] {
        assert_eq!(actual[key], expected["article"][key], "Field {key}");
    }
    assert_eq!(article.attachments.len(), 1);
    let image = &article.attachments[0];
    assert_eq!(image.id, expected["imageId"].as_str().unwrap());
    assert_eq!(image.sha256, expected["imageSha256"].as_str().unwrap());
    assert_eq!(image.alt_text, "合成の1ピクセル画像");
    let image_path = root
        .attachments_path()
        .join(article_id)
        .join(format!("{}.png", image.id));
    assert_eq!(hash(&image_path), image.sha256);
    let settings = database.get_settings().unwrap();
    assert_eq!(
        serde_json::to_value(settings).unwrap(),
        json!({
            "colorTheme":"blue", "showTopCategoryInTitle":false, "showMascot":false
        })
    );
    let user = database
        .authenticate_user("final-interop-user", "Synthetic-FinalInterop-2026!")
        .unwrap();
    assert_eq!(user.id, expected["userId"].as_str().unwrap());
    assert_eq!(serde_json::to_value(&user).unwrap()["role"], "user");
    assert_eq!(database.list_users().unwrap().len(), 2);
    let searches = database
        .list_search_logs(&ListSearchLogsInput {
            query: String::new(),
            start_date: None,
            end_date: None,
            zero_results_only: false,
            page: 1,
        })
        .unwrap();
    assert_eq!(searches.items.len(), 1);
    assert_eq!(searches.items[0].id, expected["searchId"].as_str().unwrap());
    let views = database
        .list_view_logs(&ListViewLogsInput {
            query: String::new(),
            start_date: None,
            end_date: None,
            page: 1,
        })
        .unwrap();
    assert_eq!(views.items.len(), 1);
    assert_eq!(views.items[0].article_id, article_id);
    let added = database
        .create_category("合成Rust往復追加分類", None)
        .unwrap();
    let destination = owner.join("Synthetic_Rust_to_CSharp.faqbackup");
    assert!(!destination.exists());
    let created = backup::create_full_backup(
        &root,
        &database,
        &destination,
        "Synthetic Rust to C#",
        false,
    )
    .unwrap();
    assert_eq!(created.counts.articles, 1);
    assert_eq!(created.counts.categories, 2);
    assert_eq!(created.counts.attachments, 1);
    assert_eq!(hash(&source), expected["backupSha256"].as_str().unwrap());
    let result_path = owner.join("final-interop.synthetic-result.json");
    assert!(!result_path.exists());
    fs::write(
        result_path,
        serde_json::to_vec(&json!({ "rustCategoryId": added.id })).unwrap(),
    )
    .unwrap();
    println!(
        "RUST_FINAL_BACKUP_INTEROP: normal image FAQ, auth, settings, histories and source archive preserved; return backup generated"
    );
}
