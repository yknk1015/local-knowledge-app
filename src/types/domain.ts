export type ArticleStatus = "draft" | "published" | "archived";

export interface Category {
  id: string;
  parentId: string | null;
  name: string;
  depth: number;
  sortOrder: number;
  articleCount: number;
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
  deletedAt: string | null;
  attachments: ArticleAttachment[];
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

export interface SearchArticlesInput {
  query: string;
  categoryId?: string;
  includeDrafts: boolean;
}

export interface ManagementArticlesInput {
  query: string;
  categoryId?: string;
  status?: ArticleStatus;
  deleted: boolean;
  page: number;
}

export interface ManagementArticleListItem extends ArticleListItem {
  deletedAt: string | null;
}

export interface ManagementArticlePage {
  items: ManagementArticleListItem[];
  total: number;
  page: number;
  pageSize: number;
}

export interface SystemInfo {
  appVersion: string;
  dataRoot: string;
  databasePath: string;
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
