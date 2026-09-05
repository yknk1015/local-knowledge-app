// Synthetic parser/interop checks only; never opens an application's data root.
use super::*;
use serde_json::json;

fn nested_array(depth: usize) -> String {
    format!("{}0{}", "[".repeat(depth), "]".repeat(depth))
}

fn proposal_json(body: &str) -> String {
    format!(
        r#"{{"formatVersion":2,"requestId":"51000000-0000-4000-8000-000000000001","seriesId":"55000000-0000-4000-8000-000000000001","createdAt":"2026-09-05T00:00:00Z","proposalKind":"create","sourceArticles":[],"faq":{{"title":"Synthetic depth","summary":"Synthetic only","bodyDoc":{body},"importance":1}},"existingCategoryCandidates":[],"newCategoryProposal":{{"parentCategoryId":null,"parentCategoryPath":null,"name":"Synthetic","description":"","reason":"Synthetic test"}}}}"#
    )
}

fn delegation_json(body: &str) -> String {
    format!(
        r#"{{"formatVersion":1,"delegationId":"52000000-0000-4000-8000-000000000001","createdAt":"2026-09-05T00:00:00Z","kind":"revise","articles":[{{"articleId":"53000000-0000-4000-8000-000000000001","sourceUpdatedAt":"2026-09-05T00:00:00Z","categoryId":"54000000-0000-4000-8000-000000000001","categoryPath":"Synthetic","title":"Synthetic depth","summary":"Synthetic only","bodyDoc":{body},"status":"published","importance":1,"attachments":[]}}]}}"#
    )
}

#[test]
fn codex_interop_parser_boundaries() {
    for depth in [127usize, 128, 129] {
        let value = serde_json::from_str::<Value>(&nested_array(depth)).is_ok();
        let proposal =
            serde_json::from_str::<CodexFaqProposal>(&proposal_json(&nested_array(depth - 2)))
                .is_ok();
        let delegation =
            serde_json::from_str::<CodexDelegation>(&delegation_json(&nested_array(depth - 3)))
                .is_ok();
        println!(
            "RUST_CODEX_DEPTH {depth}: value={value} proposal={proposal} delegation={delegation}"
        );
        assert_eq!(
            (value, proposal, delegation),
            (depth < 128, depth < 128, depth < 128)
        );
    }
}

fn body_at_depth(depth: usize) -> Value {
    let mut leaf = json!({"type":"text","text":"Synthetic deep answer"});
    match (depth - 5) % 4 {
        1 => leaf["marks"] = json!([]),
        2 => leaf["marks"] = json!([{"type":"bold"}]),
        3 => leaf["marks"] = json!([{"type":"bold","attrs":{}}]),
        _ => {}
    }
    let mut child = json!({"type":"paragraph","content":[leaf]});
    for _ in 0..((depth - 5) / 4) {
        child = json!({"type":"bulletList","content":[{"type":"listItem","content":[{"type":"paragraph"}, child]}]});
    }
    json!({"type":"doc","content":[child]})
}

fn container_depth(value: &Value) -> usize {
    match value {
        Value::Object(object) => 1 + object.values().map(container_depth).max().unwrap_or(0),
        Value::Array(array) => 1 + array.iter().map(container_depth).max().unwrap_or(0),
        _ => 0,
    }
}

#[test]
fn codex_interop_rust_service_boundaries() {
    let temporary = tempfile::tempdir().unwrap();
    let root = DataRootService::initialize(temporary.path().join("synthetic-app"), &[]).unwrap();
    for (index, depth) in [77usize, 125, 126].into_iter().enumerate() {
        let body = body_at_depth(depth);
        assert_eq!(container_depth(&body), depth);
        let mut proposal: CodexFaqProposal = serde_json::from_str(&proposal_json(
            &serde_json::to_string(&body_at_depth(5)).unwrap(),
        ))
        .unwrap();
        proposal.request_id = format!("51000000-0000-4000-8000-{:012}", index + 1);
        proposal.faq.body_doc = body;
        validate_proposal(&proposal).unwrap();
        let bytes = serde_json::to_vec_pretty(&proposal).unwrap();
        assert_eq!(
            container_depth(&serde_json::to_value(&proposal).unwrap()),
            depth + 2
        );
        fs::write(proposal_path(&root, &proposal.request_id), bytes).unwrap();
    }
    let (accepted, rejected) = list_proposals(&root).unwrap();
    assert_eq!(accepted.len(), 2);
    assert_eq!(rejected.len(), 1);
    println!("RUST_CODEX_SERVICE: safe full depth79/127 accepted; depth128 rejected");
}

