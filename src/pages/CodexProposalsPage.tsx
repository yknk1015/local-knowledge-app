import { useCallback, useEffect, useMemo, useState } from "react";
import { Link, useNavigate } from "react-router-dom";
import { knowledgeApi, toAppError } from "../api/knowledgeApi";
import { EmptyState, ErrorState, LoadingState } from "../components/Feedback";
import { RichTextViewer } from "../components/RichTextEditor";
import { FaqArticleLink } from "../app/FaqTabs";
import type {
  AppError,
  Article,
  Category,
  CodexFaqProposal,
  CodexProposalHistoryItem,
  CodexProposalInbox,
} from "../types/domain";

type CategoryMode = "existing" | "new";
type ProposalTab = "pending" | "history";

function categoryLabel(category: Category, byId: Map<string, Category>) {
  const names = [category.name];
  let parentId = category.parentId;
  while (parentId) {
    const parent = byId.get(parentId);
    if (!parent) break;
    names.push(parent.name);
    parentId = parent.parentId;
  }
  return names.reverse().join(" > ");
}

function kindLabel(kind: CodexFaqProposal["proposalKind"]) {
  if (kind === "revise") return "既存FAQの修正案";
  if (kind === "merge") return "FAQの統合案";
  return "新規FAQ案";
}

