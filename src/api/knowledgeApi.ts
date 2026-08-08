import { invoke } from "@tauri-apps/api/core";
import type {
  AppError,
  Article,
  ArticleListItem,
  BackupOverview,
  BackupPreview,
  BackupResult,
  Category,
  SaveArticleInput,
  SearchArticlesInput,
  RestoreResult,
  SystemInfo,
} from "../types/domain";

function isAppError(value: unknown): value is AppError {
  if (!value || typeof value !== "object") return false;
  const candidate = value as Record<string, unknown>;
  return (
    typeof candidate.code === "string" &&
    typeof candidate.message === "string" &&
    typeof candidate.action === "string"
  );
}

export function toAppError(value: unknown): AppError {
  if (isAppError(value)) return value;
  if (typeof value === "string") {
    try {
      const parsed = JSON.parse(value) as unknown;
      if (isAppError(parsed)) return parsed;
    } catch {
      return {
        code: "SYS-001",
        message: value,
        action: "アプリを再起動してください。解決しない場合は診断情報を確認してください。",
      };
    }
  }
  return {
    code: "SYS-001",
    message: "予期しないエラーが発生しました。",
    action: "アプリを再起動してください。解決しない場合は診断情報を確認してください。",
  };
}

async function call<T>(command: string, args?: Record<string, unknown>): Promise<T> {
  try {
    return await invoke<T>(command, args);
  } catch (error) {
    throw toAppError(error);
  }
}

export const knowledgeApi = {
  getSystemInfo: () => call<SystemInfo>("get_system_info"),
  listCategories: () => call<Category[]>("list_categories"),
  createCategory: (name: string, parentId?: string) =>
    call<Category>("create_category", { input: { name, parentId: parentId || null } }),
  searchArticles: (input: SearchArticlesInput) =>
    call<ArticleListItem[]>("search_articles", { input }),
  getArticle: (id: string) => call<Article>("get_article", { id }),
  saveArticle: (input: SaveArticleInput) => call<Article>("save_article", { input }),
  getBackupOverview: () => call<BackupOverview>("get_backup_overview"),
  createFullBackup: (destinationPath: string, displayName: string, overwrite = false) =>
    call<BackupResult>("create_full_backup", {
      input: { destinationPath, displayName, overwrite },
    }),
  inspectBackup: (path: string) => call<BackupPreview>("inspect_backup", { path }),
  restoreBackup: (path: string) => call<RestoreResult>("restore_backup", { path }),
};
