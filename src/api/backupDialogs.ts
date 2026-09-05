import { open, save } from "@tauri-apps/plugin-dialog";
import { hasCSharpBridge, invokeCSharp } from "./csharpBridge";

export async function selectFullBackupDestination(defaultPath: string) {
  if (hasCSharpBridge()) {
    return invokeCSharp<string | null>("select_full_backup_destination", { defaultPath });
  }
  return save({
    title: "フルバックアップの保存先と名前を選択",
    defaultPath,
    filters: [{ name: "KnowledgeAppフルバックアップ", extensions: ["faqbackup"] }],
  });
}

export async function selectRestoreBackupSource() {
  if (hasCSharpBridge()) {
    return invokeCSharp<string | null>("select_restore_backup_source");
  }
  return open({
    title: "復元するフルバックアップを選択",
    multiple: false,
    directory: false,
    filters: [{ name: "KnowledgeAppフルバックアップ", extensions: ["faqbackup"] }],
  });
}
