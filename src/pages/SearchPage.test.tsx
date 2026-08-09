import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { knowledgeApi } from "../api/knowledgeApi";
import { SearchPage } from "./SearchPage";

describe("SearchPage", () => {
  afterEach(() => vi.restoreAllMocks());

  it("guides a first-time user to create a category", async () => {
    vi.spyOn(knowledgeApi, "listCategories").mockResolvedValue([]);
    vi.spyOn(knowledgeApi, "searchArticles").mockResolvedValue([]);

    render(
      <MemoryRouter>
        <SearchPage />
      </MemoryRouter>,
    );

    expect(await screen.findByRole("heading", { name: "最初の分類を作りましょう" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "分類を作成する" })).toHaveAttribute("href", "/categories");
  });

  it("shows saved articles without rendering their body", async () => {
    vi.spyOn(knowledgeApi, "listCategories").mockResolvedValue([
      { id: "cat", parentId: null, name: "Windows", depth: 1, sortOrder: 0, articleCount: 1 },
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
        newBadgeUntil: "9999-12-31",
        updatedBadgeUntil: null,
        isHidden: false,
        updatedAt: "2026-08-08T00:00:00Z",
      },
    ]);

    render(
      <MemoryRouter>
        <SearchPage />
      </MemoryRouter>,
    );

    expect(await screen.findByRole("heading", { name: "画面が真っ暗になったとき" })).toBeInTheDocument();
    expect(screen.getByText("1件のFAQ")).toBeInTheDocument();
    expect(screen.getByText("公開")).toBeInTheDocument();
    expect(screen.getByText("新着")).toBeInTheDocument();
  });

  it("runs a keyword search only after the user submits it", async () => {
    vi.spyOn(knowledgeApi, "listCategories").mockResolvedValue([
      { id: "cat", parentId: null, name: "Windows", depth: 1, sortOrder: 0, articleCount: 0 },
    ]);
    const search = vi.spyOn(knowledgeApi, "searchArticles").mockResolvedValue([]);

    render(
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
});
