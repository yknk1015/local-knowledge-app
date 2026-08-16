import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import type { ReactNode } from "react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { knowledgeApi } from "../api/knowledgeApi";
import { ColorThemeProvider } from "../app/ColorTheme";
import type { ArticleListItem, SearchArticlePage } from "../types/domain";
import { ArticleDetailPage } from "./ArticleDetailPage";
import { SearchPage } from "./SearchPage";

function renderWithSettings(children: ReactNode) {
  return render(<ColorThemeProvider>{children}</ColorThemeProvider>);
}

function searchPage(
  items: ArticleListItem[],
  total = items.length,
  page = 1,
): SearchArticlePage {
  return { items, total, page, pageSize: 50 };
}

function articleItem(id: string, title: string, categoryId = "cat", categoryName = "Windows"): ArticleListItem {
  return {
    id,
    categoryId,
    categoryName,
    title,
    summary: `${title}の概要`,
    status: "published",
    importance: 1,
    newBadgeUntil: null,
    updatedBadgeUntil: null,
    isHidden: false,
    updatedAt: "2026-08-08T00:00:00Z",
  };
}

describe("SearchPage", () => {
  beforeEach(() => {
    vi.spyOn(knowledgeApi, "getSettings").mockResolvedValue({
      colorTheme: "green",
      showTopCategoryInTitle: true,
      showMascot: true,
    });
    vi.spyOn(knowledgeApi, "saveSettings").mockImplementation(async (settings) => settings);
  });

  afterEach(() => {
    vi.restoreAllMocks();
    window.sessionStorage.clear();
  });

  it("guides a first-time user to create a category", async () => {
    vi.spyOn(knowledgeApi, "listCategories").mockResolvedValue([]);
    vi.spyOn(knowledgeApi, "searchArticles").mockResolvedValue(searchPage([]));

    renderWithSettings(
      <MemoryRouter>
        <SearchPage />
      </MemoryRouter>,
    );

    expect(await screen.findByRole("heading", { name: "最初の分類を作りましょう" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "FAQを探す" })).toBeInTheDocument();
    expect(screen.getByText("必要な情報をすばやく検索")).toBeInTheDocument();
    expect(screen.queryByText("FAQナレッジ")).not.toBeInTheDocument();
    expect(screen.getByRole("link", { name: "分類を作成する" })).toHaveAttribute("href", "/categories");
  });

  it("shows saved articles without rendering their body", async () => {
    vi.spyOn(knowledgeApi, "listCategories").mockResolvedValue([
      { id: "cat", parentId: null, name: "Windows", description: "", depth: 1, sortOrder: 0, articleCount: 1 },
      { id: "display", parentId: "cat", name: "画面表示", description: "", depth: 2, sortOrder: 0, articleCount: 1 },
    ]);
    vi.spyOn(knowledgeApi, "searchArticles").mockResolvedValue(searchPage([
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
    ]));

    renderWithSettings(
      <MemoryRouter>
        <SearchPage />
      </MemoryRouter>,
    );

    expect(await screen.findByRole("heading", { name: "【Windows】画面が真っ暗になったとき" })).toBeInTheDocument();
    expect(screen.getByText("1件のFAQ")).toBeInTheDocument();
    expect(screen.getByText("公開")).toBeInTheDocument();
    expect(screen.getByText("新着")).toBeInTheDocument();
    expect(screen.getByRole("list", { name: "FAQ検索結果" })).toBeInTheDocument();
    expect(screen.getAllByRole("listitem")).toHaveLength(1);
  });

  it("shows the stored title without a category prefix when the setting is off", async () => {
    vi.mocked(knowledgeApi.getSettings).mockResolvedValue({
      colorTheme: "green",
      showTopCategoryInTitle: false,
      showMascot: true,
    });
    vi.spyOn(knowledgeApi, "listCategories").mockResolvedValue([
      { id: "cat", parentId: null, name: "Windows", description: "", depth: 1, sortOrder: 0, articleCount: 1 },
    ]);
    vi.spyOn(knowledgeApi, "searchArticles").mockResolvedValue(searchPage([
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
    ]));

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
    const search = vi.spyOn(knowledgeApi, "searchArticles").mockResolvedValue(searchPage([]));

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
    const search = vi.spyOn(knowledgeApi, "searchArticles").mockResolvedValue(searchPage([]));

    renderWithSettings(
      <MemoryRouter>
        <SearchPage />
      </MemoryRouter>,
    );
    await waitFor(() => expect(search).toHaveBeenCalledTimes(1));

    fireEvent.click(screen.getByRole("button", { name: "Windows" }));

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
      mergeInfo: null,
      attachments: [],
    };
    vi.spyOn(knowledgeApi, "listCategories").mockResolvedValue([category]);
    const search = vi.spyOn(knowledgeApi, "searchArticles").mockImplementation(async () => searchPage([article]));
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
    const search = vi.spyOn(knowledgeApi, "searchArticles").mockResolvedValue(searchPage([]));

    renderWithSettings(
      <MemoryRouter initialEntries={["/search?q=%E7%94%BB%E9%9D%A2&category=windows&drafts=1"]}>
        <SearchPage />
      </MemoryRouter>,
    );

    await waitFor(() => expect(search).toHaveBeenLastCalledWith({
      query: "画面",
      categoryId: "windows",
      includeDrafts: true,
      page: 1,
      sort: "updatedDesc",
    }));
    expect(screen.getByRole("textbox", { name: "検索キーワード" })).toHaveValue("画面");
    expect(screen.getByRole("checkbox", { name: "下書き・廃止も含める" })).toBeChecked();
    expect(screen.getByRole("button", { name: "Windows" })).toHaveAttribute("aria-pressed", "true");

    fireEvent.click(screen.getByRole("button", { name: "条件を初期化" }));

    await waitFor(() => expect(search).toHaveBeenLastCalledWith({
      query: "",
      categoryId: undefined,
      includeDrafts: false,
      page: 1,
      sort: "updatedDesc",
    }));
    expect(screen.getByRole("textbox", { name: "検索キーワード" })).toHaveValue("");
    expect(screen.getByRole("checkbox", { name: "下書き・廃止も含める" })).not.toBeChecked();
    expect(screen.getByRole("button", { name: "条件を初期化" })).toBeDisabled();
  });

  it("moves through search results in pages of fifty", async () => {
    vi.spyOn(knowledgeApi, "listCategories").mockResolvedValue([
      { id: "cat", parentId: null, name: "Windows", description: "", depth: 1, sortOrder: 0, articleCount: 51 },
    ]);
    const firstArticle = articleItem("first", "1ページ目のFAQ");
    const secondArticle = articleItem("second", "2ページ目のFAQ");
    const search = vi.spyOn(knowledgeApi, "searchArticles").mockImplementation(async ({ page }) => (
      page === 2 ? searchPage([secondArticle], 51, 2) : searchPage([firstArticle], 51, 1)
    ));

    renderWithSettings(
      <MemoryRouter initialEntries={["/search"]}>
        <SearchPage />
      </MemoryRouter>,
    );

    expect(await screen.findByRole("heading", { name: "【Windows】1ページ目のFAQ" })).toBeInTheDocument();
    expect(screen.getByText("51件のFAQ")).toBeInTheDocument();
    expect(screen.getByText("1〜50件 / 全51件")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "2ページ目" }));

    expect(await screen.findByRole("heading", { name: "【Windows】2ページ目のFAQ" })).toBeInTheDocument();
    await waitFor(() => expect(search).toHaveBeenLastCalledWith(expect.objectContaining({ page: 2 })));
    expect(screen.getByRole("button", { name: "2ページ目" })).toHaveAttribute("aria-current", "page");
    expect(screen.getByText("51〜51件 / 全51件")).toBeInTheDocument();
  });

  it("collapses category branches individually and all at once", async () => {
    vi.spyOn(knowledgeApi, "listCategories").mockResolvedValue([
      { id: "parent", parentId: null, name: "親分類", description: "", depth: 1, sortOrder: 0, articleCount: 0 },
      { id: "child", parentId: "parent", name: "子分類", description: "", depth: 2, sortOrder: 0, articleCount: 0 },
      { id: "grandchild", parentId: "child", name: "孫分類", description: "", depth: 3, sortOrder: 0, articleCount: 0 },
      { id: "other", parentId: null, name: "別分類", description: "", depth: 1, sortOrder: 1, articleCount: 0 },
    ]);
    vi.spyOn(knowledgeApi, "searchArticles").mockResolvedValue(searchPage([]));

    renderWithSettings(
      <MemoryRouter>
        <SearchPage />
      </MemoryRouter>,
    );

    expect(await screen.findByRole("button", { name: "孫分類" })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "親分類の下位分類を閉じる" }));
    expect(screen.queryByRole("button", { name: "子分類" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "別分類" })).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "親分類の下位分類を開く" }));
    expect(screen.getByRole("button", { name: "子分類" })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "すべて閉じる" }));
    expect(screen.queryByRole("button", { name: "子分類" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "すべて開く" })).toBeEnabled();
  });

  it("sorts all search results by the selected update date or importance order", async () => {
    vi.spyOn(knowledgeApi, "listCategories").mockResolvedValue([
      { id: "cat", parentId: null, name: "Windows", description: "", depth: 1, sortOrder: 0, articleCount: 2 },
    ]);
    const recentlyUpdated = articleItem("recent", "最近更新したFAQ");
    const important = { ...articleItem("important", "重要なFAQ"), importance: 3 };
    const search = vi.spyOn(knowledgeApi, "searchArticles").mockImplementation(async ({ sort }) => (
      sort === "importanceDesc"
        ? searchPage([important, recentlyUpdated])
        : searchPage([recentlyUpdated, important])
    ));

    renderWithSettings(
      <MemoryRouter>
        <SearchPage />
      </MemoryRouter>,
    );

    const sortSelect = await screen.findByRole("combobox", { name: "検索結果の並び替え" });
    expect(sortSelect).toHaveValue("updatedDesc");
    expect(within(screen.getAllByRole("listitem")[0]!).getByRole("heading", { name: "【Windows】最近更新したFAQ" })).toBeInTheDocument();

    fireEvent.change(sortSelect, { target: { value: "importanceDesc" } });

    await waitFor(() => expect(search).toHaveBeenLastCalledWith(expect.objectContaining({
      page: 1,
      sort: "importanceDesc",
    })));
    expect(within(screen.getAllByRole("listitem")[0]!).getByRole("heading", { name: "【Windows】重要なFAQ" })).toBeInTheDocument();
  });
});
