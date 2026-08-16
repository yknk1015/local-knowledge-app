export type ArticleStatus = "draft" | "published" | "archived";
export type UserRole = "admin" | "user";

export interface AuthenticatedUser {
  id: string;
  loginId: string;
  displayName: string;
  role: UserRole;
}

export interface UserSummary extends AuthenticatedUser {
  isActive: boolean;
  createdAt: string;
  updatedAt: string;
  lastLoginAt: string | null;
}

export interface Category {
  id: string;
  parentId: string | null;
  name: string;
  description: string;
  depth: number;
  sortOrder: number;
  articleCount: number;
}

export interface CodexCategoryCandidate {
  categoryId: string;
  categoryPath: string;
  reason: string;
}

export interface CodexNewCategoryProposal {
  parentCategoryId: string | null;
  parentCategoryPath: string | null;
  name: string;
  description: string;
  reason: string;
}

export interface CodexFaqProposal {
  formatVersion: number;
  requestId: string;
  seriesId: string | null;
  createdAt: string;
  proposalKind: "create" | "revise" | "merge";
  sourceArticles: Array<{ articleId: string; sourceUpdatedAt: string }>;
  faq: {
    title: string;
    summary: string;
    bodyDoc: Record<string, unknown>;
    importance: number;
  };
  existingCategoryCandidates: CodexCategoryCandidate[];
  newCategoryProposal: CodexNewCategoryProposal | null;
}

export interface CodexProposalInbox {
  proposals: CodexFaqProposal[];
  history: CodexProposalHistoryItem[];
  rejected: Array<{ fileName: string; message: string }>;
  inboxPath: string;
  categoryCatalogPath: string;
}

export interface CodexProposalHistoryItem {
  historyId: number;
  proposal: CodexFaqProposal;
  status: "accepted" | "rejected";
  receivedAt: string;
  decidedAt: string | null;
  acceptedArticleId: string | null;
  canReopen: boolean;
}

export interface AcceptCodexProposalResult {
  article: Article;
  createdCategory: Category | null;
}

export interface CodexMergeSourcePreview {
  articleId: string;
  title: string;
  status: ArticleStatus | null;
  sourceUpdatedAt: string;
  currentUpdatedAt: string | null;
  deletedAt: string | null;
  isCurrent: boolean;
  isMerged: boolean;
}

export interface CodexMergePublicationContext {
  targetArticleId: string;
  sourceArticles: CodexMergeSourcePreview[];
  canMarkMerged: boolean;
  allSourcesMerged: boolean;
}

export interface MarkCodexMergeSourcesResult {
  targetArticleId: string;
  markedCount: number;
}

export interface CodexDelegationResult {
  delegationId: string;
  prompt: string;
  filePath: string;
}

export interface ArticleListItem {
  id: string;
  categoryId: string;
  categoryName: string;
  title: string;
  summary: string;
  status: ArticleStatus;
  importance: number;
  newBadgeUntil: string | null;
  updatedBadgeUntil: string | null;
  isHidden: boolean;
  updatedAt: string;
}

export interface Article extends ArticleListItem {
  bodyDoc: Record<string, unknown>;
  bodyPlainText: string;
  createdAt: string;
  createdByUserId?: string;
  createdByDisplayName?: string;
  updatedByUserId?: string;
  updatedByDisplayName?: string;
  deletedAt: string | null;
  mergeInfo: ArticleMergeInfo | null;
  attachments: ArticleAttachment[];
}

export interface ArticleMergeInfo {
  targetArticleId: string;
  targetArticleTitle: string;
  mergedAt: string;
}

export interface ArticleAttachment {
  id: string;
  originalName: string;
  mediaType: string;
  byteSize: number;
  sha256: string;
  altText: string;
  assetPath: string;
  createdAt: string;
}

export interface StagedArticleImage {
  id: string;
  originalName: string;
  mediaType: string;
  byteSize: number;
  sha256: string;
  altText: string;
  assetPath: string;
}

export interface SaveArticleInput {
  id?: string;
  categoryId: string;
  title: string;
  summary: string;
  bodyDoc: Record<string, unknown>;
  status: ArticleStatus;
  importance: number;
  newBadgeUntil: string | null;
  updatedBadgeUntil: string | null;
  isHidden: boolean;
}

export type SearchSort = "updatedDesc" | "updatedAsc" | "importanceDesc" | "importanceAsc";

export interface SearchArticlesInput {
  query: string;
  categoryId?: string;
  includeDrafts: boolean;
  page: number;
  sort: SearchSort;
}

export interface SearchArticlePage {
  items: ArticleListItem[];
  total: number;
  page: number;
  pageSize: number;
}

export interface ManagementArticlesInput {
  query: string;
  categoryId?: string;
  status?: ArticleStatus;
  deleted: boolean;
  page: number;
}

export interface ManagementArticleListItem extends ArticleListItem {
  createdByDisplayName?: string;
  updatedByDisplayName?: string;
  deletedAt: string | null;
  mergeInfo: ArticleMergeInfo | null;
}

export interface ManagementArticlePage {
  items: ManagementArticleListItem[];
  total: number;
  page: number;
  pageSize: number;
}

export interface CsvExportResult {
  destinationPath: string;
  exportedCount: number;
}

export interface CsvImportPreviewRow {
  line: number;
  action: "create" | "update" | "unchanged" | "error";
  faqManagementId: string | null;
  title: string;
  bodyWillBeReplaced: boolean;
  staleUpdateWillOverwrite: boolean;
  messages: string[];
}

export interface CsvImportPreview {
  sourcePath: string;
  fileSha256: string;
  totalRows: number;
  createCount: number;
  updateCount: number;
  unchangedCount: number;
  bodyReplacementCount: number;
  staleOverwriteCount: number;
  errorCount: number;
  rows: CsvImportPreviewRow[];
}

export interface CsvImportResult {
  sourcePath: string;
  safetyBackupPath: string;
  createdCount: number;
  updatedCount: number;
  unchangedCount: number;
}

export interface SystemInfo {
  appVersion: string;
  dataRoot: string;
  databasePath: string;
  codexCategoryCatalogPath: string;
  codexInboxPath: string;
}

export type ColorTheme = "green" | "blue";

export interface AppSettings {
  colorTheme: ColorTheme;
  showTopCategoryInTitle: boolean;
  showMascot: boolean;
}

export interface PasswordPolicySettings {
  allowEmptyPasswords: boolean;
}

export interface BackupCounts {
  articles: number;
  categories: number;
  attachments: number;
  manuals: number;
}

export interface BackupOverview {
  estimatedBytes: number;
  counts: BackupCounts;
  defaultDirectory: string | null;
}

export interface BackupPreview {
  sourcePath: string;
  displayName: string;
  createdAt: string;
  appVersion: string;
  schemaVersion: number;
  backupFormatVersion: number;
  totalBytes: number;
  counts: BackupCounts;
}

export interface BackupResult {
  destinationPath: string;
  displayName: string;
  createdAt: string;
  totalBytes: number;
  counts: BackupCounts;
}

export interface RestoreResult {
  sourcePath: string;
  safetyBackupPath: string;
  restoredAt: string;
  counts: BackupCounts;
}

export interface AppError {
  code: string;
  message: string;
  action: string;
}