fn synthetic_article(source: &CodexDelegationArticle) -> Article {
    Article {
        id: source.article_id.clone(),
        category_id: source.category_id.clone(),
        category_name: "Synthetic".into(),
        title: source.title.clone(),
        summary: source.summary.clone(),
        body_doc: source.body_doc.clone(),
        body_plain_text: "Synthetic deep answer".into(),
        status: source.status.clone(),
        importance: source.importance,
        new_badge_until: None,
        updated_badge_until: None,
        is_hidden: false,
        created_at: source.source_updated_at.clone(),
        updated_at: source.source_updated_at.clone(),
        created_by_user_id: "synthetic-only".into(),
        created_by_display_name: "Synthetic".into(),
        updated_by_user_id: "synthetic-only".into(),
        updated_by_display_name: "Synthetic".into(),
        deleted_at: None,
        merge_info: None,
        attachments: vec![],
        symptoms: vec![],
        causes: vec![],
        targets: vec![],
        error_codes: vec![],
        procedures: vec![],
        cautions: vec![],
        tags: vec![],
        search_terms: vec![],
        related_articles: vec![],
    }
}

#[test]
#[ignore = "Run only by KnowledgeApp.CodexInteropCheck, which creates and owns the synthetic root"]
fn codex_interop_csharp_roundtrip() {
    // The parent process creates this new root. Read only the fixed synthetic
    // marker/proposal/delegation filenames, never a production DB or mailbox.
    let candidate = PathBuf::from(
        std::env::var("KNOWLEDGEAPP_CODEX_INTEROP_ROOT").expect("Synthetic root required"),
    );
    let temp = std::env::temp_dir().canonicalize().unwrap();
    let normalized = candidate.canonicalize().unwrap();
    assert_eq!(normalized.parent(), Some(temp.as_path()));
    let name = normalized.file_name().unwrap().to_str().unwrap();
    let suffix = name
        .strip_prefix("knowledgeapp-data-check-")
        .expect("Synthetic name required");
    assert!(Uuid::parse_str(suffix).is_ok());
    let marker = normalized.join("codex-interop.synthetic-marker");
    assert!(
        !fs::symlink_metadata(&candidate)
            .unwrap()
            .file_type()
            .is_symlink()
    );
    assert!(
        !fs::symlink_metadata(&marker)
            .unwrap()
            .file_type()
            .is_symlink()
    );
    assert_eq!(
        fs::read_to_string(&marker).unwrap(),
        "KnowledgeApp synthetic Codex cross-runtime v1"
    );
    let root = DataRootService::initialize(normalized, &[]).unwrap();
    let (proposals, rejected) = list_proposals(&root).unwrap();
    assert!(
        rejected.is_empty(),
        "Synthetic rejected entries: {rejected:?}"
    );
    assert_eq!(proposals.len(), 3);
    let mut replies = Vec::new();
    for proposal in proposals {
        validate_proposal(&proposal).unwrap();
        let mut reply = proposal.clone();
        reply.request_id = Uuid::now_v7().to_string();
        if proposal.proposal_kind == CodexProposalKind::Revise {
            validate_delegated_sources(&root, &proposal).unwrap();
            let delegation_path = root.codex_delegations_path().join(format!(
                "{}{}",
                proposal.series_id.as_ref().unwrap(),
                DELEGATION_SUFFIX
            ));
            let delegation: CodexDelegation =
                serde_json::from_slice(&fs::read(&delegation_path).unwrap()).unwrap();
            assert_eq!(delegation.articles.len(), 1);
            let source = &delegation.articles[0];
            assert!(container_depth(&source.body_doc) > 64);
            let category = Category {
                id: source.category_id.clone(),
                parent_id: None,
                name: "Synthetic".into(),
                description: String::new(),
                depth: 1,
                sort_order: 0,
                article_count: 1,
            };
            let written = write_delegation(
                &root,
                CodexDelegationKind::Revise,
                &[synthetic_article(source)],
                &[category],
            )
            .unwrap();
            reply.series_id = Some(written.delegation_id);
            validate_delegated_sources(&root, &reply).unwrap();
        }
        let bytes = serde_json::to_vec_pretty(&reply).unwrap();
        assert!(bytes.len() <= MAX_PROPOSAL_BYTES as usize);
        fs::write(proposal_path(&root, &reply.request_id), &bytes).unwrap();
        assert!(serde_json::from_slice::<CodexFaqProposal>(&bytes).is_ok());
        replies.push(
            json!({"originalRequestId":proposal.request_id,"replyRequestId":reply.request_id}),
        );
    }
    fs::write(
        root.root().join("codex-interop.synthetic-result.json"),
        serde_json::to_vec(&replies).unwrap(),
    )
    .unwrap();
    println!(
        "RUST_CODEX_INTEROP: 3 C# proposals accepted; 2 C# delegations validated; 2 real Rust delegations and 3 proposals returned"
    );
}
