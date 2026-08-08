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
  updatedAt: string;
}

export interface Article extends ArticleListItem {
  bodyDoc: Record<string, unknown>;
  bodyPlainText: string;
  createdAt: string;
}

export interface SaveArticleInput {
  id?: string;
  categoryId: string;
  title: string;
  summary: string;
  bodyDoc: Record<string, unknown>;
  status: ArticleStatus;
  importance: number;
}

export interface SearchArticlesInput {
  query: string;
  categoryId?: string;
  includeDrafts: boolean;
}

export interface SystemInfo {
  appVersion: string;
  dataRoot: string;
  databasePath: string;
}

export interface AppError {
  code: string;
  message: string;
  action: string;
}
