import { FormEvent, useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { FaqArticleLink } from "../app/FaqTabs";
import { knowledgeApi, toAppError } from "../api/knowledgeApi";
import { EmptyState, ErrorState, LoadingState } from "../components/Feedback";
import type { AppError, SearchLogPage, ViewLogPage } from "../types/domain";
import "./HistoryPage.css";

type HistoryTab = "search" | "zero" | "view";

interface HistoryFilters {
  query: string;
  startDate: string;
  endDate: string;
}

const EMPTY_FILTERS: HistoryFilters = { query: "", startDate: "", endDate: "" };
const SCOPE_LABELS = { descendants: "選択分類以下", current: "選択分類のみ", all: "全分類" } as const;

function displayDate(value: string) {
  return new Intl.DateTimeFormat("ja-JP", {
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
  }).format(new Date(value));
}

export function HistoryPage() {
  const [tab, setTab] = useState<HistoryTab>("search");
  const [draftFilters, setDraftFilters] = useState<HistoryFilters>(EMPTY_FILTERS);
  const [filters, setFilters] = useState<HistoryFilters>(EMPTY_FILTERS);
  const [searchPage, setSearchPage] = useState<SearchLogPage | null>(null);
  const [viewPage, setViewPage] = useState<ViewLogPage | null>(null);
  const [page, setPage] = useState(1);
  const [reloadToken, setReloadToken] = useState(0);
  const [loading, setLoading] = useState(true);
  const [deleting, setDeleting] = useState(false);
  const [error, setError] = useState<AppError | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);
    const request = tab === "view"
      ? knowledgeApi.listViewLogs(
        filters.query,
        filters.startDate || undefined,
        filters.endDate || undefined,
        page,
      ).then((result) => { if (!cancelled) setViewPage(result); })
      : knowledgeApi.listSearchLogs(
        filters.query,
        filters.startDate || undefined,
        filters.endDate || undefined,
        tab === "zero",
        page,
      ).then((result) => { if (!cancelled) setSearchPage(result); });
    request.catch((caught) => { if (!cancelled) setError(toAppError(caught)); })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [filters, page, reloadToken, tab]);

  const switchTab = (next: HistoryTab) => {
    setTab(next);
    setPage(1);
    setNotice(null);
  };

  const applyFilters = (event: FormEvent) => {
    event.preventDefault();
    setFilters({ ...draftFilters });
    setPage(1);
    setNotice(null);
  };

  const resetFilters = () => {
    setDraftFilters(EMPTY_FILTERS);
    setFilters(EMPTY_FILTERS);
    setPage(1);
    setNotice(null);
  };

  const removeHistory = async (deleteAll: boolean) => {
    if (!deleteAll && !filters.startDate && !filters.endDate) return;
    const targetLabel = tab === "view" ? "閲覧履歴" : "検索履歴";
    const rangeLabel = deleteAll
      ? "すべて"
      : `${filters.startDate || "最初"} ～ ${filters.endDate || "最新"}`;
    setDeleting(true);
    setError(null);
    setNotice(null);
    try {
      const count = tab === "view"
        ? (await knowledgeApi.listViewLogs(
          "",
          deleteAll ? undefined : filters.startDate || undefined,
          deleteAll ? undefined : filters.endDate || undefined,
          1,
        )).total
        : (await knowledgeApi.listSearchLogs(
          "",
          deleteAll ? undefined : filters.startDate || undefined,
          deleteAll ? undefined : filters.endDate || undefined,
          false,
          1,
        )).total;
      if (count === 0) {
        setNotice(`削除対象の${targetLabel}はありません。`);
        return;
      }
      if (!window.confirm(`${targetLabel}（${rangeLabel}、${count}件）を削除しますか？\nこの操作は元に戻せません。`)) return;
      const deleted = await knowledgeApi.deleteHistory(
        tab === "view" ? "view" : "search",
        deleteAll ? undefined : filters.startDate || undefined,
        deleteAll ? undefined : filters.endDate || undefined,
        deleteAll,
      );
      setPage(1);
      setReloadToken((value) => value + 1);
      setNotice(`${targetLabel}を${deleted}件削除しました。`);
    } catch (caught) {
      setError(toAppError(caught));
    } finally {
      setDeleting(false);
    }
  };

  const currentPage = tab === "view" ? viewPage : searchPage;
  const totalPages = currentPage ? Math.max(1, Math.ceil(currentPage.total / currentPage.pageSize)) : 1;

  return (
    <div className="page history-page">
      <div className="page-heading">
        <div><h1>検索・閲覧履歴</h1><p>利用状況と、回答を用意できていない検索を確認します。</p></div>
      </div>

      <div className="management-tabs history-tabs" role="tablist" aria-label="履歴の種類">
        <button type="button" role="tab" aria-selected={tab === "search"} className={tab === "search" ? "active" : ""} onClick={() => switchTab("search")}>検索履歴</button>
        <button type="button" role="tab" aria-selected={tab === "zero"} className={tab === "zero" ? "active" : ""} onClick={() => switchTab("zero")}>0件検索</button>
        <button type="button" role="tab" aria-selected={tab === "view"} className={tab === "view" ? "active" : ""} onClick={() => switchTab("view")}>閲覧履歴</button>
      </div>

      <form className="panel history-filter-panel" onSubmit={applyFilters}>
        <div className="history-filter-field history-query-field">
          <label htmlFor="history-query">文字列</label>
          <input id="history-query" type="search" maxLength={500} value={draftFilters.query} onChange={(event) => setDraftFilters((current) => ({ ...current, query: event.target.value }))} placeholder={tab === "view" ? "FAQタイトル・元の検索文" : "検索文"} />
        </div>
        <div className="history-filter-field">
          <label htmlFor="history-start-date">開始日</label>
          <input id="history-start-date" type="date" value={draftFilters.startDate} onChange={(event) => setDraftFilters((current) => ({ ...current, startDate: event.target.value }))} />
        </div>
        <div className="history-filter-field">
          <label htmlFor="history-end-date">終了日</label>
          <input id="history-end-date" type="date" value={draftFilters.endDate} onChange={(event) => setDraftFilters((current) => ({ ...current, endDate: event.target.value }))} />
        </div>
        <div className="history-filter-actions">
          <button type="submit" className="button primary">絞り込む</button>
          <button type="button" className="button secondary" onClick={resetFilters}>条件をクリア</button>
        </div>
      </form>

      {error && <ErrorState error={error} />}
      {notice && <div className="success-notice" role="status">{notice}</div>}

      <div className="history-delete-bar">
        <span>現在の対象: {tab === "view" ? "閲覧履歴" : "検索履歴"}</span>
        <div>
          <button type="button" className="button danger-outline" disabled={deleting || (!filters.startDate && !filters.endDate)} onClick={() => void removeHistory(false)}>指定期間を削除</button>
          <button type="button" className="button danger-outline" disabled={deleting} onClick={() => void removeHistory(true)}>全履歴を削除</button>
        </div>
      </div>

      {loading ? <LoadingState label="履歴を読み込んでいます…" /> : currentPage && currentPage.total > 0 ? (
        <div className="panel management-table-wrap history-table-wrap">
          {tab === "view" ? (
            <table className="management-table history-table">
              <thead><tr><th>閲覧日時</th><th>FAQ</th><th>元の検索文</th></tr></thead>
              <tbody>{viewPage?.items.map((item) => (
                <tr key={item.id}>
                  <td data-label="閲覧日時">{displayDate(item.viewedAt)}</td>
                  <td data-label="FAQ"><FaqArticleLink articleId={item.articleId} articleTitle={item.articleTitle}>{item.articleTitle}</FaqArticleLink></td>
                  <td data-label="元の検索文">{item.sourceQueryText || "検索結果以外から閲覧"}</td>
                </tr>
              ))}</tbody>
            </table>
          ) : (
            <table className="management-table history-table">
              <thead><tr><th>検索日時</th><th>検索文</th><th>範囲</th><th>分類</th><th>結果</th></tr></thead>
              <tbody>{searchPage?.items.map((item) => (
                <tr key={item.id}>
                  <td data-label="検索日時">{displayDate(item.createdAt)}</td>
                  <td data-label="検索文"><strong>{item.queryText || "（検索文なし）"}</strong></td>
                  <td data-label="範囲">{SCOPE_LABELS[item.scope]}</td>
                  <td data-label="分類">{item.categoryName || "指定なし"}</td>
                  <td data-label="結果"><span className={item.resultCount === 0 ? "history-zero-count" : ""}>{item.resultCount}件</span></td>
                </tr>
              ))}</tbody>
            </table>
          )}
        </div>
      ) : (
        <EmptyState title="該当する履歴はありません" description="絞り込み条件を変えて確認してください。" />
      )}

      {!loading && currentPage && currentPage.total > 0 && (
        <div className="pagination">
          <button type="button" className="button secondary compact" disabled={page <= 1} onClick={() => setPage((value) => Math.max(1, value - 1))}>前へ</button>
          <span>{currentPage.total}件中 {currentPage.page} / {totalPages}ページ</span>
          <button type="button" className="button secondary compact" disabled={page >= totalPages} onClick={() => setPage((value) => Math.min(totalPages, value + 1))}>次へ</button>
        </div>
      )}
    </div>
  );
}
