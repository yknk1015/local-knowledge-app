import { FormEvent, useCallback, useEffect, useState } from "react";
import { Link, useNavigate } from "react-router-dom";
import { knowledgeApi, toAppError } from "../api/knowledgeApi";
import { selectFaqCsvExportPath } from "../api/transferDialogs";
import { EmptyState, ErrorState, LoadingState } from "../components/Feedback";
import { StatusBadge } from "../components/StatusBadge";
import { ArticleDisplayBadges } from "../components/ArticleDisplayBadges";
import { FaqArticleLink } from "../app/FaqTabs";
import type {
  AppError,
  ArticleStatus,
  Category,
  CodexDelegationResult,
  ManagementArticlePage,
  ManagementArticlesInput,
} from "../types/domain";

const EMPTY_PAGE: ManagementArticlePage = { items: [], total: 0, page: 1, pageSize: 50 };
type ManagementViewMode = "normal" | "detail";

export function ArticleManagementPage() {
  const navigate = useNavigate();
  const [categories, setCategories] = useState<Category[]>([]);
  const [result, setResult] = useState<ManagementArticlePage>(EMPTY_PAGE);
  const [query, setQuery] = useState("");
  const [submittedQuery, setSubmittedQuery] = useState("");
  const [categoryId, setCategoryId] = useState("");
  const [status, setStatus] = useState<ArticleStatus | "">("");
  const [deleted, setDeleted] = useState(false);
  const [page, setPage] = useState(1);
  const [loading, setLoading] = useState(true);
  const [busyId, setBusyId] = useState<string | null>(null);
  const [error, setError] = useState<AppError | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [selectedForMerge, setSelectedForMerge] = useState<Set<string>>(new Set());
  const [delegation, setDelegation] = useState<CodexDelegationResult | null>(null);
  const [viewMode, setViewMode] = useState<ManagementViewMode>("normal");
  const [openActionMenuId, setOpenActionMenuId] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    const input: ManagementArticlesInput = {
      query: submittedQuery,
      categoryId: categoryId || undefined,
      status: status || undefined,
      deleted,
      page,
    };
    try {
      const [categoryItems, articles] = await Promise.all([
        knowledgeApi.listCategories(),
        knowledgeApi.listArticlesForManagement(input),
      ]);
      setCategories(categoryItems);
      setResult(articles);
    } catch (caught) {
      setError(toAppError(caught));
    } finally {
      setLoading(false);
    }
  }, [categoryId, deleted, page, status, submittedQuery]);

  useEffect(() => { void load(); }, [load]);
  useEffect(() => {
    if (!openActionMenuId) return;
    const closeMenu = (event: PointerEvent) => {
      if (event.target instanceof Element && !event.target.closest(".management-action-menu")) {
        setOpenActionMenuId(null);
      }
    };
    document.addEventListener("pointerdown", closeMenu);
    return () => document.removeEventListener("pointerdown", closeMenu);
  }, [openActionMenuId]);

  const submit = (event: FormEvent) => {
    event.preventDefault();
    setPage(1);
    setSubmittedQuery(query.trim());
    if (query.trim() === submittedQuery && page === 1) void load();
  };

  const chooseDeletionView = (nextDeleted: boolean) => {
    setNotice(null);
    setPage(1);
    setDeleted(nextDeleted);
    setSelectedForMerge(new Set());
    setDelegation(null);
    setOpenActionMenuId(null);
  };

  const exportCsv = async () => {
    const today = new Date();
    const stamp = `${today.getFullYear()}${String(today.getMonth() + 1).padStart(2, "0")}${String(today.getDate()).padStart(2, "0")}`;
    const destination = await selectFaqCsvExportPath(`KnowledgeApp_FAQ_${stamp}.knowledge-faq.csv`);
    if (!destination) return;
    const normalized = destination.toLowerCase().endsWith(".knowledge-faq.csv")
      ? destination
      : destination.replace(/\.csv$/i, "") + ".knowledge-faq.csv";
    setBusyId("csv-export"); setError(null); setNotice(null);
    try {
      const exported = await knowledgeApi.exportFaqCsv(normalized);
      setNotice(`${exported.exportedCount}件のFAQをCSVへ書き出しました。`);
    } catch (caught) { setError(toAppError(caught)); }
    finally { setBusyId(null); }
  };

  const deleteArticle = async (id: string, title: string) => {
    if (!window.confirm(`「${title}」を削除済みに移動しますか？\n通常の検索には表示されなくなりますが、あとから復元できます。`)) return;
    setBusyId(id);
    setError(null);
    setNotice(null);
    try {
      await knowledgeApi.deleteArticle(id);
      setNotice(`「${title}」を削除済みに移動しました。`);
      await load();
    } catch (caught) {
      setError(toAppError(caught));
    } finally {
      setBusyId(null);
    }
  };

  const restoreArticle = async (id: string, title: string) => {
    if (!window.confirm(`「${title}」を復元しますか？\n元の分類と状態で通常の管理一覧へ戻します。`)) return;
    setBusyId(id);
    setError(null);
    setNotice(null);
    try {
      await knowledgeApi.restoreArticle(id);
      setNotice(`「${title}」を復元しました。`);
      await load();
    } catch (caught) {
      setError(toAppError(caught));
    } finally {
      setBusyId(null);
    }
  };

  const duplicateArticle = async (id: string) => {
    setBusyId(id);
    setError(null);
    setNotice(null);
    try {
      const copy = await knowledgeApi.duplicateArticle(id);
      navigate(`/articles/${copy.id}/edit`);
    } catch (caught) {
      setError(toAppError(caught));
      setBusyId(null);
    }
  };

  const clearMerge = async (id: string, title: string) => {
    if (!window.confirm(`「${title}」の統合済み設定を解除しますか？\n統合による検索除外だけを解除します。表示されるかどうかは元の公開状態と非表示設定に従います。`)) return;
    setBusyId(id);
    setError(null);
    setNotice(null);
    try {
      await knowledgeApi.clearArticleMerge(id);
      setNotice(`「${title}」の統合済み設定を解除しました。`);
      await load();
    } catch (caught) {
      setError(toAppError(caught));
    } finally {
      setBusyId(null);
    }
  };

  const toggleMergeSelection = (id: string) => {
    setSelectedForMerge((current) => {
      const next = new Set(current);
      if (next.has(id)) next.delete(id);
      else if (next.size < 10) next.add(id);
      return next;
    });
    setDelegation(null);
  };

  const delegateMerge = async () => {
    if (selectedForMerge.size < 2 || selectedForMerge.size > 10) return;
    if (!window.confirm(`選択した${selectedForMerge.size}件のFAQ本文をCodexへ渡す委譲ファイルを作成しますか？\n統合案は新しい下書きになり、元FAQは変更・削除されません。`)) return;
    setBusyId("codex-merge");
    setError(null);
    setDelegation(null);
    try {
      setDelegation(await knowledgeApi.createCodexDelegation("merge", [...selectedForMerge]));
    } catch (caught) {
      setError(toAppError(caught));
    } finally {
      setBusyId(null);
    }
  };

  const totalPages = Math.max(1, Math.ceil(result.total / result.pageSize));

  return (
    <div className="page management-page">
      <div className="page-heading split">
        <div>
          <span className="eyebrow">下書き・廃止・統合済み・削除済みも管理</span>
          <h1>FAQの管理</h1>
          <p>通常検索に出ないFAQを含め、状態の確認、編集、統合解除、削除、復元を行えます。</p>
        </div>
        <div className="management-heading-actions">
          <button type="button" className="button secondary" disabled={busyId === "csv-export"} onClick={() => void exportCsv()}>CSVエクスポート</button>
          <Link to="/manage/csv-import" className="button secondary">CSVインポート</Link>
          <Link to="/manage/json-transfer" className="button secondary">JSON入出力</Link>
          <Link to="/articles/new" className="button primary">＋ 新しいFAQ</Link>
        </div>
      </div>

      <div className="management-tabs" role="group" aria-label="削除状態">
        <button type="button" className={!deleted ? "active" : ""} onClick={() => chooseDeletionView(false)}>
          登録中のFAQ
        </button>
        <button type="button" className={deleted ? "active danger" : ""} onClick={() => chooseDeletionView(true)}>
          削除済み
        </button>
      </div>

      <form className="management-filters panel" onSubmit={submit}>
        <label>
          <span>キーワード</span>
          <input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="タイトル・概要・回答から検索" />
        </label>
        <label>
          <span>分類</span>
          <select value={categoryId} onChange={(event) => { setCategoryId(event.target.value); setPage(1); }}>
            <option value="">すべての分類</option>
            {categories.map((category) => (
              <option key={category.id} value={category.id}>{"　".repeat(category.depth - 1)}{category.name}</option>
            ))}
          </select>
        </label>
        <label>
          <span>状態</span>
          <select value={status} onChange={(event) => { setStatus(event.target.value as ArticleStatus | ""); setPage(1); }}>
            <option value="">すべての状態</option>
            <option value="draft">下書き</option>
            <option value="published">公開</option>
            <option value="archived">廃止</option>
          </select>
        </label>
        <button type="submit" className="button primary">絞り込む</button>
      </form>

      {notice && <div className="success-notice management-notice" role="status">{notice}</div>}
      {delegation && (
        <section className="success-notice codex-delegation-notice" role="status">
          <strong>Codexへの統合委譲を準備しました。</strong>
          <p>Codexの新しいタスクへ、次の文章をそのまま送ってください。</p>
          <code>{delegation.prompt}</code>
          <small>委譲番号：{delegation.delegationId}</small>
        </section>
      )}
      {error && <ErrorState error={error} onRetry={() => void load()} />}

      <div className="management-result-heading">
        <div className="management-result-summary">
          <strong>{loading ? "読み込み中…" : `${result.total}件`}</strong>
          <span>{deleted ? "削除済みFAQは復元できます。" : "削除操作ではデータを完全消去しません。"}</span>
        </div>
        <div className="management-view-controls" role="group" aria-label="一覧の表示">
          <span>表示</span>
          <button
            type="button"
            className={viewMode === "normal" ? "active" : ""}
            aria-pressed={viewMode === "normal"}
            onClick={() => setViewMode("normal")}
          >
            通常
          </button>
          <button
            type="button"
            className={viewMode === "detail" ? "active" : ""}
            aria-pressed={viewMode === "detail"}
            onClick={() => setViewMode("detail")}
          >
            詳細
          </button>
        </div>
      </div>
      {!deleted && (
        <div className="management-codex-actions panel">
          <div>
            <strong>CodexでFAQを統合</strong>
            <span>統合するFAQを2～10件選択してください。統合済みFAQは選択できません。</span>
          </div>
          <button type="button" className="button secondary" disabled={selectedForMerge.size < 2 || busyId === "codex-merge"} onClick={() => void delegateMerge()}>
            選択中の{selectedForMerge.size}件をCodexへ委譲
          </button>
        </div>
      )}

      {loading && <LoadingState label="FAQ管理一覧を読み込んでいます…" />}
      {!loading && !error && result.items.length === 0 && (
        <EmptyState
          title={deleted ? "削除済みFAQはありません" : "条件に一致するFAQがありません"}
          description={deleted ? "削除したFAQはここから復元できます。" : "絞り込み条件を変更してお試しください。"}
        />
      )}
      {!loading && !error && result.items.length > 0 && (
        <div className="management-table-wrap panel">
          <table className={`management-table management-articles-table ${viewMode === "detail" ? "is-detailed" : "is-normal"}`}>
            <colgroup>
              {!deleted && <col className="management-col-select" />}
              <col className="management-col-faq" />
              <col className="management-col-category" />
              <col className="management-col-status" />
              {viewMode === "detail" && <col className="management-col-audit" />}
              <col className="management-col-updated" />
              <col className="management-col-actions" />
            </colgroup>
            <thead>
              <tr>
                {!deleted && <th>統合</th>}
                <th>FAQ</th>
                <th>分類</th>
                <th>状態</th>
                {viewMode === "detail" && <th className="management-audit-heading">作成者・更新者</th>}
                <th>最終更新</th>
                <th><span className="sr-only">操作</span></th>
              </tr>
            </thead>
            <tbody>
              {result.items.map((article) => (
                <tr key={article.id} className={deleted ? "without-selection" : ""}>
                  {!deleted && (
                    <td className="management-select-cell" data-label="統合">
                      <input
                        type="checkbox"
                        aria-label={`「${article.title}」を統合対象にする`}
                        checked={selectedForMerge.has(article.id)}
                        disabled={Boolean(article.mergeInfo) || (!selectedForMerge.has(article.id) && selectedForMerge.size >= 10)}
                        onChange={() => toggleMergeSelection(article.id)}
                      />
                    </td>
                  )}
                  <td className="management-faq-cell" data-label="FAQ">
                    <FaqArticleLink articleId={article.id} articleTitle={article.title}>{article.title}</FaqArticleLink>
                    {article.summary && <small>{article.summary}</small>}
                    {article.mergeInfo && (
                      <small className="management-merge-target">
                        統合先：<FaqArticleLink articleId={article.mergeInfo.targetArticleId} articleTitle={article.mergeInfo.targetArticleTitle}>{article.mergeInfo.targetArticleTitle}</FaqArticleLink>
                      </small>
                    )}
                  </td>
                  <td className="management-category-cell" data-label="分類">{article.categoryName}</td>
                  <td className="management-status-cell" data-label="状態">
                    <div className="management-badges">
                      {article.mergeInfo
                        ? <span className="status-badge merged">統合済み</span>
                        : <StatusBadge status={article.status} />}
                      <ArticleDisplayBadges
                        newBadgeUntil={article.newBadgeUntil}
                        updatedBadgeUntil={article.updatedBadgeUntil}
                        isHidden={article.isHidden}
                        showHidden
                        showExpired
                      />
                    </div>
                  </td>
                  {viewMode === "detail" && (
                    <td className="management-audit-cell" data-label="作成者・更新者">
                      <span title={article.createdByDisplayName ?? "不明"}><small>作成</small><strong>{article.createdByDisplayName ?? "不明"}</strong></span>
                      <span title={article.updatedByDisplayName ?? "不明"}><small>更新</small><strong>{article.updatedByDisplayName ?? "不明"}</strong></span>
                    </td>
                  )}
                  <td className="management-updated-cell" data-label="最終更新">{new Date(article.updatedAt).toLocaleDateString("ja-JP")}</td>
                  <td className="management-row-actions" data-label="操作">
                    <div className="management-row-actions-inner">
                      {deleted ? (
                        <>
                          <button type="button" className="button secondary" disabled={busyId === article.id} onClick={() => void restoreArticle(article.id, article.title)}>
                            復元
                          </button>
                          {article.mergeInfo && (
                            <div className="management-action-menu">
                              <button
                                type="button"
                                className="button secondary management-more-button"
                                aria-label={`「${article.title}」のその他の操作`}
                                aria-expanded={openActionMenuId === article.id}
                                onClick={() => setOpenActionMenuId((current) => current === article.id ? null : article.id)}
                              >…</button>
                              {openActionMenuId === article.id && (
                                <div className="management-action-menu-items" role="menu">
                                  <button type="button" role="menuitem" disabled={busyId === article.id} onClick={() => { setOpenActionMenuId(null); void clearMerge(article.id, article.title); }}>
                                    統合を解除
                                  </button>
                                </div>
                              )}
                            </div>
                          )}
                        </>
                      ) : (
                        <>
                          <Link to={`/articles/${article.id}/edit`} className="button secondary">編集</Link>
                          <div className="management-action-menu">
                            <button
                              type="button"
                              className="button secondary management-more-button"
                              aria-label={`「${article.title}」のその他の操作`}
                              aria-expanded={openActionMenuId === article.id}
                              onClick={() => setOpenActionMenuId((current) => current === article.id ? null : article.id)}
                            >…</button>
                            {openActionMenuId === article.id && (
                              <div className="management-action-menu-items" role="menu">
                                <button type="button" role="menuitem" disabled={busyId === article.id} onClick={() => { setOpenActionMenuId(null); void duplicateArticle(article.id); }}>
                                  複製
                                </button>
                                {article.mergeInfo && (
                                  <button type="button" role="menuitem" disabled={busyId === article.id} onClick={() => { setOpenActionMenuId(null); void clearMerge(article.id, article.title); }}>
                                    統合を解除
                                  </button>
                                )}
                                <button type="button" role="menuitem" className="danger" disabled={busyId === article.id} onClick={() => { setOpenActionMenuId(null); void deleteArticle(article.id, article.title); }}>
                                  削除
                                </button>
                              </div>
                            )}
                          </div>
                        </>
                      )}
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {!loading && result.total > result.pageSize && (
        <nav className="pagination" aria-label="FAQ管理一覧のページ">
          <button type="button" className="button secondary" disabled={page <= 1} onClick={() => setPage((current) => current - 1)}>前へ</button>
          <span>{page} / {totalPages}ページ</span>
          <button type="button" className="button secondary" disabled={page >= totalPages} onClick={() => setPage((current) => current + 1)}>次へ</button>
        </nav>
      )}
    </div>
  );
}
