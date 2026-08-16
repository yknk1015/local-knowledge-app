import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { ArticleManagementPage } from "./ArticleManagementPage";

const mocks = vi.hoisted(() => ({
  listCategories: vi.fn(),
  listArticlesForManagement: vi.fn(),
  deleteArticle: vi.fn(),
  restoreArticle: vi.fn(),
  duplicateArticle: vi.fn(),
  createCodexDelegation: vi.fn(),
  clearArticleMerge: vi.fn(),
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
  newBadgeUntil: null,
  updatedBadgeUntil: null,
  isHidden: false,
  updatedAt: "2026-08-08T12:00:00Z",
  createdByDisplayName: "作成担当",
  updatedByDisplayName: "更新担当",
  deletedAt: null,
  mergeInfo: null,
};

describe("ArticleManagementPage", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mocks.listCategories.mockResolvedValue([
      { id: "category-1", parentId: null, name: "Windows", description: "", depth: 1, sortOrder: 0, articleCount: 1 },
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
    expect(screen.getByRole("button", { name: "通常" })).toHaveAttribute("aria-pressed", "true");
    expect(screen.queryByTitle("作成担当")).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "詳細" }));
    expect(screen.getByTitle("作成担当")).toBeVisible();
    expect(screen.getByTitle("更新担当")).toBeVisible();
    expect(screen.getByRole("columnheader", { name: "作成者・更新者" })).toBeVisible();
    fireEvent.click(screen.getByRole("button", { name: "「画面が暗い」のその他の操作" }));
    fireEvent.click(screen.getByRole("menuitem", { name: "削除" }));

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

  it("delegates two selected FAQs to Codex for a non-destructive merge", async () => {
    const second = { ...article, id: "article-2", title: "画面が明るすぎる" };
    mocks.listArticlesForManagement.mockResolvedValue({ items: [article, second], total: 2, page: 1, pageSize: 50 });
    mocks.createCodexDelegation.mockResolvedValue({
      delegationId: "delegation-id",
      prompt: "KnowledgeAppの委譲番号 delegation-id のFAQを統合してください。",
      filePath: "C:\\local\\delegation.json",
    });
    render(<MemoryRouter><ArticleManagementPage /></MemoryRouter>);

    fireEvent.click(await screen.findByRole("checkbox", { name: /「画面が暗い」/ }));
    fireEvent.click(screen.getByRole("checkbox", { name: /「画面が明るすぎる」/ }));
    fireEvent.click(screen.getByRole("button", { name: "選択中の2件をCodexへ委譲" }));

    await waitFor(() => expect(mocks.createCodexDelegation).toHaveBeenCalledWith("merge", ["article-1", "article-2"]));
    expect(await screen.findByText(/KnowledgeAppの委譲番号 delegation-id/)).toBeInTheDocument();
  });

  it("shows the integrated target, prevents re-selection, and can clear the relation", async () => {
    const mergedSource = {
      ...article,
      mergeInfo: {
        targetArticleId: "target-article",
        targetArticleTitle: "統合先FAQ",
        mergedAt: "2026-08-15T01:00:00Z",
      },
    };
    mocks.listArticlesForManagement
      .mockResolvedValueOnce({ items: [mergedSource], total: 1, page: 1, pageSize: 50 })
      .mockResolvedValueOnce({ items: [article], total: 1, page: 1, pageSize: 50 });
    mocks.clearArticleMerge.mockResolvedValue(article);
    render(<MemoryRouter><ArticleManagementPage /></MemoryRouter>);

    expect(await screen.findByText("統合済み")).toBeVisible();
    expect(screen.getByText("統合先FAQ")).toBeVisible();
    expect(screen.getByRole("checkbox", { name: /「画面が暗い」/ })).toBeDisabled();
    fireEvent.click(screen.getByRole("button", { name: "「画面が暗い」のその他の操作" }));
    fireEvent.click(screen.getByRole("menuitem", { name: "統合を解除" }));

    await waitFor(() => expect(mocks.clearArticleMerge).toHaveBeenCalledWith("article-1"));
    expect(await screen.findByText("「画面が暗い」の統合済み設定を解除しました。")).toBeVisible();
  });
});
