import { open, save } from "@tauri-apps/plugin-dialog";
import { hasCSharpBridge, invokeCSharp } from "./csharpBridge";

export async function selectFaqCsvExportPath(defaultName: string) {
  if (hasCSharpBridge()) {
    return invokeCSharp<string | null>("select_faq_csv_export_path", { defaultName });
  }
  return save({
    defaultPath: defaultName,
    filters: [{ name: "KnowledgeApp FAQ CSV", extensions: ["csv"] }],
  });
}

export async function selectFaqCsvImportPath() {
  if (hasCSharpBridge()) {
    return invokeCSharp<string | null>("select_faq_csv_import_path");
  }
  return open({
    multiple: false,
    directory: false,
    filters: [{ name: "KnowledgeApp FAQ CSV", extensions: ["csv"] }],
  });
}

export async function selectJsonExportPath(defaultName: string) {
  if (hasCSharpBridge()) {
    return invokeCSharp<string | null>("select_json_export_path", { defaultName });
  }
  return save({
    title: "KnowledgeApp JSONの保存先を選択",
    defaultPath: defaultName,
    filters: [{ name: "KnowledgeApp JSON", extensions: ["json"] }],
  });
}

export async function selectJsonImportPath() {
  if (hasCSharpBridge()) {
    return invokeCSharp<string | null>("select_json_import_path");
  }
  return open({
    title: "取り込むKnowledgeApp JSONを選択",
    multiple: false,
    directory: false,
    filters: [{ name: "KnowledgeApp JSON", extensions: ["json"] }],
  });
}
