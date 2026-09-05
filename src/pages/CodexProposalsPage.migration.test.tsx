import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { createMemoryRouter, RouterProvider, useLocation } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { knowledgeApi } from "../api/knowledgeApi";
import { FaqTabsProvider } from "../app/FaqTabs";
import type {
  AppError,
  Article,
  Category,
  CodexFaqProposal,
  CodexProposalHistoryItem,
  CodexProposalInbox,
} from "../types/domain";
import { CodexProposalsPage } from "./CodexProposalsPage";

// Rich-text security and rendering have their own tests. These tests exercise
// this page's approval controls and verify which synthetic document is previewed.
vi.mock("../components/RichTextEditor", () => ({
  RichTextViewer: ({ value }: { value: Record<string, unknown> }) => (
    <pre data-testid="proposal-document">{JSON.stringify(value)}</pre>
  ),
}));

const timestamp = "2026-09-05T01:00:00Z";
const categories: Category[] = [
  { id: "category-1", parentId: null, name: "合成PC", description: "合成分類", depth: 1, sortOrder: 0, articleCount: 2 },
  { id: "category-2", parentId: null, name: "合成ネットワーク", description: "", depth: 1, sortOrder: 1, articleCount: 0 },
];

function proposal(overrides: Partial<CodexFaqProposal> = {}): CodexFaqProposal {
  return {
    formatVersion: 2,
    requestId: "proposal-1",
    seriesId: "series-1",
    createdAt: timestamp,
    proposalKind: "create",
    sourceArticles: [],
    faq: {
      title: "合成FAQを確認するには？",
      summary: "合成画面で設定を確認します。",
      bodyDoc: {
        type: "doc",
        content: [{ type: "paragraph", content: [{ type: "text", text: "合成の回答手順です。" }] }],
      },
      importance: 2,
    },
    existingCategoryCandidates: [{ categoryId: "category-1", categoryPath: "合成PC", reason: "合成分類の候補です。" }],
    newCategoryProposal: null,
    ...overrides,
  };
}

function article(overrides: Partial<Article> = {}): Article {
  return {
    id: "source-1",
    categoryId: "category-1",
    categoryName: "合成PC",
    title: "合成の元FAQその1",
    summary: "元の合成概要です。",
    bodyDoc: { type: "doc", content: [{ type: "paragraph", content: [{ type: "text", text: "合成の元回答です。" }] }] },
    bodyPlainText: "合成の元回答です。",
    status: "published",
    importance: 1,
    newBadgeUntil: null,
    updatedBadgeUntil: null,
    isHidden: false,
    createdAt: timestamp,
    updatedAt: timestamp,
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
    ...overrides,
  };
}

function inbox(overrides: Partial<CodexProposalInbox> = {}): CodexProposalInbox {
  return {
    proposals: [],
    history: [],
    rejected: [],
    inboxPath: "synthetic/codex-inbox",
    categoryCatalogPath: "synthetic/codex-bridge/categories.json",
    ...overrides,
  };
}

function historyItem(value: CodexFaqProposal, id: number, canReopen: boolean): CodexProposalHistoryItem {
  return {
    historyId: id,
    proposal: value,
    status: "rejected",
    receivedAt: timestamp,
    decidedAt: timestamp,
    acceptedArticleId: null,
    canReopen,
  };
}

function EditDestination() {
  const location = useLocation();
  const state = location.state as { notice?: string } | null;
  return <div><h1>合成FAQ編集先</h1><p>{state?.notice}</p></div>;
}

function renderPage() {
  const router = createMemoryRouter([
    { path: "/codex", element: <FaqTabsProvider><CodexProposalsPage /></FaqTabsProvider> },
    { path: "/articles/:articleId/edit", element: <EditDestination /> },
  ], { initialEntries: ["/codex"] });
  render(<RouterProvider router={router} />);
  return router;
}

async function waitForReadyAction(name: string, initialCategoryId?: string) {
  const button = await screen.findByRole("button", { name });
  // Headings may render before effects select the initial category or load source FAQs.
  // Wait for the actual precondition before simulating a user change or approval.
  await waitFor(() => {
    expect(button).toBeEnabled();
    if (initialCategoryId) expect(screen.getByRole("combobox")).toHaveValue(initialCategoryId);
  });
  return button;
}

