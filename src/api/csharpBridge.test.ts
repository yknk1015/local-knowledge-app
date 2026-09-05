import { afterEach, describe, expect, it, vi } from "vitest";
import { knowledgeApi } from "./knowledgeApi";

type BridgeWindow = Window & {
  __KNOWLEDGE_CSHARP_BRIDGE__?: boolean;
  chrome?: { webview?: unknown };
};

afterEach(() => {
  const current = window as BridgeWindow;
  delete current.__KNOWLEDGE_CSHARP_BRIDGE__;
  delete current.chrome;
  vi.restoreAllMocks();
});

describe("C# WebView2 bridge", () => {
  it("routes authentication, category, search, FAQ view/editing, and history contracts without using Tauri invoke", async () => {
    let listener: ((event: MessageEvent) => void) | undefined;
    const postMessage = vi.fn((message: unknown) => {
      const request = message as { id: string; command: string; args: Record<string, unknown> };
      const result = request.command === "login"
        ? { id: "admin", loginId: "0000", displayName: "初期管理者", role: "admin" }
        : request.command === "list_categories"
          ? [{
              id: "network",
              parentId: null,
              name: "ネットワーク",
              description: "合成分類",
              depth: 1,
              sortOrder: 0,
              articleCount: 1,
            }]
          : request.command === "search_articles"
            ? { items: [], total: 0, page: 1, pageSize: 50 }
            : request.command === "get_article"
              ? {
                  id: "mesh",
                  categoryId: "wifi",
                  categoryName: "Wi-Fi",
                  title: "メッシュWi-FiでZoomが切れる原因と対処は？",
                  summary: "合成概要",
                  bodyDoc: { type: "doc", content: [] },
                  bodyPlainText: "合成本文",
                  status: "published",
                  importance: 3,
                  newBadgeUntil: null,
                  updatedBadgeUntil: null,
                  isHidden: false,
                  createdAt: "2026-08-01T00:00:00Z",
                  updatedAt: "2026-08-01T00:00:00Z",
                  createdByUserId: "admin",
                  createdByDisplayName: "初期管理者",
                  updatedByUserId: "admin",
                  updatedByDisplayName: "初期管理者",
                  deletedAt: null,
                  mergeInfo: null,
                  attachments: [],
                  symptoms: [],
                  causes: [],
                  targets: [],
                  errorCodes: [],
                  procedures: [],
                  cautions: [],
                  tags: ["Zoom"],
                  searchTerms: [],
                  relatedArticles: [],
                }
            : request.command === "list_tags"
              ? [{ id: "tag-zoom", name: "Zoom", usageCount: 1, updatedAt: "2026-08-01T00:00:00Z" }]
            : request.command === "list_articles_for_management"
              ? { items: [], total: 0, page: 1, pageSize: 50 }
            : request.command === "get_codex_merge_publication_context"
              ? null
            : request.command === "list_search_logs"
              ? {
                  items: [{
                    id: "search-log-1",
                    queryText: "Zoom",
                    normalizedQuery: "zoom",
                    scope: "all",
                    categoryName: null,
                    resultCount: 1,
                    createdAt: "2026-08-30T00:00:00Z",
                  }],
                  total: 1,
                  page: 1,
                  pageSize: 50,
                }
            : request.command === "list_view_logs"
              ? {
                  items: [{
                    id: "view-log-1",
                    articleId: "mesh",
                    articleTitle: "メッシュWi-FiでZoomが切れる原因と対処は？",
                    sourceQueryText: "Zoom",
                    viewedAt: "2026-08-30T00:01:00Z",
                  }],
                  total: 1,
                  page: 1,
                  pageSize: 50,
                }
            : request.command === "delete_history"
              ? 1
            : ["save_article", "duplicate_article", "delete_article", "restore_article"].includes(request.command)
              ? {
                  id: "mesh",
                  categoryId: "wifi",
                  categoryName: "Wi-Fi",
                  title: "C#編集契約",
                  summary: "合成概要",
                  bodyDoc: { type: "doc", content: [{ type: "paragraph", content: [{ type: "text", text: "合成本文" }] }] },
                  bodyPlainText: "合成本文",
                  status: "draft",
                  importance: 1,
                  newBadgeUntil: null,
                  updatedBadgeUntil: null,
                  isHidden: false,
                  createdAt: "2026-08-01T00:00:00Z",
                  updatedAt: "2026-08-01T00:00:00Z",
                  deletedAt: null,
                  mergeInfo: null,
                  attachments: [],
                  symptoms: [],
                  causes: [],
                  targets: [],
                  errorCodes: [],
                  procedures: [],
                  cautions: [],
                  tags: ["Zoom"],
                  searchTerms: [],
                  relatedArticles: [],
                }
            : undefined;
      queueMicrotask(() => listener?.(new MessageEvent("message", {
        data: {
          id: request.id,
          ok: true,
          result,
          error: null,
        },
      })));
    });
    const current = window as BridgeWindow;
    current.__KNOWLEDGE_CSHARP_BRIDGE__ = true;
    current.chrome = {
      webview: {
        postMessage,
        addEventListener: (_name: string, next: (event: MessageEvent) => void) => { listener = next; },
      },
    };

    await expect(knowledgeApi.login("0000", "")).resolves.toEqual({
      id: "admin",
      loginId: "0000",
      displayName: "初期管理者",
      role: "admin",
    });
    expect(postMessage).toHaveBeenCalledTimes(1);
    await expect(knowledgeApi.listCategories()).resolves.toEqual([{
      id: "network",
      parentId: null,
      name: "ネットワーク",
      description: "合成分類",
      depth: 1,
      sortOrder: 0,
      articleCount: 1,
    }]);
    await expect(knowledgeApi.searchArticles({
      query: "Wi-Fi",
      categoryId: "network",
      scope: "descendants",
      includeDrafts: false,
      page: 1,
      sort: "updatedDesc",
    })).resolves.toEqual({ items: [], total: 0, page: 1, pageSize: 50 });
    await expect(knowledgeApi.getArticle("mesh")).resolves.toEqual(expect.objectContaining({
      id: "mesh",
      categoryName: "Wi-Fi",
      tags: ["Zoom"],
    }));
    await expect(knowledgeApi.recordArticleView("mesh", "search-log-1")).resolves.toBeUndefined();
    await expect(knowledgeApi.listTags()).resolves.toEqual([
      expect.objectContaining({ id: "tag-zoom", name: "Zoom", usageCount: 1 }),
    ]);
    await expect(knowledgeApi.getCodexMergePublicationContext("mesh")).resolves.toBeNull();
    await expect(knowledgeApi.saveArticle({
      id: "mesh",
      categoryId: "wifi",
      title: "C#編集契約",
      summary: "合成概要",
      bodyDoc: { type: "doc", content: [{ type: "paragraph", content: [{ type: "text", text: "合成本文" }] }] },
      status: "draft",
      importance: 1,
      newBadgeUntil: null,
      updatedBadgeUntil: null,
      isHidden: false,
      symptoms: [],
      causes: [],
      targets: [],
      errorCodes: [],
      procedures: [],
      cautions: [],
      tags: ["Zoom"],
      searchTerms: [],
      relatedArticleIds: [],
    })).resolves.toEqual(expect.objectContaining({ id: "mesh", title: "C#編集契約" }));
    await expect(knowledgeApi.listArticlesForManagement({
      query: "C#編集契約",
      categoryId: "wifi",
      status: "draft",
      deleted: false,
      page: 1,
    })).resolves.toEqual({ items: [], total: 0, page: 1, pageSize: 50 });
    await expect(knowledgeApi.duplicateArticle("mesh")).resolves.toEqual(expect.objectContaining({ id: "mesh" }));
    await expect(knowledgeApi.deleteArticle("mesh")).resolves.toEqual(expect.objectContaining({ id: "mesh" }));
    await expect(knowledgeApi.restoreArticle("mesh")).resolves.toEqual(expect.objectContaining({ id: "mesh" }));
    await expect(knowledgeApi.listSearchLogs("ｚｏｏｍ", "2026-08-30", "2026-08-30", true, 1))
      .resolves.toEqual(expect.objectContaining({ total: 1, pageSize: 50 }));
    await expect(knowledgeApi.listViewLogs("Zoom", undefined, undefined, 1))
      .resolves.toEqual(expect.objectContaining({ total: 1, pageSize: 50 }));
    await expect(knowledgeApi.deleteHistory("search", "2026-08-30", "2026-08-30", false))
      .resolves.toBe(1);
    expect(postMessage).toHaveBeenNthCalledWith(1, expect.objectContaining({
      command: "login",
      args: { input: { loginId: "0000", password: "" } },
    }));
    expect(postMessage).toHaveBeenNthCalledWith(2, expect.objectContaining({
      command: "list_categories",
      args: {},
    }));
    expect(postMessage).toHaveBeenNthCalledWith(3, expect.objectContaining({
      command: "search_articles",
      args: {
        input: {
          query: "Wi-Fi",
          categoryId: "network",
          scope: "descendants",
          includeDrafts: false,
          page: 1,
          sort: "updatedDesc",
        },
      },
    }));
    expect(postMessage).toHaveBeenNthCalledWith(4, expect.objectContaining({
      command: "get_article",
      args: { id: "mesh" },
    }));
    expect(postMessage).toHaveBeenNthCalledWith(5, expect.objectContaining({
      command: "record_article_view",
      args: { input: { articleId: "mesh", sourceSearchLogId: "search-log-1" } },
    }));
    expect(postMessage).toHaveBeenNthCalledWith(6, expect.objectContaining({ command: "list_tags", args: {} }));
    expect(postMessage).toHaveBeenNthCalledWith(7, expect.objectContaining({
      command: "get_codex_merge_publication_context",
      args: { articleId: "mesh" },
    }));
    expect(postMessage).toHaveBeenNthCalledWith(8, expect.objectContaining({
      command: "save_article",
      args: { input: expect.objectContaining({ id: "mesh", categoryId: "wifi", tags: ["Zoom"] }) },
    }));
    expect(postMessage).toHaveBeenNthCalledWith(9, expect.objectContaining({
      command: "list_articles_for_management",
      args: { input: expect.objectContaining({ query: "C#編集契約", deleted: false, page: 1 }) },
    }));
    expect(postMessage).toHaveBeenNthCalledWith(10, expect.objectContaining({ command: "duplicate_article", args: { id: "mesh" } }));
    expect(postMessage).toHaveBeenNthCalledWith(11, expect.objectContaining({ command: "delete_article", args: { id: "mesh" } }));
    expect(postMessage).toHaveBeenNthCalledWith(12, expect.objectContaining({ command: "restore_article", args: { id: "mesh" } }));
    expect(postMessage).toHaveBeenNthCalledWith(13, expect.objectContaining({
      command: "list_search_logs",
      args: { input: { query: "ｚｏｏｍ", startDate: "2026-08-30", endDate: "2026-08-30", zeroResultsOnly: true, page: 1 } },
    }));
    expect(postMessage).toHaveBeenNthCalledWith(14, expect.objectContaining({
      command: "list_view_logs",
      args: { input: { query: "Zoom", startDate: undefined, endDate: undefined, page: 1 } },
    }));
    expect(postMessage).toHaveBeenNthCalledWith(15, expect.objectContaining({
      command: "delete_history",
      args: { input: { target: "search", startDate: "2026-08-30", endDate: "2026-08-30", deleteAll: false } },
    }));
  });
});
