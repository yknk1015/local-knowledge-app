import { FormEvent, useCallback, useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { knowledgeApi, toAppError } from "../api/knowledgeApi";
import { EmptyState, ErrorState, LoadingState } from "../components/Feedback";
import { StatusBadge } from "../components/StatusBadge";
import { ArticleDisplayBadges } from "../components/ArticleDisplayBadges";
import type { AppError, ArticleListItem, Category } from "../types/domain";

export function SearchPage() {
  const [categories, setCategories] = useState<Category[]>([]);
  const [articles, setArticles] = useState<ArticleListItem[]>([]);
  const [query, setQuery] = useState("");
  const [submittedQuery, setSubmittedQuery] = useState("");
  const [selectedCategory, setSelectedCategory] = useState("");
  const [includeDrafts, setIncludeDrafts] = useState(false);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<AppError | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const [categoryItems, articleItems] = await Promise.all([
        knowledgeApi.listCategories(),
        knowledgeApi.searchArticles({
          query: submittedQuery,
          categoryId: selectedCategory || undefined,
          includeDrafts,
        }),
      ]);
      setCategories(categoryItems);
      setArticles(articleItems);
    } catch (caught) {
      setError(toAppError(caught));
    } finally {
      setLoading(false);
    }
  }, [includeDrafts, selectedCategory, submittedQuery]);

  useEffect(() => {
    void load();
  }, [load]);

  const submit = (event: FormEvent) => {
    event.preventDefault();
    const nextQuery = query.trim();
    if (nextQuery === submittedQuery) {
      void load();
    } else {
      setSubmittedQuery(nextQuery);
    }
  };

  return (
    <div className="page search-page">
      <div className="page-heading split">
        <div>
          <span className="eyebrow">FAQナレッジ</span>
          <h1>知りたいことを探す</h1>
          <p>キーワードや困っている状況を入力してください。</p>
        </div>
        <Link to="/articles/new" className="button primary">＋ 新しいFAQ</Link>
      </div>

      <form className="search-panel" onSubmit={submit}>
        <label className="search-field">
          <span className="sr-only">検索キーワード</span>
          <span aria-hidden="true" className="search-symbol">⌕</span>
          <input
            value={query}
            onChange={(event) => setQuery(event.target.value)}
            placeholder="例：Windowsの画面が真っ暗になる"
          />
        </label>
        <select
          aria-label="分類で絞り込む"
          value={selectedCategory}
          onChange={(event) => setSelectedCategory(event.target.value)}
        >
          <option value="">すべての分類</option>
          {categories.map((category) => (
            <option key={category.id} value={category.id}>
              {"　".repeat(Math.max(0, category.depth - 1))}{category.name}
            </option>
          ))}
        </select>
        <button type="submit" className="button primary">検索</button>
      </form>

      <div className="results-toolbar">
        <strong>{loading ? "検索中…" : `${articles.length}件のFAQ`}</strong>
        <label className="toggle-label">
          <input
            type="checkbox"
            checked={includeDrafts}
            onChange={(event) => setIncludeDrafts(event.target.checked)}
          />
          下書き・廃止も表示
        </label>
      </div>

      {loading && <LoadingState label="FAQを読み込んでいます…" />}
      {!loading && error && <ErrorState error={error} onRetry={() => void load()} />}
      {!loading && !error && categories.length === 0 && (
        <EmptyState
          title="最初の分類を作りましょう"
          description="FAQを登録する前に、整理先となる分類を1つ作成します。"
          action={<Link to="/categories" className="button primary">分類を作成する</Link>}
        />
      )}
      {!loading && !error && categories.length > 0 && articles.length === 0 && (
        <EmptyState
          title={submittedQuery ? "一致するFAQがありません" : "FAQはまだ登録されていません"}
          description={submittedQuery ? "検索語を短くするか、分類を「すべて」にしてお試しください。" : "最初のFAQを登録すると、ここに一覧が表示されます。"}
          action={<Link to="/articles/new" className="button primary">FAQを登録する</Link>}
        />
      )}
      {!loading && !error && articles.length > 0 && (
        <div className="article-grid">
          {articles.map((article) => (
            <Link key={article.id} to={`/articles/${article.id}`} className="article-card">
              <div className="card-meta">
                <span className="category-pill">{article.categoryName}</span>
                <StatusBadge status={article.status} />
                <ArticleDisplayBadges
                  newBadgeUntil={article.newBadgeUntil}
                  updatedBadgeUntil={article.updatedBadgeUntil}
                />
              </div>
              <h2>{article.title}</h2>
              <p>{article.summary || "概要はまだ入力されていません。"}</p>
              <div className="card-footer">
                <span>重要度 {"●".repeat(article.importance)}{"○".repeat(3 - article.importance)}</span>
                <time dateTime={article.updatedAt}>{new Date(article.updatedAt).toLocaleDateString("ja-JP")}</time>
              </div>
            </Link>
          ))}
        </div>
      )}
    </div>
  );
}
