import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { CodexProposalsPage } from "./CodexProposalsPage";

const mocks = vi.hoisted(() => ({
  listCodexProposals: vi.fn(),
  listCategories: vi.fn(),
  acceptCodexProposal: vi.fn(),
  rejectCodexProposal: vi.fn(),
  reopenRejectedCodexProposal: vi.fn(),
  getArticle: vi.fn(),
}));

vi.mock("../api/knowledgeApi", async (importOriginal) => {
  const original = await importOriginal<typeof import("../api/knowledgeApi")>();
  return { ...original, knowledgeApi: mocks };
});

const category = {
  id: "019c0000-0000-7000-8000-000000000001",
  parentId: null,
  name: "Windows",
  description: "Windowsの操作",
  depth: 1,
  sortOrder: 0,
  articleCount: 0,
};

const proposal = {
  formatVersion: 2,
  requestId: "019c0000-0000-7000-8000-000000000002",
  seriesId: "019c0000-0000-7000-8000-000000000002",
  createdAt: "2026-08-12T10:00:00+09:00",
  proposalKind: "create" as const,
  sourceArticles: [],
  faq: {
    title: "Windowsを再起動するには？",
    summary: "通常の再起動手順です。",
    bodyDoc: { type: "doc", content: [{ type: "paragraph", content: [{ type: "text", text: "再起動します。" }] }] },
    importance: 1,
  },
  existingCategoryCandidates: [{
    categoryId: category.id,
    categoryPath: "Windows",
    reason: "Windowsの基本操作だからです。",
  }],
  newCategoryProposal: null,
};

describe("CodexProposalsPage", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mocks.listCategories.mockResolvedValue([category]);
    mocks.listCodexProposals.mockResolvedValue({
      proposals: [proposal],
      history: [],
      rejected: [],
      inboxPath: "C:\\local\\codex-inbox",
      categoryCatalogPath: "C:\\local\\codex-bridge\\categories.json",
    });
  });

  it("shows the proposal and imports it only as a reviewed draft", async () => {
    mocks.acceptCodexProposal.mockResolvedValue({
      article: { id: "article-id" },
      createdCategory: null,
    });
    render(<MemoryRouter><CodexProposalsPage /></MemoryRouter>);

    expect(await screen.findByRole("heading", { name: proposal.faq.title })).toBeInTheDocument();
    expect(screen.getByText("Windowsの基本操作だからです。", { exact: false })).toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole("combobox")).toHaveValue(category.id));
    fireEvent.click(screen.getByRole("button", { name: "確認して下書きに取り込む" }));

    await waitFor(() => {
      expect(mocks.acceptCodexProposal).toHaveBeenCalledWith(proposal.requestId, category.id, false);
    });
  });

  it("shows the concise heading and only the pending empty-state title", async () => {
    mocks.listCodexProposals.mockResolvedValue({
      proposals: [],
      history: [],
      rejected: [],
      inboxPath: "C:\\local\\codex-inbox",
      categoryCatalogPath: "C:\\local\\codex-bridge\\categories.json",
    });

    render(<MemoryRouter><CodexProposalsPage /></MemoryRouter>);

    expect(await screen.findByRole("heading", { name: "Codexからの提案" })).toBeInTheDocument();
    expect(screen.getByText("新規下書き、既存FAQの推敲・修正、複数FAQの統合案を確認できます。")).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "確認待ちの提案はありません" })).toBeInTheDocument();
    expect(screen.queryByText("内容を確認してから反映する")).not.toBeInTheDocument();
    expect(screen.queryByText("Codexが直接データベースを変更することはありません。", { exact: false })).not.toBeInTheDocument();
    expect(screen.queryByText("Codexへの委譲方針")).not.toBeInTheDocument();
    expect(screen.queryByText("従量課金APIをアプリへ組み込まず", { exact: false })).not.toBeInTheDocument();
    expect(screen.queryByText("新規FAQはCodexへ直接依頼できます。", { exact: false })).not.toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Codex連携用の保存先" })).not.toBeInTheDocument();
  });

  it("keeps a rejected proposal in history and can reopen the latest item", async () => {
    mocks.listCodexProposals
      .mockResolvedValueOnce({
        proposals: [],
        history: [{
          historyId: 1,
          proposal,
          status: "rejected",
          receivedAt: "2026-08-12T10:00:00+09:00",
          decidedAt: "2026-08-12T10:05:00+09:00",
          acceptedArticleId: null,
          canReopen: true,
        }],
        rejected: [],
        inboxPath: "C:\\local\\codex-inbox",
        categoryCatalogPath: "C:\\local\\codex-bridge\\categories.json",
      })
      .mockResolvedValueOnce({
        proposals: [proposal],
        history: [],
        rejected: [],
        inboxPath: "C:\\local\\codex-inbox",
        categoryCatalogPath: "C:\\local\\codex-bridge\\categories.json",
      });
    mocks.reopenRejectedCodexProposal.mockResolvedValue(undefined);
    render(<MemoryRouter><CodexProposalsPage /></MemoryRouter>);

    fireEvent.click(await screen.findByRole("tab", { name: /承認・却下履歴/ }));
    fireEvent.click(await screen.findByRole("button", { name: "再検討へ戻す" }));

    await waitFor(() => expect(mocks.reopenRejectedCodexProposal).toHaveBeenCalledWith(proposal.requestId));
    expect(await screen.findByRole("tab", { name: /確認待ち/ })).toHaveAttribute("aria-selected", "true");
  });

  it("disables a stale revision and explains why it cannot be applied", async () => {
    const sourceUpdatedAt = "2026-08-12T09:00:00+09:00";
    const revision = {
      ...proposal,
      requestId: "019c0000-0000-7000-8000-000000000003",
      seriesId: "019c0000-0000-7000-8000-000000000004",
      proposalKind: "revise" as const,
      sourceArticles: [{ articleId: "source-article", sourceUpdatedAt }],
      existingCategoryCandidates: [],
    };
    mocks.listCodexProposals.mockResolvedValue({
      proposals: [revision],
      history: [],
      rejected: [],
      inboxPath: "C:\\local\\codex-inbox",
      categoryCatalogPath: "C:\\local\\codex-bridge\\categories.json",
    });
    mocks.getArticle.mockResolvedValue({
      id: "source-article",
      categoryId: category.id,
      categoryName: category.name,
      title: "現在のFAQ",
      summary: "委譲後に更新されています。",
      status: "published",
      importance: 1,
      newBadgeUntil: null,
      updatedBadgeUntil: null,
      isHidden: false,
      updatedAt: "2026-08-12T11:00:00+09:00",
      bodyDoc: { type: "doc", content: [] },
      bodyPlainText: "",
      createdAt: "2026-08-01T00:00:00+09:00",
      deletedAt: null,
      attachments: [],
    });
    render(<MemoryRouter><CodexProposalsPage /></MemoryRouter>);

    expect(await screen.findByText("委譲元FAQが更新・削除されたか、現在の内容を確認できません。"))
      .toBeInTheDocument();
    expect(screen.getByRole("button", { name: "確認して既存FAQへ反映" })).toBeDisabled();
  });
});
