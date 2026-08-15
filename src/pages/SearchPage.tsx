import { FormEvent, useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Link, useLocation, useSearchParams } from "react-router-dom";
import { knowledgeApi, toAppError } from "../api/knowledgeApi";
import { useDisplaySettings } from "../app/ColorTheme";
import { EmptyState, ErrorState, LoadingState } from "../components/Feedback";
import { StatusBadge } from "../components/StatusBadge";
import { ArticleDisplayBadges } from "../components/ArticleDisplayBadges";
import type { AppError, ArticleListItem, Category } from "../types/domain";

function SearchIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true">
      <circle cx="11" cy="11" r="6.5" />
      <path d="m16 16 4 4" />
    </svg>
  );
}

function FolderIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true">
      <path d="M3.5 7.5h6l2-2h9v13h-17z" />
    </svg>
  );
}

function formatUpdatedAt(value: string) {
  return new Intl.DateTimeFormat("ja-JP", {
    year: "numeric",
    month: "numeric",
    day: "numeric",
  }).format(new Date(value));
}

function topCategoryName(
  categoryId: string,
  categoryById: Map<string, Category>,
  fallbackName: string,
) {
  let current = categoryById.get(categoryId);
  const visited = new Set<string>();
  while (current?.parentId && !visited.has(current.id)) {
    visited.add(current.id);
    current = categoryById.get(current.parentId) ?? current;
    if (visited.has(current.id)) break;
  }
  return current?.name || fallbackName;
}

function displayArticleTitle(
  article: ArticleListItem,
  categoryById: Map<string, Category>,
  showTopCategoryInTitle: boolean,
) {
  if (!showTopCategoryInTitle) return article.title;
  const name = topCategoryName(article.categoryId, categoryById, article.categoryName);
  const prefix = `【${name}】`;
  return article.title.startsWith(prefix) ? article.title : `${prefix}${article.title}`;
}

const SEARCH_RETURN_POSITION_KEY = "knowledgeapp.searchReturnPosition";

interface SearchReturnPosition {
  returnTo: string;
  scrollY: number;
}

function readSearchReturnPosition(returnTo: string): number | null {
  try {
    const stored = window.sessionStorage.getItem(SEARCH_RETURN_POSITION_KEY);
    if (!stored) return null;
    const parsed = JSON.parse(stored) as Partial<SearchReturnPosition>;
    return parsed.returnTo === returnTo && typeof parsed.scrollY === "number"
      ? Math.max(0, parsed.scrollY)
      : null;
  } catch {
    return null;
  }
}