export function CodexProposalsPage() {
  const navigate = useNavigate();
  const [inbox, setInbox] = useState<CodexProposalInbox | null>(null);
  const [categories, setCategories] = useState<Category[]>([]);
  const [tab, setTab] = useState<ProposalTab>("pending");
  const [selectedId, setSelectedId] = useState("");
  const [selectedHistoryId, setSelectedHistoryId] = useState<number | null>(null);
  const [sourceArticles, setSourceArticles] = useState<Article[]>([]);
  const [categoryMode, setCategoryMode] = useState<CategoryMode>("existing");
  const [categoryId, setCategoryId] = useState("");
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<AppError | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const [nextInbox, nextCategories] = await Promise.all([
        knowledgeApi.listCodexProposals(),
        knowledgeApi.listCategories(),
      ]);
      setInbox(nextInbox);
      setCategories(nextCategories);
      setSelectedId((current) =>
        nextInbox.proposals.some((proposal) => proposal.requestId === current)
          ? current
          : nextInbox.proposals[0]?.requestId ?? "",
      );
      setSelectedHistoryId((current) =>
        nextInbox.history.some((item) => item.historyId === current)
          ? current
          : nextInbox.history[0]?.historyId ?? null,
      );
    } catch (caught) {
      setError(toAppError(caught));
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { void load(); }, [load]);

  const selectedHistory = useMemo(
    () => inbox?.history.find((item) => item.historyId === selectedHistoryId) ?? null,
    [inbox, selectedHistoryId],
  );
  const selected = useMemo(
    () => tab === "pending"
      ? inbox?.proposals.find((proposal) => proposal.requestId === selectedId) ?? null
      : selectedHistory?.proposal ?? null,
    [inbox, selectedHistory, selectedId, tab],
  );
  const byId = useMemo(() => new Map(categories.map((category) => [category.id, category])), [categories]);

  useEffect(() => {
    if (!selected) {
      setSourceArticles([]);
      return;
    }
    let active = true;
    Promise.all(selected.sourceArticles.map((source) => knowledgeApi.getArticle(source.articleId)))
      .then((articles) => { if (active) setSourceArticles(articles); })
      .catch(() => { if (active) setSourceArticles([]); });
    return () => { active = false; };
  }, [selected]);

  useEffect(() => {
    if (!selected || selected.proposalKind === "revise") return;
    const candidate = selected.existingCategoryCandidates.find((item) => byId.has(item.categoryId));
    if (candidate) {
      setCategoryMode("existing");
      setCategoryId(candidate.categoryId);
    } else if (selected.newCategoryProposal) {
      setCategoryMode("new");
      setCategoryId("");
    } else {
      setCategoryMode("existing");
      setCategoryId("");
    }
  }, [byId, selected]);

  const proposedParent = selected?.newCategoryProposal?.parentCategoryId
    ? byId.get(selected.newCategoryProposal.parentCategoryId) ?? null
    : null;
  const newCategoryAvailable = Boolean(
    selected?.newCategoryProposal &&
      (!selected.newCategoryProposal.parentCategoryId || (proposedParent && proposedParent.depth < 5)),
  );
  const revisionSource = selected?.proposalKind === "revise" ? sourceArticles[0] ?? null : null;
  const sourceVersionsCurrent = Boolean(
    selected && (
      selected.proposalKind === "create" || (
        sourceArticles.length === selected.sourceArticles.length &&
        selected.sourceArticles.every((source) => {
          const current = sourceArticles.find((article) => article.id === source.articleId);
          return current?.updatedAt === source.sourceUpdatedAt && !current.deletedAt;
        })
      )
    ),
  );
  const canAccept = Boolean(
    selected && sourceVersionsCurrent && (
      selected.proposalKind === "revise" || (categoryMode === "new" ? newCategoryAvailable : categoryId)
    ),
  );
  const proposalImages = revisionSource?.attachments.map((attachment) => ({
    id: attachment.id,
    assetPath: attachment.assetPath,
    altText: attachment.altText,
  })) ?? [];

  const accept = async () => {
    if (!selected || !canAccept || tab !== "pending") return;
    setBusy(true);
    setError(null);
    try {
      const result = await knowledgeApi.acceptCodexProposal(
        selected.requestId,
        selected.proposalKind === "revise" ? null : categoryMode === "existing" ? categoryId : null,
        selected.proposalKind !== "revise" && categoryMode === "new",
      );
      const notice = selected.proposalKind === "revise"
        ? "Codexの修正案を既存FAQへ反映しました。内容を確認してください。"
        : selected.proposalKind === "merge"
          ? "Codexの統合案を新しい下書きとして取り込みました。元FAQは変更していません。"
          : "Codexの提案を下書きとして取り込みました。内容を確認してから公開してください。";
      navigate(`/articles/${result.article.id}/edit`, { state: { notice } });
    } catch (caught) {
      setError(toAppError(caught));
    } finally {
      setBusy(false);
    }
  };

  const reject = async (proposal: CodexFaqProposal) => {
    if (!window.confirm(`「${proposal.faq.title}」のCodex提案を却下しますか？\n履歴へ保存され、同じ依頼系列の最新案であれば再検討できます。`)) return;
    setBusy(true);
    setError(null);
    try {
      await knowledgeApi.rejectCodexProposal(proposal.requestId);
      await load();
    } catch (caught) {
      setError(toAppError(caught));
    } finally {
      setBusy(false);
    }
  };

  const reopen = async (item: CodexProposalHistoryItem) => {
    if (!item.canReopen) return;
    setBusy(true);
    setError(null);
    try {
      await knowledgeApi.reopenRejectedCodexProposal(item.proposal.requestId);
      await load();
      setTab("pending");
      setSelectedId(item.proposal.requestId);
    } catch (caught) {
      setError(toAppError(caught));
    } finally {
      setBusy(false);
    }
  };

  if (loading && !inbox) return <div className="page"><LoadingState label="Codexからの提案を確認しています…" /></div>;

  const currentItems = tab === "pending" ? inbox?.proposals ?? [] : inbox?.history ?? [];

  return (
    <div className="page codex-page">
      <div className="page-heading split">
        <div>
          <h1>Codexからの提案</h1>
          <p>新規下書き、既存FAQの推敲・修正、複数FAQの統合案を確認できます。</p>
        </div>
        <button type="button" className="button secondary" onClick={() => void load()} disabled={loading || busy}>提案を更新</button>
      </div>

      <div className="management-tabs" role="tablist" aria-label="Codex提案の状態">
        <button type="button" role="tab" aria-selected={tab === "pending"} className={tab === "pending" ? "active" : ""} onClick={() => setTab("pending")}>
          確認待ち（{inbox?.proposals.length ?? 0}）
        </button>
        <button type="button" role="tab" aria-selected={tab === "history"} className={tab === "history" ? "active" : ""} onClick={() => setTab("history")}>
          承認・却下履歴（{inbox?.history.length ?? 0}）
        </button>
      </div>

      {error && <ErrorState error={error} onRetry={() => void load()} />}
      {inbox && inbox.rejected.length > 0 && (
        <div className="validation-summary" role="alert">
          <strong>形式が正しくない提案が{inbox.rejected.length}件あります。</strong>
          <ul>{inbox.rejected.map((item) => <li key={item.fileName}>{item.fileName}：{item.message}</li>)}</ul>
        </div>
      )}

      {inbox && currentItems.length === 0 && (
        <EmptyState
          title={tab === "pending" ? "確認待ちの提案はありません" : "承認・却下履歴はありません"}
          description={tab === "pending"
            ? undefined
            : "提案を承認または却下すると、ここから後で内容を確認できます。"}
        />
      )}

      {inbox && currentItems.length > 0 && selected && (
        <div className="codex-layout">
          <aside className="panel codex-proposal-list">
            <div className="panel-heading"><h2>{tab === "pending" ? "確認待ち" : "履歴"}</h2><span>{currentItems.length}件</span></div>
            {tab === "pending" ? inbox.proposals.map((proposal) => (
              <button type="button" key={proposal.requestId} className={proposal.requestId === selected.requestId ? "selected" : ""} onClick={() => setSelectedId(proposal.requestId)}>
                <strong>{proposal.faq.title}</strong>
                <small>{kindLabel(proposal.proposalKind)}・{new Date(proposal.createdAt).toLocaleString("ja-JP")}</small>
              </button>
            )) : inbox.history.map((item) => (
              <button type="button" key={item.historyId} className={item.historyId === selectedHistoryId ? "selected" : ""} onClick={() => setSelectedHistoryId(item.historyId)}>
                <strong>{item.proposal.faq.title}</strong>
                <small>{item.status === "accepted" ? "承認済み" : "却下済み"}・{kindLabel(item.proposal.proposalKind)}</small>
              </button>
            ))}
          </aside>

          <section className="codex-review">
            {tab === "pending" && selected.proposalKind !== "create" && !sourceVersionsCurrent && (
              <div className="validation-summary" role="alert">
                <strong>委譲元FAQが更新・削除されたか、現在の内容を確認できません。</strong>
                <p>古い案の取り違えを防ぐため、この提案は反映できません。現在のFAQを開き、Codexへもう一度委譲してください。</p>
              </div>
            )}
            {selected.proposalKind === "revise" && revisionSource && (
              <div className="panel codex-original-preview">
                <div className="panel-heading"><h2>現在のFAQ</h2><span>{revisionSource.updatedAt === selected.sourceArticles[0]?.sourceUpdatedAt ? "委譲時から変更なし" : "委譲後に変更あり"}</span></div>
                <h3>{revisionSource.title}</h3>
                {revisionSource.summary && <p>{revisionSource.summary}</p>}
                <RichTextViewer value={revisionSource.bodyDoc} imageSources={proposalImages} />
              </div>
            )}
            {selected.proposalKind === "merge" && (
              <div className="panel codex-source-list">
                <div className="panel-heading"><h2>統合元FAQ</h2><span>元FAQは変更しません</span></div>
                <ul>{sourceArticles.map((article) => <li key={article.id}><FaqArticleLink articleId={article.id} articleTitle={article.title}>{article.title}</FaqArticleLink></li>)}</ul>
              </div>
            )}

            <div className="panel codex-draft-preview">
              <div className="card-meta"><span className="status-badge draft">{kindLabel(selected.proposalKind)}</span><span className="importance">重要度 {selected.faq.importance}</span></div>
              <h2>{selected.faq.title}</h2>
              {selected.faq.summary && <p className="detail-summary">{selected.faq.summary}</p>}
              <div className="codex-answer"><h3>{selected.proposalKind === "revise" ? "修正後の回答案" : "回答案"}</h3><RichTextViewer value={selected.faq.bodyDoc} imageSources={proposalImages} /></div>
            </div>

            {selected.proposalKind !== "revise" && tab === "pending" && (
              <section className="panel codex-category-review">
                <div className="panel-heading"><h2>所属分類を決める</h2><span>利用者の確認が必要です</span></div>
                <div className="codex-category-options">
                  <label className="codex-category-option">
                    <input type="radio" name="categoryMode" checked={categoryMode === "existing"} onChange={() => setCategoryMode("existing")} />
                    <span><strong>既存分類に入れる</strong><small>Codexの候補以外も選択できます。</small></span>
                  </label>
                  {categoryMode === "existing" && (
                    <select value={categoryId} onChange={(event) => setCategoryId(event.target.value)}>
                      <option value="">所属分類を選択してください</option>
                      {categories.map((category) => <option key={category.id} value={category.id}>{categoryLabel(category, byId)}</option>)}
                    </select>
                  )}
                  {selected.existingCategoryCandidates.length > 0 && (
                    <ul className="codex-reasons">
                      {selected.existingCategoryCandidates.map((candidate) => (
                        <li key={candidate.categoryId} className={!byId.has(candidate.categoryId) ? "stale" : ""}>
                          <strong>{candidate.categoryPath}</strong> — {byId.has(candidate.categoryId) ? candidate.reason : "この分類は現在存在しません。"}
                        </li>
                      ))}
                    </ul>
                  )}
                  {selected.newCategoryProposal && (
                    <>
                      <label className="codex-category-option">
                        <input type="radio" name="categoryMode" checked={categoryMode === "new"} disabled={!newCategoryAvailable} onChange={() => setCategoryMode("new")} />
                        <span><strong>提案された分類を新しく作る</strong><small>FAQ取込と同時に1分類だけ作成します。</small></span>
                      </label>
                      <div className={`new-category-proposal${newCategoryAvailable ? "" : " unavailable"}`}>
                        <strong>{selected.newCategoryProposal.parentCategoryPath ? `${selected.newCategoryProposal.parentCategoryPath} > ` : ""}{selected.newCategoryProposal.name}</strong>
                        {selected.newCategoryProposal.description && <p>{selected.newCategoryProposal.description}</p>}
                        <small>{newCategoryAvailable ? selected.newCategoryProposal.reason : "親分類が存在しないか、5階層目のため作成できません。"}</small>
                      </div>
                    </>
                  )}
                </div>
              </section>
            )}

            {tab === "pending" ? (
              <div className="codex-review-actions">
                <button type="button" className="button danger-outline" disabled={busy} onClick={() => void reject(selected)}>この提案を却下</button>
                <button type="button" className="button primary large" disabled={busy || !canAccept} onClick={() => void accept()}>
                  {busy ? "反映しています…" : selected.proposalKind === "revise" ? "確認して既存FAQへ反映" : selected.proposalKind === "merge" ? "統合版を下書きに取り込む" : "確認して下書きに取り込む"}
                </button>
              </div>
            ) : selectedHistory && (
              <div className="codex-review-actions history-actions">
                <span>{selectedHistory.status === "accepted" ? "この提案は承認済みです。" : selectedHistory.canReopen ? "この依頼系列の最新の却下案です。" : "同じ依頼に新しい案があるため閲覧専用です。"}</span>
                {selectedHistory.acceptedArticleId && <FaqArticleLink className="button secondary" articleId={selectedHistory.acceptedArticleId} articleTitle={selectedHistory.proposal.faq.title}>反映先FAQを開く</FaqArticleLink>}
                {selectedHistory.status === "rejected" && <button type="button" className="button primary" disabled={busy || !selectedHistory.canReopen} onClick={() => void reopen(selectedHistory)}>再検討へ戻す</button>}
              </div>
            )}
          </section>
        </div>
      )}
    </div>
  );
}
