import { FormEvent, useCallback, useEffect, useState } from "react";
import { Link, useNavigate } from "react-router-dom";
import { knowledgeApi, toAppError } from "../api/knowledgeApi";
import { EmptyState, ErrorState, LoadingState } from "../components/Feedback";
import { StatusBadge } from "../components/StatusBadge";
import { ArticleDisplayBadges } from "../components/ArticleDisplayBadges";
import type {
  AppError,
  ArticleStatus,
  Category,
  ManagementArticlePage,
  ManagementArticlesInput,
} from "../types/domain";

const EMPTY_PAGE: ManagementArticlePage = { items: [], total: 0, page: 1, pageSize: 50 };

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

  const totalPages = Math.max(1, Math.ceil(result.total / result.pageSize));

  return (
    <div className="page management-page">
      <div className="page-heading split">
        <div>
          <span className="eyebrow">下書き・廃止・削除済みも管理</span>
          <h1>FAQの管理</h1>
          <p>通常検索に出ないFAQを含め、状態の確認、編集、削除、復元を行えます。</p>
        </div>
        <Link to="/articles/new" className="button primary">＋ 新しいFAQ</Link>
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
      {error && <ErrorState error={error} onRetry={() => void load()} />}

      <div className="management-result-heading">
        <strong>{loading ? "読み込み中…" : `${result.total}件`}</strong>
        <span>{deleted ? "削除済みFAQは復元できます。" : "削除操作ではデータを完全消去しません。"}</span>
      </div>

      {loading && <LoadingState label="FAQ管理一覧を読み込んでいます…" />}
      {!loading && !error && result.items.length === 0 && (
        <EmptyState
          title={deleted ? "削除済みFAQはありません" : "条件に一致するFAQがありません"}
          description={deleted ? "削除したFAQはここから復元できます。" : "絞り込み条件を変更してお試しください。"}
        />
      )}
      {!loading && !error && result.items.length > 0 && (
        <div className="management-table-wrap panel">
          <table className="management-table">
            <thead><tr><th>FAQ</th><th>分類</th><th>状態</th><th>最終更新</th><th><span className="sr-only">操作</span></th></tr></thead>
            <tbody>
              {result.items.map((article) => (
                <tr key={article.id}>
                  <td>
                    <Link to={`/articles/${article.id}`}>{article.title}</Link>
                    {article.summary && <small>{article.summary}</small>}
                  </td>
                  <td>{article.categoryName}</td>
                  <td>
                    <div className="management-badges">
                      <StatusBadge status={article.status} />
                      <ArticleDisplayBadges
                        newBadgeUntil={article.newBadgeUntil}
                        updatedBadgeUntil={article.updatedBadgeUntil}
                        isHidden={article.isHidden}
                        showHidden
                        showExpired
                      />
                    </div>
                  </td>
                  <td>{new Date(article.updatedAt).toLocaleDateString("ja-JP")}</td>
                  <td className="management-row-actions">
                    {deleted ? (
                      <button type="button" className="button secondary" disabled={busyId === article.id} onClick={() => void restoreArticle(article.id, article.title)}>
                        復元
                      </button>
                    ) : (
                      <>
                        <Link to={`/articles/${article.id}/edit`} className="button secondary">編集</Link>
                        <button type="button" className="button secondary" disabled={busyId === article.id} onClick={() => void duplicateArticle(article.id)}>
                          複製
                        </button>
                        <button type="button" className="button danger-outline" disabled={busyId === article.id} onClick={() => void deleteArticle(article.id, article.title)}>
                          削除
                        </button>
                      </>
                    )}
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
