import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { knowledgeApi } from "../api/knowledgeApi";
import { ArticleEditorPage, getDefaultBadgeUntil } from "./ArticleEditorPage";

vi.mock("../components/RichTextEditor", () => ({
  RichTextEditor: () => <div aria-label="FAQの回答" />,
}));

function renderEditor(initialEntry = "/articles/new") {
  const router = createMemoryRouter([
    { path: "/articles/new", element: <ArticleEditorPage /> },
    { path: "/articles/:articleId/edit", element: <ArticleEditorPage /> },
    { path: "/articles/:articleId", element: <div>保存完了</div> },
    { path: "/search", element: <div>検索画面</div> },
    { path: "/tags", element: <div>タグマスター</div> },
  ], { initialEntries: [initialEntry] });
  return { ...render(<RouterProvider router={router} />), router };
}

describe("ArticleEditorPage", () => {
  beforeEach(() => {
    vi.spyOn(knowledgeApi, "listTags").mockResolvedValue([]);
  });

  afterEach(() => vi.restoreAllMocks());

  it("calculates the default display end date using the local calendar", () => {
    expect(getDefaultBadgeUntil(new Date(2026, 11, 28, 23, 30))).toBe("2027-01-04");
  });

  it("shows the security reminder on the FAQ creation screen", async () => {
    vi.spyOn(knowledgeApi, "listCategories").mockResolvedValue([
      { id: "category-1", parentId: null, name: "操作全般", description: "", depth: 1, sortOrder: 0, articleCount: 0 },
    ]);

    renderEditor();

    expect(await screen.findByText("安全のために、パスワードや秘密鍵、個人情報はFAQへ登録しないでください。")).toBeVisible();
    expect(screen.getByText("最初に結論を簡潔に示し、その後に手順や詳細を記載すると、読み手に伝わりやすくなります。")).toBeVisible();
    expect(screen.queryByText("見出しや番号付きリストを使い、利用者が上から順番に実行できるように整理します。")).not.toBeInTheDocument();
  });

  it("visually separates the basic information fields and emphasizes required items", async () => {
    vi.spyOn(knowledgeApi, "listCategories").mockResolvedValue([
      { id: "category-1", parentId: null, name: "操作全般", description: "", depth: 1, sortOrder: 0, articleCount: 0 },
    ]);

    renderEditor();

    const titleCard = (await screen.findByLabelText(/タイトル/)).closest(".basic-info-card");
    const categoryCard = screen.getByLabelText(/所属分類/).closest(".basic-info-card");
    const importanceCard = screen.getByLabelText(/重要度/).closest(".basic-info-card");
    const summaryCard = screen.getByLabelText(/概要/).closest(".basic-info-card");

    expect(titleCard).toHaveClass("required-basic-field");
    expect(categoryCard).toHaveClass("required-basic-field");
    expect(titleCard).toHaveClass("unfilled");
    expect(categoryCard).toHaveClass("unfilled");
    expect(screen.getByLabelText(/所属分類/)).toHaveValue("");
    expect(importanceCard).toHaveClass("optional-basic-field");
    expect(summaryCard).toHaveClass("conditional-basic-field");
    expect(screen.getByText("任意")).toBeVisible();
    expect(summaryCard?.querySelector(".required")).toHaveClass("conditional-required");

    fireEvent.click(screen.getByRole("radio", { name: /^公開/ }));
    expect(summaryCard).toHaveClass("required-basic-field");
    expect(summaryCard?.querySelector(".required")).not.toHaveClass("conditional-required");
  });

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

    renderEditor();

    fireEvent.change(await screen.findByLabelText(/タイトル/), {
      target: { value: "アプリの起動方法" },
    });
    fireEvent.change(screen.getByLabelText(/所属分類/), { target: { value: "category-1" } });
    fireEvent.click(screen.getByRole("button", { name: "下書きを保存" }));

    expect(await screen.findByText("クリック可能な参考URLを確認してください。")).toBeVisible();
    expect(screen.getByText(/^URLを文字として保存する場合はリンク解除/)).toBeVisible();
    await waitFor(() => expect(scrollIntoView).toHaveBeenCalledWith({
      behavior: "smooth",
      block: "start",
    }));
  });

  it("sets one week from today as the default end date for new and updated badges", async () => {
    Object.defineProperty(HTMLElement.prototype, "scrollIntoView", {
      configurable: true,
      value: vi.fn(),
    });
    vi.spyOn(knowledgeApi, "listCategories").mockResolvedValue([
      { id: "category-1", parentId: null, name: "操作全般", description: "", depth: 1, sortOrder: 0, articleCount: 0 },
    ]);
    renderEditor();

    await screen.findByLabelText(/タイトル/);
    fireEvent.click(screen.getByRole("checkbox", { name: /「新着」を表示する/ }));
    fireEvent.click(screen.getByRole("checkbox", { name: /「更新」を表示する/ }));

    const expected = getDefaultBadgeUntil();
    expect(screen.getByLabelText(/「新着」を表示する/).closest(".display-setting-card")?.querySelector("input[type='date']")).toHaveValue(expected);
    expect(screen.getByLabelText(/「更新」を表示する/).closest(".display-setting-card")?.querySelector("input[type='date']")).toHaveValue(expected);
  });

  it("keeps an entered end date when a display flag is enabled again", async () => {
    vi.spyOn(knowledgeApi, "listCategories").mockResolvedValue([
      { id: "category-1", parentId: null, name: "操作全般", description: "", depth: 1, sortOrder: 0, articleCount: 0 },
    ]);

    renderEditor();

    await screen.findByLabelText(/タイトル/);
    const checkbox = screen.getByRole("checkbox", { name: /「新着」を表示する/ });
    fireEvent.click(checkbox);
    const dateInput = checkbox.closest(".display-setting-card")?.querySelector("input[type='date']") as HTMLInputElement;
    fireEvent.change(dateInput, { target: { value: "2030-05-20" } });
    fireEvent.click(checkbox);
    fireEvent.click(checkbox);

    expect(dateInput).toHaveValue("2030-05-20");
  });

  it("still requires an end date when the default value is cleared", async () => {
    Object.defineProperty(HTMLElement.prototype, "scrollIntoView", {
      configurable: true,
      value: vi.fn(),
    });
    vi.spyOn(knowledgeApi, "listCategories").mockResolvedValue([
      { id: "category-1", parentId: null, name: "操作全般", description: "", depth: 1, sortOrder: 0, articleCount: 0 },
    ]);
    const save = vi.spyOn(knowledgeApi, "saveArticle");

    renderEditor();

    fireEvent.change(await screen.findByLabelText(/タイトル/), { target: { value: "新しいFAQ" } });
    fireEvent.change(screen.getByLabelText(/所属分類/), { target: { value: "category-1" } });
    const checkbox = screen.getByRole("checkbox", { name: /「新着」を表示する/ });
    fireEvent.click(checkbox);
    const dateInput = checkbox.closest(".display-setting-card")?.querySelector("input[type='date']") as HTMLInputElement;
    fireEvent.change(dateInput, { target: { value: "" } });
    fireEvent.click(screen.getByRole("button", { name: "下書きを保存" }));

    expect(await screen.findAllByText("新着フラグの表示終了日を選択してください。")).toHaveLength(2);
    expect(save).not.toHaveBeenCalled();
  });

  it("does not show developer-oriented guidance below the answer editor", async () => {
    vi.spyOn(knowledgeApi, "listCategories").mockResolvedValue([
      { id: "category-1", parentId: null, name: "操作全般", description: "", depth: 1, sortOrder: 0, articleCount: 0 },
    ]);

    renderEditor();

    await screen.findByLabelText(/タイトル/);
    expect(screen.queryByText(/画像はこのPCのアプリ管理フォルダに保存されます/)).not.toBeInTheDocument();
    expect(screen.queryByText(/貼り付けただけではクリックされません/)).not.toBeInTheDocument();
    expect(screen.queryByText(/詳細画面で正確にコピーできる枠になります/)).not.toBeInTheDocument();
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

    renderEditor();

    fireEvent.change(await screen.findByLabelText(/タイトル/), { target: { value: "公開するFAQ" } });
    fireEvent.change(screen.getByLabelText(/所属分類/), { target: { value: "category-1" } });
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

    renderEditor();

    fireEvent.change(await screen.findByLabelText(/タイトル/), { target: { value: "ショートカットで保存" } });
    fireEvent.change(screen.getByLabelText(/所属分類/), { target: { value: "category-1" } });
    fireEvent.keyDown(window, { key: "s", ctrlKey: true });

    await waitFor(() => expect(save).toHaveBeenCalledWith(expect.objectContaining({
      title: "ショートカットで保存",
      status: "draft",
    })));
  });

  it("removes search and related FAQ inputs and saves tags selected from the master", async () => {
    vi.spyOn(knowledgeApi, "listCategories").mockResolvedValue([
      { id: "category-1", parentId: null, name: "操作全般", description: "", depth: 1, sortOrder: 0, articleCount: 0 },
    ]);
    vi.mocked(knowledgeApi.listTags).mockResolvedValue([
      { id: "tag-1", name: "ディスプレイ", usageCount: 3, updatedAt: "2026-08-16T00:00:00Z" },
      { id: "tag-2", name: "Windows 11", usageCount: 5, updatedAt: "2026-08-16T00:00:00Z" },
    ]);
    const save = vi.spyOn(knowledgeApi, "saveArticle").mockRejectedValue({
      code: "TEST-001",
      message: "入力内容確認用の停止です。",
      action: "テストを確認してください。",
    });
    renderEditor();

    fireEvent.change(await screen.findByLabelText(/タイトル/), { target: { value: "画面が暗いときは？" } });
    fireEvent.change(screen.getByLabelText(/所属分類/), { target: { value: "category-1" } });
    expect(screen.queryByRole("heading", { name: "検索情報" })).not.toBeInTheDocument();
    expect(screen.queryByLabelText("症状")).not.toBeInTheDocument();
    expect(screen.queryByLabelText("関連FAQを検索")).not.toBeInTheDocument();
    fireEvent.change(screen.getByLabelText("タグマスターから選択"), { target: { value: "ディスプレイ" } });
    fireEvent.click(screen.getByRole("button", { name: "タグを追加" }));
    expect(screen.getByRole("list", { name: "選択中のタグ" })).toHaveTextContent("ディスプレイ");
    fireEvent.click(screen.getByRole("button", { name: "下書きを保存" }));

    await waitFor(() => expect(save).toHaveBeenCalledWith(expect.objectContaining({
      symptoms: [],
      tags: ["ディスプレイ"],
      relatedArticleIds: [],
    })));
  });

  it("preserves hidden legacy search values and related FAQ links when editing", async () => {
    const category = { id: "category-1", parentId: null, name: "操作全般", description: "", depth: 1, sortOrder: 0, articleCount: 1 };
    const article = {
      id: "article-1",
      categoryId: category.id,
      categoryName: category.name,
      title: "既存FAQ",
      summary: "既存の概要",
      bodyDoc: { type: "doc", content: [{ type: "paragraph", content: [{ type: "text", text: "回答" }] }] },
      bodyPlainText: "回答",
      status: "draft" as const,
      importance: 1,
      newBadgeUntil: null,
      updatedBadgeUntil: null,
      isHidden: false,
      createdAt: "2026-08-15T00:00:00Z",
      updatedAt: "2026-08-15T00:00:00Z",
      deletedAt: null,
      mergeInfo: null,
      attachments: [],
      symptoms: ["画面が暗い"],
      causes: ["表示設定"],
      targets: ["Windows 11"],
      errorCodes: ["E001"],
      procedures: ["表示先を確認"],
      cautions: ["作業中のファイルを保存"],
      tags: ["ディスプレイ"],
      searchTerms: ["ブラックスクリーン"],
      relatedArticles: [{ id: "related-1", title: "関連FAQ", status: "published" as const, deletedAt: null, isMerged: false }],
    };
    vi.spyOn(knowledgeApi, "listCategories").mockResolvedValue([category]);
    vi.mocked(knowledgeApi.listTags).mockResolvedValue([
      { id: "tag-1", name: "ディスプレイ", usageCount: 1, updatedAt: "2026-08-16T00:00:00Z" },
    ]);
    vi.spyOn(knowledgeApi, "getArticle").mockResolvedValue(article);
    vi.spyOn(knowledgeApi, "getCodexMergePublicationContext").mockResolvedValue(null);
    const save = vi.spyOn(knowledgeApi, "saveArticle").mockRejectedValue({ code: "TEST-001", message: "確認停止", action: "テストを確認" });

    renderEditor(`/articles/${article.id}/edit`);
    fireEvent.change(await screen.findByLabelText(/概要/), { target: { value: "更新後の概要" } });
    fireEvent.click(screen.getByRole("button", { name: "下書きを保存" }));

    await waitFor(() => expect(save).toHaveBeenCalledWith(expect.objectContaining({
      symptoms: article.symptoms,
      causes: article.causes,
      targets: article.targets,
      errorCodes: article.errorCodes,
      procedures: article.procedures,
      cautions: article.cautions,
      searchTerms: article.searchTerms,
      relatedArticleIds: ["related-1"],
    })));
  });

  it("warns before route navigation or window closing when changes are unsaved", async () => {
    vi.spyOn(knowledgeApi, "listCategories").mockResolvedValue([
      { id: "category-1", parentId: null, name: "操作全般", description: "", depth: 1, sortOrder: 0, articleCount: 0 },
    ]);
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(false);
    const { router } = renderEditor();
    fireEvent.change(await screen.findByLabelText(/タイトル/), { target: { value: "未保存のFAQ" } });

    const beforeUnload = new Event("beforeunload", { cancelable: true });
    window.dispatchEvent(beforeUnload);
    expect(beforeUnload.defaultPrevented).toBe(true);

    fireEvent.click(screen.getByRole("link", { name: /編集をキャンセルして戻る/ }));
    await waitFor(() => expect(confirm).toHaveBeenCalledWith(expect.stringContaining("保存していない変更")));
    expect(router.state.location.pathname).toBe("/articles/new");

    confirm.mockReturnValue(true);
    fireEvent.click(screen.getByRole("link", { name: /編集をキャンセルして戻る/ }));
    await waitFor(() => expect(router.state.location.pathname).toBe("/search"));
  });

  it("publishes a merged FAQ as new and then confirms marking its sources as merged", async () => {
    const category = {
      id: "category-1",
      parentId: null,
      name: "操作全般",
      description: "",
      depth: 1,
      sortOrder: 0,
      articleCount: 3,
    };
    const mergedDraft = {
      id: "merged-article",
      categoryId: category.id,
      categoryName: category.name,
      title: "統合した質問",
      summary: "統合した回答の概要です。",
      bodyDoc: { type: "doc", content: [{ type: "paragraph", content: [{ type: "text", text: "回答" }] }] },
      bodyPlainText: "回答",
      status: "draft" as const,
      importance: 2,
      newBadgeUntil: null,
      updatedBadgeUntil: null,
      isHidden: false,
      createdAt: "2026-08-15T00:00:00Z",
      updatedAt: "2026-08-15T00:00:00Z",
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
    vi.spyOn(knowledgeApi, "listCategories").mockResolvedValue([category]);
    vi.spyOn(knowledgeApi, "getArticle").mockResolvedValue(mergedDraft);
    vi.spyOn(knowledgeApi, "getCodexMergePublicationContext").mockResolvedValue({
      targetArticleId: mergedDraft.id,
      sourceArticles: [
        {
          articleId: "source-1",
          title: "元FAQ A",
          status: "published",
          sourceUpdatedAt: "2026-08-14T00:00:00Z",
          currentUpdatedAt: "2026-08-14T00:00:00Z",
          deletedAt: null,
          isCurrent: true,
          isMerged: false,
        },
        {
          articleId: "source-2",
          title: "元FAQ B",
          status: "published",
          sourceUpdatedAt: "2026-08-14T00:00:00Z",
          currentUpdatedAt: "2026-08-14T00:00:00Z",
          deletedAt: null,
          isCurrent: true,
          isMerged: false,
        },
      ],
      canMarkMerged: true,
      allSourcesMerged: false,
    });
    const save = vi.spyOn(knowledgeApi, "saveArticle").mockImplementation(async (input) => ({
      ...mergedDraft,
      ...input,
      status: input.status,
      updatedAt: "2026-08-15T01:00:00Z",
    }));
    const mark = vi.spyOn(knowledgeApi, "markCodexMergeSources").mockResolvedValue({
      targetArticleId: mergedDraft.id,
      markedCount: 2,
    });
    vi.spyOn(window, "confirm").mockReturnValue(true);

    renderEditor(`/articles/${mergedDraft.id}/edit`);

    expect(await screen.findByText("この下書きはCodexで統合したFAQです")).toBeVisible();
    fireEvent.click(screen.getByRole("radio", { name: /^公開/ }));
    const newBadge = screen.getByRole("checkbox", { name: /「新着」を表示する/ });
    expect(newBadge).toBeChecked();
    expect(newBadge.closest(".display-setting-card")?.querySelector("input[type='date']")).toHaveValue(getDefaultBadgeUntil());
    fireEvent.click(screen.getByRole("button", { name: "FAQを保存" }));

    await waitFor(() => expect(save).toHaveBeenCalledWith(expect.objectContaining({
      id: mergedDraft.id,
      status: "published",
      newBadgeUntil: getDefaultBadgeUntil(),
    })));
    await waitFor(() => expect(mark).toHaveBeenCalledWith(mergedDraft.id));
    expect(await screen.findByText("保存完了")).toBeVisible();
  });
});
