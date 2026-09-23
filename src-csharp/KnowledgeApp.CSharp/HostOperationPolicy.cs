namespace KnowledgeApp.CSharp;

internal static class HostOperationPolicy
{
    // These operations may hold the database lock or create managed working files.
    // Wait for the host result before allowing disposal of the synthetic data root.
    internal static bool PreventsWindowClose(string command) => command is
        "create_full_backup" or "inspect_backup" or "restore_backup" or
        "export_faq_csv" or "inspect_faq_csv" or "import_faq_csv" or
        "export_json" or "inspect_json" or "import_json" or
        "list_codex_proposals" or "accept_codex_proposal" or "reject_codex_proposal" or
        "reopen_rejected_codex_proposal" or "create_codex_delegation" or
        "mark_codex_merge_sources" or "clear_article_merge" or "save_tag" or
        "issue_recovery_key" or "verify_recovery_key" or "complete_password_recovery" or "skip_recovery_setup" or "save_connection_settings" or "change_codex_location" or "save_storage_folder" or "check_storage_folder" or "set_user_role";
}
