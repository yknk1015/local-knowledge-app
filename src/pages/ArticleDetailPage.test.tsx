import { fireEvent, render, screen } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { knowledgeApi } from "../api/knowledgeApi";
import type { Article } from "../types/domain";
import { ArticleDetailPage } from "./ArticleDetailPage";

vi.mock("../components/RichTextEditor", () => ({
  RichTextViewer: () => <div>回答本文</div>,
}));

function article(overrides: Partial<Article> = {}): Article {
  return {
    id: "article-1",
    categoryId: "category-1",
    categoryName: "PC",
    title: "画面が暗いときは？",
    summary: "表示先を確認します。",
    bodyDoc: { type: "doc", content: [{ type: "paragraph" }] },
    bodyPlainText: "回答",
    status: "published",
    importance: 1,
    newBadgeUntil: null,
    updatedBadgeUntil: null,
    isHidden: false,
    createdAt: "2026-08-16T00:00:00Z",
    updatedAt: "2026-08-16T00:00:00Z",
    deletedAt: null,
    mergeInfo: null,
    attachments: [],
    symptoms: ["画面が真っ暗"],
    causes: ["表示先の切替"],
    targets: ["Windows 11"],
    errorCodes: ["0x80070005"],
    procedures: ["Windowsキーを押す"],
    cautions: ["未保存ファイルを閉じる"],
    tags: ["ディスプレイ"],
    searchTerms: ["ブラックスクリーン"],
    relatedArticles: [{
      id: "article-2",
      title: "ネットワークを確認するには？",
      status: "published",
      deletedAt: null,
      isMerged: true,
    }],
    ...overrides,
  };
}

describe("ArticleDetailPage", () => {
  afterEach(() => vi.restoreAllMocks());

  it("shows search details in the designed order and opens a related FAQ", async () => {
    vi.spyOn(knowledgeApi, "recordArticleView").mockResolvedValue(undefined);
    const related = article({
      id: "article-2",
      title: "ネットワークを確認するには？",
      symptoms: [],
      causes: [],
      targets: [],
      errorCodes: [],
      procedures: [],
      cautions: [],
      tags: [],
      searchTerms: [],
      relatedArticles: [],
    });
    const getArticle = vi.spyOn(knowledgeApi, "getArticle").mockImplementation(async (id) => (
      id === related.id ? related : article()
    ));
    const router = createMemoryRouter([
      { path: "/articles/:articleId", element: <ArticleDetailPage /> },
    ], { initialEntries: ["/articles/article-1"] });
    render(<RouterProvider router={router} />);

    expect(await screen.findByRole("heading", { name: "画面が暗いときは？" })).toBeVisible();
    expect(knowledgeApi.recordArticleView).toHaveBeenCalledWith("article-1", undefined);
    const labels = ["症状", "想定原因", "対象OS・製品・機種", "エラーコード", "対応手順", "注意事項", "タグ"];
    const headings = labels.map((label) => screen.getByRole("heading", { name: label }));
    for (let index = 0; index < headings.length - 1; index += 1) {
      expect(headings[index]!.compareDocumentPosition(headings[index + 1]!) & Node.DOCUMENT_POSITION_FOLLOWING).not.toBe(0);
    }
    expect(screen.getByText("画面が真っ暗")).toBeVisible();
    expect(screen.getByText("統合済み")).toBeVisible();

    fireEvent.click(screen.getByRole("link", { name: /ネットワークを確認するには/ }));
    expect(await screen.findByRole("heading", { name: "ネットワークを確認するには？" })).toBeVisible();
    expect(getArticle).toHaveBeenLastCalledWith("article-2");
  });
});