export function SearchPage() {
  const { showTopCategoryInTitle } = useDisplaySettings();
  const location = useLocation();
  const [searchParams, setSearchParams] = useSearchParams();
  const submittedQuery = searchParams.get("q")?.trim() ?? "";
  const selectedCategory = searchParams.get("category") ?? "";
  const includeDrafts = searchParams.get("drafts") === "1";
  const returnTo = `${location.pathname}${location.search}`;
  const restoreScrollY = useRef(readSearchReturnPosition(returnTo));
  const [categories, setCategories] = useState<Category[]>([]);
  const [articles, setArticles] = useState<ArticleListItem[]>([]);
  const [query, setQuery] = useState(submittedQuery);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<AppError | null>(null);
  const categoryById = useMemo(
    () => new Map(categories.map((category) => [category.id, category])),
    [categories],
  );

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

  useEffect(() => {
    setQuery(submittedQuery);
  }, [submittedQuery]);

  useEffect(() => {
    if (loading || restoreScrollY.current === null) return;
    const scrollY = restoreScrollY.current;
    restoreScrollY.current = null;
    window.requestAnimationFrame(() => window.scrollTo({ top: scrollY, behavior: "auto" }));
    window.sessionStorage.removeItem(SEARCH_RETURN_POSITION_KEY);
  }, [loading]);

  const selectedCategoryItem = categories.find((category) => category.id === selectedCategory);
  const categoryScopeLabel = selectedCategoryItem
    ? `「${selectedCategoryItem.name}」以下`
    : "すべての分類";
  const hasActiveFilters = Boolean(query || submittedQuery || selectedCategory || includeDrafts);

  const submit = (event: FormEvent) => {
    event.preventDefault();
    const nextQuery = query.trim();
    if (nextQuery === submittedQuery) {
      void load();
    } else {
      const nextParams = new URLSearchParams(searchParams);
      if (nextQuery) nextParams.set("q", nextQuery);
      else nextParams.delete("q");
      setSearchParams(nextParams, { replace: true });
    }
  };

  const clearFilters = () => {
    setQuery("");
    setSearchParams(new URLSearchParams(), { replace: true });
  };

  const selectCategory = (categoryId: string) => {
    const nextParams = new URLSearchParams(searchParams);
    if (categoryId) nextParams.set("category", categoryId);
    else nextParams.delete("category");
    setSearchParams(nextParams, { replace: true });
  };

  const setDraftVisibility = (checked: boolean) => {
    const nextParams = new URLSearchParams(searchParams);
    if (checked) nextParams.set("drafts", "1");
    else nextParams.delete("drafts");
    setSearchParams(nextParams, { replace: true });
  };

  const rememberSearchPosition = () => {
    try {
      window.sessionStorage.setItem(SEARCH_RETURN_POSITION_KEY, JSON.stringify({
        returnTo,
        scrollY: window.scrollY,
      } satisfies SearchReturnPosition));
    } catch {
      // 一時保存が使えない環境でも、検索条件はURLから復元できる。
    }
  };

  return (
    <div className="page search-page">
      <div className="page-heading search-page-heading">
        <div>
          <span className="eyebrow">FAQナレッジ</span>
          <h1>知りたいことを探す</h1>
          <p>キーワードや困っている状況から、必要な情報をすばやく見つけられます。</p>
        </div>
        <Link to="/articles/new" className="button primary search-create-button">
          <span aria-hidden="true">＋</span> 新しいFAQ
        </Link>
      </div>

      <form className="search-panel" onSubmit={submit}>
        <div className="search-primary-row">
          <label className="search-field">
            <span className="sr-only">検索キーワード</span>
            <span aria-hidden="true" className="search-symbol"><SearchIcon /></span>
            <input
              value={query}
              onChange={(event) => setQuery(event.target.value)}
              placeholder="例：Windowsの画面が真っ暗になる、エラーコード 0x80070005"
            />
          </label>
          <button type="submit" className="button primary search-submit-button">
            <SearchIcon />
            検索
          </button>
        </div>
        <div className="search-options">
          <span className="search-scope">
            <FolderIcon />
            検索範囲：<strong>{categoryScopeLabel}</strong>
          </span>
          <label className="toggle-label">
            <input
              type="checkbox"
              checked={includeDrafts}
              onChange={(event) => setDraftVisibility(event.target.checked)}
            />
            下書き・廃止も含める
          </label>
          <button
            type="button"
            className="clear-filter-button"
            onClick={clearFilters}
            disabled={!hasActiveFilters}
            title="キーワード・分類・表示条件を初期状態に戻します"
          >
            <span aria-hidden="true">↺</span>
            条件を初期化
          </button>
        </div>
      </form>

      <div className="search-workspace">
        <aside className="category-sidebar" aria-label="分類で絞り込む">
          <div className="category-sidebar-heading">
            <div>
              <span className="eyebrow">CATEGORY</span>
              <h2>分類から探す</h2>
            </div>
            <Link to="/categories" aria-label="分類を管理する">管理</Link>
          </div>

          <nav className="category-filter-nav" aria-label="FAQの分類">
            <button
              type="button"
              className={`category-filter-item category-filter-all${selectedCategory ? "" : " active"}`}
              aria-pressed={!selectedCategory}
              onClick={() => selectCategory("")}
            >
              <span className="category-filter-icon"><FolderIcon /></span>
              <span>すべての分類</span>
            </button>
            {categories.map((category) => (
              <button
                key={category.id}
                type="button"
                className={`category-filter-item${selectedCategory === category.id ? " active" : ""}`}
                aria-pressed={selectedCategory === category.id}
                onClick={() => selectCategory(category.id)}
                style={{ paddingInlineStart: `${14 + Math.max(0, category.depth - 1) * 16}px` }}
              >
                <span className="category-tree-mark" aria-hidden="true">
                  {category.depth > 1 ? "└" : ""}
                </span>
                <span className="category-filter-name">{category.name}</span>
                <span className="category-filter-count" aria-label={`${category.articleCount}件`}>
                  {category.articleCount}
                </span>
              </button>
            ))}
          </nav>

          {categories.length > 0 && (
            <p className="category-sidebar-help">選んだ分類と、その配下にあるFAQを表示します。</p>
          )}
        </aside>

        <section className="search-results" aria-labelledby="search-results-heading">
          <div className="results-toolbar">
            <div>
              <span className="results-context">{categoryScopeLabel}</span>
              <h2 id="search-results-heading">
                {loading ? "FAQを検索しています" : `${articles.length}件のFAQ`}
              </h2>
            </div>
            {submittedQuery && <span className="submitted-query">「{submittedQuery}」の検索結果</span>}
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
              title={submittedQuery || selectedCategory ? "一致するFAQがありません" : "FAQはまだ登録されていません"}
              description={submittedQuery || selectedCategory ? "検索語を短くするか、分類を「すべての分類」にしてお試しください。" : "最初のFAQを登録すると、ここに一覧が表示されます。"}
              action={hasActiveFilters
                ? <button type="button" className="button secondary" onClick={clearFilters}>条件をクリアする</button>
                : <Link to="/articles/new" className="button primary">FAQを登録する</Link>}
            />
          )}
          {!loading && !error && articles.length > 0 && (
            <div className="article-grid">
              {articles.map((article) => (
                <Link
                  key={article.id}
                  to={`/articles/${article.id}`}
                  state={{ returnTo }}
                  className="article-card"
                  onClick={rememberSearchPosition}
                >
                  <div className="card-meta">
                    <span className="category-pill">{article.categoryName}</span>
                    <StatusBadge status={article.status} />
                    <ArticleDisplayBadges
                      newBadgeUntil={article.newBadgeUntil}
                      updatedBadgeUntil={article.updatedBadgeUntil}
                    />
                  </div>
                  <h3>{displayArticleTitle(article, categoryById, showTopCategoryInTitle)}</h3>
                  <p>{article.summary || "概要はまだ入力されていません。"}</p>
                  <div className="card-footer">
                    <span className="importance-dots" aria-label={`重要度 ${article.importance}`}>
                      <span>重要度</span>
                      <span aria-hidden="true">{"●".repeat(article.importance)}{"○".repeat(3 - article.importance)}</span>
                    </span>
                    <time dateTime={article.updatedAt}>更新日 {formatUpdatedAt(article.updatedAt)}</time>
                    <span className="card-open-mark" aria-hidden="true">›</span>
                  </div>
                </Link>
              ))}
            </div>
          )}
        </section>
      </div>
    </div>
  );
}
