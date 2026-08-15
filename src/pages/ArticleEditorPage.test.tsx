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
        description: "",
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

  it("explains when a selected display flag has no end date", async () => {
    Object.defineProperty(HTMLElement.prototype, "scrollIntoView", {
      configurable: true,
      value: vi.fn(),
    });
    vi.spyOn(knowledgeApi, "listCategories").mockResolvedValue([
      { id: "category-1", parentId: null, name: "操作全般", description: "", depth: 1, sortOrder: 0, articleCount: 0 },
    ]);
    const save = vi.spyOn(knowledgeApi, "saveArticle");

    render(
      <MemoryRouter initialEntries={["/articles/new"]}>
        <Routes>
          <Route path="/articles/new" element={<ArticleEditorPage />} />
        </Routes>
      </MemoryRouter>,
    );

    fireEvent.change(await screen.findByLabelText(/タイトル/), { target: { value: "新しいFAQ" } });
    fireEvent.click(screen.getByRole("checkbox", { name: /「新着」を表示する/ }));
    fireEvent.click(screen.getByRole("button", { name: "下書きを保存" }));

    expect(await screen.findAllByText("新着フラグの表示終了日を選択してください。")).toHaveLength(2);
    expect(save).not.toHaveBeenCalled();
  });

  it("shows the missing public fields both at the top and beside each field", async () => {
    Object.defineProperty(HTMLElement.prototype, "scrollIntoView", {
      configurable: true,
      value: vi.fn(),
    });
    vi.spyOn(knowledgeApi, "listCategories").mockResolvedValue([
      { id: "category-1", parentId: null, name: "操作全般", description: "", depth: 1, sortOrder: 0, articleCount: 0 },
    ]);
    const save = vi.spyOn(knowledgeApi, "saveArticle");

    render(
      <MemoryRouter initialEntries={["/articles/new"]}>
        <Routes>
          <Route path="/articles/new" element={<ArticleEditorPage />} />
        </Routes>
      </MemoryRouter>,
    );

    fireEvent.change(await screen.findByLabelText(/タイトル/), { target: { value: "公開するFAQ" } });
    fireEvent.click(screen.getByRole("radio", { name: /^公開/ }));
    fireEvent.click(screen.getByRole("button", { name: "FAQを保存" }));

    expect(await screen.findAllByText("公開する場合は概要を入力してください。")).toHaveLength(2);
    expect(screen.getAllByText("公開する場合は回答を入力してください。")).toHaveLength(2);
    expect(screen.getByLabelText(/概要/)).toHaveAttribute("aria-invalid", "true");
    expect(save).not.toHaveBeenCalled();
  });

  it("saves the current draft with Ctrl+S", async () => {
    Object.defineProperty(HTMLElement.prototype, "scrollIntoView", {
      configurable: true,
      value: vi.fn(),
    });
    vi.spyOn(knowledgeApi, "listCategories").mockResolvedValue([
      { id: "category-1", parentId: null, name: "操作全般", description: "", depth: 1, sortOrder: 0, articleCount: 0 },
    ]);
    const save = vi.spyOn(knowledgeApi, "saveArticle").mockRejectedValue({
      code: "ART-005",
      message: "確認用の保存エラーです。",
      action: "入力内容を確認してください。",
    });

    render(
      <MemoryRouter initialEntries={["/articles/new"]}>
        <Routes>
          <Route path="/articles/new" element={<ArticleEditorPage />} />
        </Routes>
      </MemoryRouter>,
    );

    fireEvent.change(await screen.findByLabelText(/タイトル/), { target: { value: "ショートカットで保存" } });
    fireEvent.keyDown(window, { key: "s", ctrlKey: true });

    await waitFor(() => expect(save).toHaveBeenCalledWith(expect.objectContaining({
      title: "ショートカットで保存",
      status: "draft",
    })));
  });
});
