import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { knowledgeApi } from "../api/knowledgeApi";
import type { Article } from "../types/domain";
import { App } from "./App";
import { AuthProvider } from "./AuthContext";
import { ColorThemeProvider } from "./ColorTheme";

vi.mock("../components/RichTextEditor", () => ({
  RichTextEditor: () => <div aria-label="FAQの回答">回答編集欄</div>,
  RichTextViewer: () => <div>回答本文</div>,
}));

describe("release FAQ screen workflow", () => {
  afterEach(() => vi.restoreAllMocks());

  it("moves from search to detail, edits the FAQ, and returns to the updated detail", async () => {
    const category = {
      id: "category-1",
      parentId: null,
      name: "PC",
      description: "PC全般",
      depth: 1,
      sortOrder: 0,
      articleCount: 1,
    };
    let current: Article = {
      id: "article-1",
      categoryId: category.id,
      categoryName: category.name,
      title: "画面が暗いときは？",
      summary: "表示設定を確認します。",
      bodyDoc: {
        type: "doc",
        content: [{ type: "paragraph", content: [{ type: "text", text: "明るさを確認します。" }] }],
      },
      bodyPlainText: "明るさを確認します。",
      status: "published",
      importance: 1,
      newBadgeUntil: null,
      updatedBadgeUntil: null,
      isHidden: false,
      createdAt: "2026-08-16T00:00:00Z",
      updatedAt: "2026-08-16T00:00:00Z",
      createdByUserId: "user-1",
      createdByDisplayName: "確認担当",
      updatedByUserId: "user-1",
      updatedByDisplayName: "確認担当",
      deletedAt: null,
      mergeInfo: null,
      attachments: [],
      symptoms: [],
      causes: [],
      targets: [],
      errorCodes: [],
      procedures: [],
      cautions: [],
      tags: [],
      searchTerms: [],
      relatedArticles: [],
    };

    vi.spyOn(knowledgeApi, "getCurrentUser").mockResolvedValue({
      id: "user-1",
      loginId: "0000",
      displayName: "確認担当",
      role: "admin",
    });
    vi.spyOn(knowledgeApi, "getRecoveryKeyStatus").mockResolvedValue({ hasKey: false, needsSetup: false, issuedAt: null });
    vi.spyOn(knowledgeApi, "getSettings").mockResolvedValue({
      colorTheme: "green",
      showTopCategoryInTitle: true,
      showMascot: false,
    });
    vi.spyOn(knowledgeApi, "saveSettings").mockImplementation(async (settings) => settings);
    vi.spyOn(knowledgeApi, "logout").mockResolvedValue();
    vi.spyOn(knowledgeApi, "listCategories").mockResolvedValue([category]);
    vi.spyOn(knowledgeApi, "listTags").mockResolvedValue([]);
    vi.spyOn(knowledgeApi, "searchArticles").mockImplementation(async () => ({
      items: [{
        id: current.id,
        categoryId: current.categoryId,
        categoryName: current.categoryName,
        title: current.title,
        summary: current.summary,
        status: current.status,
        importance: current.importance,
        newBadgeUntil: current.newBadgeUntil,
        updatedBadgeUntil: current.updatedBadgeUntil,
        isHidden: current.isHidden,
        updatedAt: current.updatedAt,
        tags: [],
        matchReasons: [],
      }],
      total: 1,
      page: 1,
      pageSize: 50,
    }));
    vi.spyOn(knowledgeApi, "recordSearchLog").mockResolvedValue("search-log-1");
    const recordView = vi.spyOn(knowledgeApi, "recordArticleView").mockResolvedValue(undefined);
    const getArticle = vi.spyOn(knowledgeApi, "getArticle").mockImplementation(async () => current);
    vi.spyOn(knowledgeApi, "getCodexMergePublicationContext").mockResolvedValue(null);
    const saveArticle = vi.spyOn(knowledgeApi, "saveArticle").mockImplementation(async (input) => {
      current = {
        ...current,
        ...input,
        id: current.id,
        categoryName: category.name,
        bodyPlainText: current.bodyPlainText,
        updatedAt: "2026-08-16T01:00:00Z",
      };
      return current;
    });

    const router = createMemoryRouter([{
      path: "*",
      element: (
        <AuthProvider>
          <ColorThemeProvider>
            <App />
          </ColorThemeProvider>
        </AuthProvider>
      ),
    }], { initialEntries: ["/search"] });
    render(<RouterProvider router={router} />);

    fireEvent.click(await screen.findByRole("link", { name: /画面が暗いときは/ }));
    expect(await screen.findByRole("heading", { name: "画面が暗いときは？" })).toBeVisible();
    expect(recordView).toHaveBeenCalledWith("article-1", undefined);

    fireEvent.click(screen.getByRole("link", { name: "編集する" }));
    const title = await screen.findByLabelText(/タイトル/);
    fireEvent.change(title, { target: { value: "画面の明るさを変えるには？" } });
    fireEvent.click(screen.getByRole("button", { name: "FAQを保存" }));

    await waitFor(() => expect(saveArticle).toHaveBeenCalledWith(expect.objectContaining({
      id: "article-1",
      title: "画面の明るさを変えるには？",
      status: "published",
    })));
    expect(await screen.findByRole("heading", { name: "画面の明るさを変えるには？" })).toBeVisible();
    expect(getArticle).toHaveBeenCalledTimes(3);
  });
});
