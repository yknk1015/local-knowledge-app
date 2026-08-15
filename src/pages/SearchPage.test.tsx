import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { knowledgeApi } from "../api/knowledgeApi";
import { ColorThemeProvider } from "../app/ColorTheme";
import { ArticleDetailPage } from "./ArticleDetailPage";
import { SearchPage } from "./SearchPage";

function renderWithSettings(children: ReactNode) {
  return render(<ColorThemeProvider>{children}</ColorThemeProvider>);
}

describe("SearchPage", () => {
  beforeEach(() => {
    vi.spyOn(knowledgeApi, "getSettings").mockResolvedValue({
      colorTheme: "green",
      showTopCategoryInTitle: true,
    });
    vi.spyOn(knowledgeApi, "saveSettings").mockImplementation(async (settings) => settings);
  });

  afterEach(() => {
    vi.restoreAllMocks();
    window.sessionStorage.clear();
  });

  it("guides a first-time user to create a category", async () => {
    vi.spyOn(knowledgeApi, "listCategories").mockResolvedValue([]);
    vi.spyOn(knowledgeApi, "searchArticles").mockResolvedValue([]);

    renderWithSettings(
      <MemoryRouter>
        <SearchPage />
      </MemoryRouter>,
    );

    expect(await screen.findByRole("heading", { name: "最初の分類を作りましょう" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "分類を作成する" })).toHaveAttribute("href", "/categories");
  });

  it("shows saved articles without rendering their body", async () => {
    vi.spyOn(knowledgeApi, "listCategories").mockResolvedValue([
      { id: "cat", parentId: null, name: "Windows", description: "", depth: 1, sortOrder: 0, articleCount: 1 },
      { id: "display", parentId: "cat", name: "画面表示", description: "", depth: 2, sortOrder: 0, articleCount: 1 },
    ]);
    vi.spyOn(knowledgeApi, "searchArticles").mockResolvedValue([
      {
        id: "article",
        categoryId: "display",
        categoryName: "画面表示",
        title: "画面が真っ暗になったとき",
        summary: "画面表示を確認する手順です。",
        status: "published",
        importance: 2,
        newBadgeUntil: "9999-12-31",
        updatedBadgeUntil: null,
        isHidden: false,
        updatedAt: "2026-08-08T00:00:00Z",
      },
    ]);

    renderWithSettings(
      <MemoryRouter>
        <SearchPage />
      </MemoryRouter>,
    );

    expect(await screen.findByRole("heading", { name: "【Windows】画面が真っ暗になったとき" })).toBeInTheDocument();
    expect(screen.getByText("1件のFAQ")).toBeInTheDocument();
    expect(screen.getByText("公開")).toBeInTheDocument();
    expect(screen.getByText("新着")).toBeInTheDocument();
  });

  it("shows the stored title without a category prefix when the setting is off", async () => {
    vi.mocked(knowledgeApi.getSettings).mockResolvedValue({
      colorTheme: "green",
      showTopCategoryInTitle: false,
    });
    vi.spyOn(knowledgeApi, "listCategories").mockResolvedValue([
      { id: "cat", parentId: null, name: "Windows", description: "", depth: 1, sortOrder: 0, articleCount: 1 },
    ]);
    vi.spyOn(knowledgeApi, "searchArticles").mockResolvedValue([
      {
        id: "article",
        categoryId: "cat",
        categoryName: "Windows",
        title: "画面が真っ暗になったとき",
        summary: "画面表示を確認する手順です。",
        status: "published",
        importance: 2,
        newBadgeUntil: null,
        updatedBadgeUntil: null,
        isHidden: false,
        updatedAt: "2026-08-08T00:00:00Z",
      },
    ]);

    renderWithSettings(
      <MemoryRouter>
        <SearchPage />
      </MemoryRouter>,
    );

    expect(await screen.findByRole("heading", { name: "画面が真っ暗になったとき" })).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "【Windows】画面が真っ暗になったとき" })).not.toBeInTheDocument();
  });

  it("runs a keyword search only after the user submits it", async () => {
    vi.spyOn(knowledgeApi, "listCategories").mockResolvedValue([
      { id: "cat", parentId: null, name: "Windows", description: "", depth: 1, sortOrder: 0, articleCount: 0 },
    ]);
    const search = vi.spyOn(knowledgeApi, "searchArticles").mockResolvedValue([]);

    renderWithSettings(
      <MemoryRouter>
        <SearchPage />
      </MemoryRouter>,
    );
    await waitFor(() => expect(search).toHaveBeenCalledTimes(1));

    fireEvent.change(screen.getByRole("textbox", { name: "検索キーワード" }), {
      target: { value: "画面が真っ暗" },
    });
    expect(search).toHaveBeenCalledTimes(1);

    fireEvent.click(screen.getByRole("button", { name: "検索" }));
    await waitFor(() => expect(search).toHaveBeenCalledTimes(2));
    expect(search).toHaveBeenLastCalledWith(expect.objectContaining({ query: "画面が真っ暗" }));
  });

  it("filters by the selected category and explains that descendants are included", async () => {
    vi.spyOn(knowledgeApi, "listCategories").mockResolvedValue([
      { id: "windows", parentId: null, name: "Windows", description: "", depth: 1, sortOrder: 0, articleCount: 1 },
      { id: "display", parentId: "windows", name: "画面表示", description: "", depth: 2, sortOrder: 0, articleCount: 1 },
    ]);
    const search = vi.spyOn(knowledgeApi, "searchArticles").mockResolvedValue([]);

    renderWithSettings(
      <MemoryRouter>
        <SearchPage />
      </MemoryRouter>,
    );
    await waitFor(() => expect(search).toHaveBeenCalledTimes(1));

    fireEvent.click(screen.getByRole("button", { name: /Windows/ }));

    await waitFor(() => expect(search).toHaveBeenCalledTimes(2));
    expect(search).toHaveBeenLastCalledWith(expect.objectContaining({ categoryId: "windows" }));
    expect(screen.getAllByText("「Windows」以下")).toHaveLength(2);
    expect(screen.getByText("選んだ分類と、その配下にあるFAQを表示します。")).toBeInTheDocument();
  });

  it("restores the keyword and results after opening an FAQ and returning to the list", async () => {
    const category = {
      id: "cat",
      parentId: null,
      name: "Windows",
      description: "",
      depth: 1,
      sortOrder: 0,
      articleCount: 1,
    };
    const article = {
      id: "article",
      categoryId: "cat",
      categoryName: "Windows",
      title: "画面が真っ暗になったとき",
      summary: "画面表示を確認する手順です。",
      bodyDoc: { type: "doc" as const, content: [{ type: "paragraph", content: [{ type: "text", text: "回答" }] }] },
      bodyPlainText: "回答",
      status: "published" as const,
      importance: 2 as const,
      newBadgeUntil: null,
      updatedBadgeUntil: null,
      isHidden: false,
      createdAt: "2026-08-08T00:00:00Z",
      updatedAt: "2026-08-08T00:00:00Z",
      deletedAt: null,
      attachments: [],
    };
    vi.spyOn(knowledgeApi, "listCategories").mockResolvedValue([category]);
    const search = vi.spyOn(knowledgeApi, "searchArticles").mockImplementation(async ({ query }) => (
      query === "真っ暗" ? [article] : [article]
    ));
    vi.spyOn(knowledgeApi, "getArticle").mockResolvedValue(article);
    const scrollTo = vi.spyOn(window, "scrollTo").mockImplementation(() => undefined);
    Object.defineProperty(window, "scrollY", { configurable: true, value: 480 });

    renderWithSettings(
      <MemoryRouter initialEntries={["/search"]}>
        <Routes>
          <Route path="/search" element={<SearchPage />} />
          <Route path="/articles/:articleId" element={<ArticleDetailPage />} />
        </Routes>
      </MemoryRouter>,
    );

    const searchResultTitle = `【Windows】${article.title}`;
    await screen.findByRole("heading", { name: searchResultTitle });
    fireEvent.change(screen.getByRole("textbox", { name: "検索キーワード" }), {
      target: { value: "真っ暗" },
    });
    fireEvent.click(screen.getByRole("button", { name: "検索" }));
    await waitFor(() => expect(search).toHaveBeenLastCalledWith(expect.objectContaining({ query: "真っ暗" })));

    fireEvent.click(screen.getByRole("heading", { name: searchResultTitle }));
    expect(await screen.findByRole("link", { name: /一覧へ戻る/ })).toHaveAttribute("href", "/search?q=%E7%9C%9F%E3%81%A3%E6%9A%97");
    fireEvent.click(screen.getByRole("link", { name: /一覧へ戻る/ }));

    expect(await screen.findByRole("textbox", { name: "検索キーワード" })).toHaveValue("真っ暗");
    expect(await screen.findByText("「真っ暗」の検索結果")).toBeInTheDocument();
    await waitFor(() => expect(scrollTo).toHaveBeenCalledWith({ top: 480, behavior: "auto" }));
  });

  it("resets keyword, category, and draft visibility with the initialization button", async () => {
    vi.spyOn(knowledgeApi, "listCategories").mockResolvedValue([
      { id: "windows", parentId: null, name: "Windows", description: "", depth: 1, sortOrder: 0, articleCount: 0 },
    ]);
    const search = vi.spyOn(knowledgeApi, "searchArticles").mockResolvedValue([]);

    renderWithSettings(
      <MemoryRouter initialEntries={["/search?q=%E7%94%BB%E9%9D%A2&category=windows&drafts=1"]}>
        <SearchPage />
      </MemoryRouter>,
    );

    await waitFor(() => expect(search).toHaveBeenLastCalledWith({
      query: "画面",
      categoryId: "windows",
      includeDrafts: true,
    }));
    expect(screen.getByRole("textbox", { name: "検索キーワード" })).toHaveValue("画面");
    expect(screen.getByRole("checkbox", { name: "下書き・廃止も含める" })).toBeChecked();
    expect(screen.getByRole("button", { name: /Windows/ })).toHaveAttribute("aria-pressed", "true");

    fireEvent.click(screen.getByRole("button", { name: "条件を初期化" }));

    await waitFor(() => expect(search).toHaveBeenLastCalledWith({
      query: "",
      categoryId: undefined,
      includeDrafts: false,
    }));
    expect(screen.getByRole("textbox", { name: "検索キーワード" })).toHaveValue("");
    expect(screen.getByRole("checkbox", { name: "下書き・廃止も含める" })).not.toBeChecked();
    expect(screen.getByRole("button", { name: "条件を初期化" })).toBeDisabled();
  });
});
