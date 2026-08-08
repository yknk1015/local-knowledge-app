import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { knowledgeApi } from "../api/knowledgeApi";
import { ArticleEditorPage } from "./ArticleEditorPage";

vi.mock("../components/RichTextEditor", () => ({
  RichTextEditor: () => <div aria-label="FAQの回答" />,
}));

describe("ArticleEditorPage", () => {
  afterEach(() => vi.restoreAllMocks());

  it("moves to the failure reason when saving is rejected", async () => {
    const scrollIntoView = vi.fn();
    Object.defineProperty(HTMLElement.prototype, "scrollIntoView", {
      configurable: true,
      value: scrollIntoView,
    });
    vi.spyOn(knowledgeApi, "listCategories").mockResolvedValue([
      {
        id: "category-1",
        parentId: null,
        name: "操作全般",
        depth: 1,
        sortOrder: 0,
        articleCount: 0,
      },
    ]);
    vi.spyOn(knowledgeApi, "saveArticle").mockRejectedValue({
      code: "ART-005",
      message: "クリック可能な参考URLを確認してください。",
      action: "URLを文字として保存する場合はリンク解除を押してください。",
    });

    render(
      <MemoryRouter initialEntries={["/articles/new"]}>
        <Routes>
          <Route path="/articles/new" element={<ArticleEditorPage />} />
        </Routes>
      </MemoryRouter>,
    );

    fireEvent.change(await screen.findByLabelText(/タイトル/), {
      target: { value: "アプリの起動方法" },
    });
    fireEvent.click(screen.getByRole("button", { name: "下書きを保存" }));

    expect(await screen.findByText("クリック可能な参考URLを確認してください。")).toBeVisible();
    expect(screen.getByText(/^URLを文字として保存する場合はリンク解除/)).toBeVisible();
    await waitFor(() => expect(scrollIntoView).toHaveBeenCalledWith({
      behavior: "smooth",
      block: "start",
    }));
  });
});
