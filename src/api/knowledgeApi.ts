import { invoke } from "@tauri-apps/api/core";
import type {
  AppSettings,
  AppError,
  Article,
  BackupOverview,
  BackupPreview,
  BackupResult,
  Category,
  CodexProposalInbox,
  CodexDelegationResult,
  CodexMergePublicationContext,
  AcceptCodexProposalResult,
  ManagementArticlePage,
  ManagementArticlesInput,
  MarkCodexMergeSourcesResult,
  SaveArticleInput,
  SearchArticlesInput,
  SearchArticlePage,
  RestoreResult,
  StagedArticleImage,
  RelatedArticleCandidate,
  SynonymGroup,
  SystemInfo,
  AuthenticatedUser,
  UserRole,
  UserSummary,
  CsvExportResult,
  CsvImportPreview,
  CsvImportResult,
  PasswordPolicySettings,
  SearchLogPage,
  ViewLogPage,
  JsonExportResult,
  JsonImportPreview,
  JsonImportResult,
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
    const appError = toAppError(error);
    if (appError.code === "AUTH-002" && command !== "get_current_user") {
      window.dispatchEvent(new Event("knowledge-auth-expired"));
      window.location.hash = "/login";
    }
    throw appError;
  }
}

export const knowledgeApi = {
  login: (loginId: string, password: string) =>
    call<AuthenticatedUser>("login", { input: { loginId, password } }),
  logout: () => call<void>("logout"),
  getCurrentUser: () => call<AuthenticatedUser | null>("get_current_user"),
  listUsers: () => call<UserSummary[]>("list_users"),
  createUser: (loginId: string, displayName: string, password: string, role: UserRole) =>
    call<UserSummary>("create_user", { input: { loginId, displayName, password, role } }),
  setUserActive: (id: string, isActive: boolean) =>
    call<UserSummary>("set_user_active", { input: { id, isActive } }),
  resetUserPassword: (id: string, password: string) =>
    call<UserSummary>("reset_user_password", { input: { id, password } }),
  getSystemInfo: () => call<SystemInfo>("get_system_info"),
  getSettings: () => call<AppSettings>("get_settings"),
  saveSettings: (input: AppSettings) => call<AppSettings>("save_settings", { input }),
  getPasswordPolicy: () => call<PasswordPolicySettings>("get_password_policy"),
  savePasswordPolicy: (input: PasswordPolicySettings) =>
    call<PasswordPolicySettings>("save_password_policy", { input }),
  listCategories: () => call<Category[]>("list_categories"),
  createCategory: (name: string, description: string, parentId?: string) =>
    call<Category>("create_category", { input: { name, description, parentId: parentId || null } }),
  updateCategory: (id: string, name: string, description: string, parentId?: string) =>
    call<Category>("update_category", { input: { id, name, description, parentId: parentId || null } }),
  reorderCategory: (id: string, direction: "up" | "down") =>
    call<Category[]>("reorder_category", { input: { id, direction } }),
  deleteCategory: (id: string) => call<void>("delete_category", { id }),
  listCodexProposals: () => call<CodexProposalInbox>("list_codex_proposals"),
  acceptCodexProposal: (
    requestId: string,
    categoryId: string | null,
    createProposedCategory: boolean,
  ) => call<AcceptCodexProposalResult>("accept_codex_proposal", {
    input: { requestId, categoryId, createProposedCategory },
  }),
  rejectCodexProposal: (requestId: string) =>
    call<void>("reject_codex_proposal", { requestId }),
  reopenRejectedCodexProposal: (requestId: string) =>
    call<void>("reopen_rejected_codex_proposal", { requestId }),
  createCodexDelegation: (kind: "revise" | "merge", articleIds: string[]) =>
    call<CodexDelegationResult>("create_codex_delegation", {
      input: { kind, articleIds },
    }),
  searchArticles: (input: SearchArticlesInput) =>
    call<SearchArticlePage>("search_articles", { input }),
  recordSearchLog: (
    query: string,
    categoryId: string | undefined,
    scope: "descendants" | "current" | "all",
    resultCount: number,
  ) => call<string>("record_search_log", {
    input: { query, categoryId, scope, resultCount },
  }),
  recordArticleView: (articleId: string, sourceSearchLogId?: string) =>
    call<void>("record_article_view", {
      input: { articleId, sourceSearchLogId },
    }),
  listSearchLogs: (
    query: string,
    startDate: string | undefined,
    endDate: string | undefined,
    zeroResultsOnly: boolean,
    page: number,
  ) => call<SearchLogPage>("list_search_logs", {
    input: { query, startDate, endDate, zeroResultsOnly, page },
  }),
  listViewLogs: (
    query: string,
    startDate: string | undefined,
    endDate: string | undefined,
    page: number,
  ) => call<ViewLogPage>("list_view_logs", {
    input: { query, startDate, endDate, page },
  }),
  deleteHistory: (
    target: "search" | "view",
    startDate: string | undefined,
    endDate: string | undefined,
    deleteAll: boolean,
  ) => call<number>("delete_history", {
    input: { target, startDate, endDate, deleteAll },
  }),
  listSynonymGroups: () => call<SynonymGroup[]>("list_synonym_groups"),
  saveSynonymGroup: (
    id: string | undefined,
    displayName: string,
    terms: string[],
    allowConflicts = false,
  ) => call<SynonymGroup>("save_synonym_group", {
    input: { id, displayName, terms, allowConflicts },
  }),
  deleteSynonymGroup: (id: string) => call<void>("delete_synonym_group", { id }),
  searchRelatedArticles: (articleId: string | undefined, query: string) =>
    call<RelatedArticleCandidate[]>("search_related_articles", {
      input: { articleId, query },
    }),
  getArticle: (id: string) => call<Article>("get_article", { id }),
  getCodexMergePublicationContext: (articleId: string) =>
    call<CodexMergePublicationContext | null>("get_codex_merge_publication_context", {
      articleId,
    }),
  markCodexMergeSources: (articleId: string) =>
    call<MarkCodexMergeSourcesResult>("mark_codex_merge_sources", { articleId }),
  clearArticleMerge: (articleId: string) =>
    call<Article>("clear_article_merge", { articleId }),
  saveArticle: (input: SaveArticleInput) => call<Article>("save_article", { input }),
  duplicateArticle: (id: string) => call<Article>("duplicate_article", { id }),
  stageArticleImage: (path: string) =>
    call<StagedArticleImage>("stage_article_image", { path }),
  stageArticleImageBytes: (originalName: string, bytes: number[]) =>
    call<StagedArticleImage>("stage_article_image_bytes", {
      input: { originalName, bytes },
    }),
  discardStagedArticleImage: (id: string) =>
    call<void>("discard_staged_article_image", { id }),
  openExternalUrl: (url: string) => call<void>("open_external_url", { url }),
  listArticlesForManagement: (input: ManagementArticlesInput) =>
    call<ManagementArticlePage>("list_articles_for_management", { input }),
  exportFaqCsv: (destinationPath: string) =>
    call<CsvExportResult>("export_faq_csv", { input: { destinationPath } }),
  inspectFaqCsv: (path: string) => call<CsvImportPreview>("inspect_faq_csv", { path }),
  importFaqCsv: (sourcePath: string, expectedFileSha256: string) =>
    call<CsvImportResult>("import_faq_csv", { input: { sourcePath, expectedFileSha256 } }),
  exportJson: (destinationPath: string) =>
    call<JsonExportResult>("export_json", { input: { destinationPath } }),
  inspectJson: (path: string) => call<JsonImportPreview>("inspect_json", { path }),
  importJson: (sourcePath: string, expectedFileSha256: string) =>
    call<JsonImportResult>("import_json", { input: { sourcePath, expectedFileSha256 } }),
  deleteArticle: (id: string) => call<Article>("delete_article", { id }),
  restoreArticle: (id: string) => call<Article>("restore_article", { id }),
  getBackupOverview: () => call<BackupOverview>("get_backup_overview"),
  createFullBackup: (destinationPath: string, displayName: string, overwrite = false) =>
    call<BackupResult>("create_full_backup", {
      input: { destinationPath, displayName, overwrite },
    }),
  inspectBackup: (path: string) => call<BackupPreview>("inspect_backup", { path }),
  restoreBackup: async (path: string) => {
    const result = await call<RestoreResult>("restore_backup", { path });
    window.dispatchEvent(new Event("knowledge-auth-expired"));
    window.location.hash = "/login";
    return result;
  },
};
