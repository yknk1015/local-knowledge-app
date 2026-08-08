import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { ArticleManagementPage } from "./ArticleManagementPage";

const mocks = vi.hoisted(() => ({
  listCategories: vi.fn(),
  listArticlesForManagement: vi.fn(),
  deleteArticle: vi.fn(),
  restoreArticle: vi.fn(),
}));

vi.mock("../api/knowledgeApi", async (importOriginal) => {
  const original = await importOriginal<typeof import("../api/knowledgeApi")>();
  return {
    ...original,
    knowledgeApi: mocks,
  };
});

const article = {
  id: "article-1",
  categoryId: "category-1",
  categoryName: "Windows",
  title: "画面が暗い",
  summary: "画面設定を確認します",
  status: "published" as const,
  importance: 1,
  updatedAt: "2026-08-08T12:00:00Z",
  deletedAt: null,
};

describe("ArticleManagementPage", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mocks.listCategories.mockResolvedValue([
      { id: "category-1", parentId: null, name: "Windows", depth: 1, sortOrder: 0, articleCount: 1 },
    ]);
    vi.spyOn(window, "confirm").mockReturnValue(true);
  });

  it("moves an active FAQ to deleted items after confirmation", async () => {
    mocks.listArticlesForManagement
      .mockResolvedValueOnce({ items: [article], total: 1, page: 1, pageSize: 50 })
      .mockResolvedValueOnce({ items: [], total: 0, page: 1, pageSize: 50 });
    mocks.deleteArticle.mockResolvedValue({ ...article, deletedAt: "2026-08-08T13:00:00Z" });
    render(<MemoryRouter><ArticleManagementPage /></MemoryRouter>);

    expect(await screen.findByText("画面が暗い")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "削除" }));

    await waitFor(() => expect(mocks.deleteArticle).toHaveBeenCalledWith("article-1"));
    expect(window.confirm).toHaveBeenCalled();
    expect(await screen.findByText("「画面が暗い」を削除済みに移動しました。")).toBeInTheDocument();
  });

  it("lists deleted FAQs separately and restores them", async () => {
    mocks.listArticlesForManagement.mockImplementation(async (input: { deleted: boolean }) => ({
      items: input.deleted ? [{ ...article, deletedAt: "2026-08-08T13:00:00Z" }] : [],
      total: input.deleted ? 1 : 0,
      page: 1,
      pageSize: 50,
    }));
    mocks.restoreArticle.mockResolvedValue(article);
    render(<MemoryRouter><ArticleManagementPage /></MemoryRouter>);

    await screen.findByText("条件に一致するFAQがありません");
    fireEvent.click(screen.getByRole("button", { name: "削除済み" }));
    expect(await screen.findByText("画面が暗い")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "復元" }));

    await waitFor(() => expect(mocks.restoreArticle).toHaveBeenCalledWith("article-1"));
    expect(await screen.findByText("「画面が暗い」を復元しました。")).toBeInTheDocument();
  });
});