describe("CodexProposalsPage migration approval boundaries", () => {
  beforeEach(() => {
    vi.spyOn(knowledgeApi, "listCategories").mockResolvedValue(categories);
    vi.spyOn(knowledgeApi, "listCodexProposals").mockResolvedValue(inbox());
    vi.spyOn(knowledgeApi, "getArticle").mockResolvedValue(article());
    vi.spyOn(knowledgeApi, "acceptCodexProposal").mockResolvedValue({
      article: article({ id: "accepted-draft", status: "draft" }), createdCategory: null,
    });
    vi.spyOn(knowledgeApi, "rejectCodexProposal").mockResolvedValue(undefined);
    vi.spyOn(knowledgeApi, "reopenRejectedCodexProposal").mockResolvedValue(undefined);
  });

  afterEach(() => vi.restoreAllMocks());

  it("shows the empty inbox and rejected-file reason without allowing an import", async () => {
    vi.mocked(knowledgeApi.listCodexProposals).mockResolvedValue(inbox({
      rejected: [{ fileName: "synthetic-invalid.knowledge-proposal.json", message: "未知の項目があるため取り込めません。" }],
    }));
    renderPage();

    expect(await screen.findByRole("heading", { name: "確認待ちの提案はありません" })).toBeVisible();
    expect(screen.getByRole("alert")).toHaveTextContent("形式が正しくない提案が1件あります。");
    expect(screen.getByRole("alert")).toHaveTextContent("未知の項目があるため取り込めません。");
    expect(screen.queryByRole("button", { name: "確認して下書きに取り込む" })).not.toBeInTheDocument();
    expect(knowledgeApi.acceptCodexProposal).not.toHaveBeenCalled();
    expect(knowledgeApi.getArticle).not.toHaveBeenCalled();
  });

  it("previews a valid proposal and sends only the explicitly selected category on approval", async () => {
    const selected = proposal();
    vi.mocked(knowledgeApi.listCodexProposals).mockResolvedValue(inbox({ proposals: [selected] }));
    const router = renderPage();

    expect(await screen.findByRole("heading", { name: selected.faq.title })).toBeVisible();
    expect(screen.getByTestId("proposal-document")).toHaveTextContent("合成の回答手順です。");
    expect(screen.getByText(selected.faq.summary)).toBeVisible();
    expect(knowledgeApi.acceptCodexProposal).not.toHaveBeenCalled();
    const approve = await waitForReadyAction("確認して下書きに取り込む", "category-1");
    const category = screen.getByRole("combobox");
    fireEvent.change(category, { target: { value: "category-2" } });
    expect(category).toHaveValue("category-2");
    fireEvent.click(approve);

    await screen.findByRole("heading", { name: "合成FAQ編集先" });
    expect(knowledgeApi.acceptCodexProposal).toHaveBeenCalledExactlyOnceWith(selected.requestId, "category-2", false);
    expect(router.state.location.pathname).toBe("/articles/accepted-draft/edit");
    expect(screen.getByText("Codexの提案を下書きとして取り込みました。内容を確認してから公開してください。")).toBeVisible();
  });

  it("requires the proposed-category option before requesting one new category", async () => {
    const selected = proposal({
      newCategoryProposal: {
        parentCategoryId: "category-1", parentCategoryPath: "合成PC", name: "合成の新分類",
        description: "新しい合成分類です。", reason: "確認のための合成候補です。",
      },
    });
    vi.mocked(knowledgeApi.listCodexProposals).mockResolvedValue(inbox({ proposals: [selected] }));
    renderPage();

    await screen.findByRole("heading", { name: selected.faq.title });
    const approve = await waitForReadyAction("確認して下書きに取り込む", "category-1");
    const createCategory = screen.getByRole("radio", { name: /提案された分類を新しく作る/ });
    expect(createCategory).not.toBeChecked();
    expect(knowledgeApi.acceptCodexProposal).not.toHaveBeenCalled();
    fireEvent.click(createCategory);
    expect(createCategory).toBeChecked();
    expect(approve).toBeEnabled();
    fireEvent.click(approve);

    await waitFor(() => expect(knowledgeApi.acceptCodexProposal).toHaveBeenCalledExactlyOnceWith(selected.requestId, null, true));
  });

  it("compares the current revision source and approves without changing category", async () => {
    const selected = proposal({
      proposalKind: "revise", existingCategoryCandidates: [],
      sourceArticles: [{ articleId: "source-1", sourceUpdatedAt: timestamp }],
    });
    vi.mocked(knowledgeApi.listCodexProposals).mockResolvedValue(inbox({ proposals: [selected] }));
    renderPage();

    expect(await screen.findByText("委譲時から変更なし")).toBeVisible();
    expect(screen.getByRole("heading", { name: "現在のFAQ" })).toBeVisible();
    expect(screen.getAllByTestId("proposal-document")[0]).toHaveTextContent("合成の元回答です。");
    expect(screen.getAllByTestId("proposal-document")[1]).toHaveTextContent("合成の回答手順です。");
    expect(screen.queryByRole("combobox")).not.toBeInTheDocument();
    fireEvent.click(await waitForReadyAction("確認して既存FAQへ反映"));

    await waitFor(() => expect(knowledgeApi.acceptCodexProposal).toHaveBeenCalledExactlyOnceWith(selected.requestId, null, false));
  });

  it("disables a revision after the source changed and never submits the stale proposal", async () => {
    const selected = proposal({
      proposalKind: "revise", existingCategoryCandidates: [],
      sourceArticles: [{ articleId: "source-1", sourceUpdatedAt: timestamp }],
    });
    vi.mocked(knowledgeApi.listCodexProposals).mockResolvedValue(inbox({ proposals: [selected] }));
    vi.mocked(knowledgeApi.getArticle).mockResolvedValue(article({ updatedAt: "2026-09-05T02:00:00Z" }));
    renderPage();

    await screen.findByText("委譲後に変更あり");
    expect(screen.getByRole("alert")).toHaveTextContent("委譲元FAQが更新・削除されたか、現在の内容を確認できません。");
    const approve = screen.getByRole("button", { name: "確認して既存FAQへ反映" });
    expect(approve).toBeDisabled();
    fireEvent.click(approve);
    expect(knowledgeApi.acceptCodexProposal).not.toHaveBeenCalled();
    expect(knowledgeApi.getArticle).toHaveBeenCalledExactlyOnceWith("source-1");
  });

  it("does not reject or reload a proposal when the confirmation is cancelled", async () => {
    const selected = proposal();
    vi.mocked(knowledgeApi.listCodexProposals).mockResolvedValue(inbox({ proposals: [selected] }));
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(false);
    renderPage();

    await screen.findByRole("heading", { name: selected.faq.title });
    fireEvent.click(await waitForReadyAction("この提案を却下", "category-1"));

    expect(confirm).toHaveBeenCalledWith(expect.stringContaining("履歴へ保存され"));
    expect(knowledgeApi.rejectCodexProposal).not.toHaveBeenCalled();
    expect(knowledgeApi.listCodexProposals).toHaveBeenCalledTimes(1);
    expect(screen.getByRole("heading", { name: selected.faq.title })).toBeVisible();
  });

  it("keeps older rejected proposals read-only and reopens only the latest rejected proposal", async () => {
    const latest = proposal({ requestId: "latest", faq: { ...proposal().faq, title: "合成の最新却下案" } });
    const older = proposal({ requestId: "older", faq: { ...proposal().faq, title: "合成の古い却下案" } });
    vi.mocked(knowledgeApi.listCodexProposals)
      .mockResolvedValueOnce(inbox({ history: [historyItem(latest, 2, true), historyItem(older, 1, false)] }))
      .mockResolvedValue(inbox({ proposals: [latest], history: [historyItem(older, 1, false)] }));
    renderPage();

    await screen.findByRole("heading", { name: "確認待ちの提案はありません" });
    fireEvent.click(screen.getByRole("tab", { name: "承認・却下履歴（2）" }));
    fireEvent.click(screen.getByRole("button", { name: /合成の古い却下案/ }));
    expect(screen.getByText("同じ依頼に新しい案があるため閲覧専用です。")).toBeVisible();
    expect(screen.getByRole("button", { name: "再検討へ戻す" })).toBeDisabled();
    fireEvent.click(screen.getByRole("button", { name: "再検討へ戻す" }));
    expect(knowledgeApi.reopenRejectedCodexProposal).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole("button", { name: /合成の最新却下案/ }));
    expect(screen.getByText("この依頼系列の最新の却下案です。")).toBeVisible();
    fireEvent.click(await waitForReadyAction("再検討へ戻す"));

    await waitFor(() => expect(knowledgeApi.reopenRejectedCodexProposal).toHaveBeenCalledExactlyOnceWith("latest"));
    await waitFor(() => expect(screen.getByRole("tab", { name: "確認待ち（1）" })).toHaveAttribute("aria-selected", "true"));
    expect(screen.getByRole("heading", { name: latest.faq.title })).toBeVisible();
  });

  it("shows all explicitly delegated merge sources and imports only a new draft", async () => {
    const sources = [article(), article({ id: "source-2", title: "合成の元FAQその2" })];
    const selected = proposal({
      proposalKind: "merge",
      sourceArticles: sources.map((source) => ({ articleId: source.id, sourceUpdatedAt: source.updatedAt })),
    });
    vi.mocked(knowledgeApi.getArticle).mockImplementation(async (id) => sources.find((source) => source.id === id)!);
    vi.mocked(knowledgeApi.listCodexProposals).mockResolvedValue(inbox({ proposals: [selected] }));
    const save = vi.spyOn(knowledgeApi, "saveArticle");
    const remove = vi.spyOn(knowledgeApi, "deleteArticle");
    renderPage();

    expect(await screen.findByRole("link", { name: sources[1]!.title })).toBeVisible();
    expect(screen.getByRole("link", { name: sources[0]!.title })).toBeVisible();
    expect(screen.getByText("元FAQは変更しません")).toBeVisible();
    fireEvent.click(await waitForReadyAction("統合版を下書きに取り込む", "category-1"));

    expect(await screen.findByText("Codexの統合案を新しい下書きとして取り込みました。元FAQは変更していません。")).toBeVisible();
    expect(knowledgeApi.acceptCodexProposal).toHaveBeenCalledExactlyOnceWith(selected.requestId, "category-1", false);
    expect(save).not.toHaveBeenCalled();
    expect(remove).not.toHaveBeenCalled();
    expect(knowledgeApi.getArticle).toHaveBeenCalledTimes(2);
  });

  it("blocks duplicate approval while busy and shows the host error with its recovery action", async () => {
    const selected = proposal();
    vi.mocked(knowledgeApi.listCodexProposals).mockResolvedValue(inbox({ proposals: [selected] }));
    let rejectApproval!: (reason: AppError) => void;
    vi.mocked(knowledgeApi.acceptCodexProposal).mockImplementation(() => new Promise((_, reject) => {
      rejectApproval = reject;
    }));
    const router = renderPage();

    await screen.findByRole("heading", { name: selected.faq.title });
    const approve = await waitForReadyAction("確認して下書きに取り込む", "category-1");
    fireEvent.click(approve);
    const busy = await screen.findByRole("button", { name: "反映しています…" });
    expect(busy).toBeDisabled();
    expect(screen.getByRole("button", { name: "この提案を却下" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "提案を更新" })).toBeDisabled();
    fireEvent.click(busy);
    expect(knowledgeApi.acceptCodexProposal).toHaveBeenCalledTimes(1);
    await act(async () => rejectApproval({
      code: "CDX-015", message: "元FAQが更新されたため反映できません。", action: "最新のFAQを委譲し直してください。",
    }));

    expect(screen.getByRole("alert")).toHaveTextContent("CDX-015");
    expect(screen.getByRole("alert")).toHaveTextContent("最新のFAQを委譲し直してください。");
    expect(screen.getByRole("button", { name: "確認して下書きに取り込む" })).toBeEnabled();
    expect(router.state.location.pathname).toBe("/codex");
  });

  it("reports an inbox read failure and retries without accepting anything", async () => {
    vi.mocked(knowledgeApi.listCodexProposals)
      .mockRejectedValueOnce({ code: "CDX-001", message: "合成の提案箱を確認できません。", action: "提案を更新してください。" })
      .mockResolvedValue(inbox());
    renderPage();

    expect(await screen.findByRole("alert")).toHaveTextContent("CDX-001");
    fireEvent.click(await waitForReadyAction("もう一度試す"));
    expect(await screen.findByRole("heading", { name: "確認待ちの提案はありません" })).toBeVisible();
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
    expect(knowledgeApi.listCodexProposals).toHaveBeenCalledTimes(2);
    expect(knowledgeApi.acceptCodexProposal).not.toHaveBeenCalled();
  });
});
