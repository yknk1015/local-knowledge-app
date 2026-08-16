import { FormEvent, useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Link, useLocation, useSearchParams } from "react-router-dom";
import { knowledgeApi, toAppError } from "../api/knowledgeApi";
import { useDisplaySettings } from "../app/ColorTheme";
import { EmptyState, ErrorState, LoadingState } from "../components/Feedback";
import { StatusBadge } from "../components/StatusBadge";
import { ArticleDisplayBadges } from "../components/ArticleDisplayBadges";
import type { AppError, ArticleListItem, Category, SearchArticlePage, SearchSort } from "../types/domain";

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
const COLLAPSED_CATEGORIES_KEY = "knowledgeapp.collapsedSearchCategories";
const EMPTY_SEARCH_PAGE: SearchArticlePage = { items: [], total: 0, page: 1, pageSize: 50 };
const DEFAULT_SEARCH_SORT: SearchSort = "updatedDesc";

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

function readCollapsedCategories(): Set<string> {
  try {
    const stored = window.sessionStorage.getItem(COLLAPSED_CATEGORIES_KEY);
    if (!stored) return new Set();
    const parsed = JSON.parse(stored) as unknown;
    return Array.isArray(parsed)
      ? new Set(parsed.filter((value): value is string => typeof value === "string"))
      : new Set();
  } catch {
    return new Set();
  }
}

function parsePage(value: string | null) {
  const parsed = Number(value);
  return Number.isSafeInteger(parsed) && parsed > 0 ? parsed : 1;
}

function parseSort(value: string | null): SearchSort {
  switch (value) {
    case "updatedAsc":
    case "importanceDesc":
    case "importanceAsc":
      return value;
    default:
      return DEFAULT_SEARCH_SORT;
  }
}

function paginationItems(currentPage: number, totalPages: number): Array<number | string> {
  const candidates = new Set([1, totalPages, currentPage - 1, currentPage, currentPage + 1]);
  const pages = [...candidates]
    .filter((page) => page >= 1 && page <= totalPages)
    .sort((left, right) => left - right);
  const items: Array<number | string> = [];
  pages.forEach((page, index) => {
    const previous = pages[index - 1];
    if (previous && page - previous > 1) items.push(`ellipsis-${previous}-${page}`);
    items.push(page);
  });
  return items;
}

export function SearchPage() {
  const { showTopCategoryInTitle } = useDisplaySettings();
  const location = useLocation();
  const [searchParams, setSearchParams] = useSearchParams();
  const submittedQuery = searchParams.get("q")?.trim() ?? "";
  const selectedCategory = searchParams.get("category") ?? "";
  const includeDrafts = searchParams.get("drafts") === "1";
  const requestedPage = parsePage(searchParams.get("page"));
  const selectedSort = parseSort(searchParams.get("sort"));
  const returnTo = `${location.pathname}${location.search}`;
  const restoreScrollY = useRef(readSearchReturnPosition(returnTo));
  const pageNavigationTarget = useRef<number | null>(null);
  const resultsHeadingRef = useRef<HTMLHeadingElement>(null);
  const [categories, setCategories] = useState<Category[]>([]);
  const [result, setResult] = useState<SearchArticlePage>(EMPTY_SEARCH_PAGE);
  const [collapsedCategories, setCollapsedCategories] = useState(readCollapsedCategories);
  const [query, setQuery] = useState(submittedQuery);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<AppError | null>(null);
  const categoryById = useMemo(
    () => new Map(categories.map((category) => [category.id, category])),
    [categories],
  );
  const expandableCategoryIds = useMemo(
    () => new Set(categories.flatMap((category) => category.parentId ? [category.parentId] : [])),
    [categories],
  );
  const visibleCategories = useMemo(() => categories.filter((category) => {
    let parentId = category.parentId;
    const visited = new Set<string>();
    while (parentId && !visited.has(parentId)) {
      if (collapsedCategories.has(parentId)) return false;
      visited.add(parentId);
      parentId = categoryById.get(parentId)?.parentId ?? null;
    }
    return true;
  }), [categories, categoryById, collapsedCategories]);
  const articles = result.items;
  const totalPages = Math.max(1, Math.ceil(result.total / result.pageSize));

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
          page: requestedPage,
          sort: selectedSort,
        }),
      ]);
      setCategories(categoryItems);
      setResult(articleItems);
      if (articleItems.page !== requestedPage) {
        const nextParams = new URLSearchParams();
        if (submittedQuery) nextParams.set("q", submittedQuery);
        if (selectedCategory) nextParams.set("category", selectedCategory);
        if (includeDrafts) nextParams.set("drafts", "1");
        if (selectedSort !== DEFAULT_SEARCH_SORT) nextParams.set("sort", selectedSort);
        if (articleItems.page > 1) nextParams.set("page", String(articleItems.page));
        setSearchParams(nextParams, { replace: true });
      }
    } catch (caught) {
      setError(toAppError(caught));
    } finally {
      setLoading(false);
    }
  }, [includeDrafts, requestedPage, selectedCategory, selectedSort, setSearchParams, submittedQuery]);

  useEffect(() => {
    void load();
  }, [load]);

  useEffect(() => {
    setQuery(submittedQuery);
  }, [submittedQuery]);

  useEffect(() => {
    if (categories.length === 0) return;
    setCollapsedCategories((current) => {
      const next = new Set([...current].filter((id) => expandableCategoryIds.has(id)));
      return next.size === current.size ? current : next;
    });
  }, [categories.length, expandableCategoryIds]);

  useEffect(() => {
    try {
      window.sessionStorage.setItem(COLLAPSED_CATEGORIES_KEY, JSON.stringify([...collapsedCategories]));
    } catch {
      // 一時保存が使えない環境でも、分類の開閉操作自体は利用できる。
    }
  }, [collapsedCategories]);

  useEffect(() => {
    if (!selectedCategory || categoryById.size === 0) return;
    const ancestors = new Set<string>();
    let parentId = categoryById.get(selectedCategory)?.parentId ?? null;
    while (parentId && !ancestors.has(parentId)) {
      ancestors.add(parentId);
      parentId = categoryById.get(parentId)?.parentId ?? null;
    }
    if (ancestors.size === 0) return;
    setCollapsedCategories((current) => {
      if (![...ancestors].some((id) => current.has(id))) return current;
      const next = new Set(current);
      ancestors.forEach((id) => next.delete(id));
      return next;
    });
  }, [categoryById, selectedCategory]);

  useEffect(() => {
    if (loading || restoreScrollY.current === null) return;
    const scrollY = restoreScrollY.current;
    restoreScrollY.current = null;
    window.requestAnimationFrame(() => window.scrollTo({ top: scrollY, behavior: "auto" }));
    window.sessionStorage.removeItem(SEARCH_RETURN_POSITION_KEY);
  }, [loading]);

  useEffect(() => {
    if (loading || pageNavigationTarget.current !== result.page || result.page !== requestedPage) return;
    pageNavigationTarget.current = null;
    window.requestAnimationFrame(() => {
      resultsHeadingRef.current?.focus({ preventScroll: true });
      resultsHeadingRef.current?.scrollIntoView?.({ block: "start" });
    });
  }, [loading, requestedPage, result.page]);

  const selectedCategoryItem = categories.find((category) => category.id === selectedCategory);
  const categoryScopeLabel = selectedCategoryItem
    ? `「${selectedCategoryItem.name}」以下`
    : "すべての分類";
  const hasActiveFilters = Boolean(
    query
    || submittedQuery
    || selectedCategory
    || includeDrafts
    || requestedPage > 1
    || selectedSort !== DEFAULT_SEARCH_SORT,
  );

  const submit = (event: FormEvent) => {
    event.preventDefault();
    const nextQuery = query.trim();
    if (nextQuery === submittedQuery && requestedPage === 1) {
      void load();
    } else {
      const nextParams = new URLSearchParams(searchParams);
      if (nextQuery) nextParams.set("q", nextQuery);
      else nextParams.delete("q");
      nextParams.delete("page");
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
    nextParams.delete("page");
    setSearchParams(nextParams, { replace: true });
  };

  const setDraftVisibility = (checked: boolean) => {
    const nextParams = new URLSearchParams(searchParams);
    if (checked) nextParams.set("drafts", "1");
    else nextParams.delete("drafts");
    nextParams.delete("page");
    setSearchParams(nextParams, { replace: true });
  };

  const setSort = (sort: SearchSort) => {
    const nextParams = new URLSearchParams(searchParams);
    if (sort === DEFAULT_SEARCH_SORT) nextParams.delete("sort");
    else nextParams.set("sort", sort);
    nextParams.delete("page");
    setSearchParams(nextParams, { replace: true });
  };

  const toggleCategory = (categoryId: string) => {
    setCollapsedCategories((current) => {
      const next = new Set(current);
      if (next.has(categoryId)) next.delete(categoryId);
      else next.add(categoryId);
      return next;
    });
  };

  const goToPage = (page: number) => {
    const nextPage = Math.max(1, Math.min(totalPages, page));
    if (nextPage === result.page) return;
    pageNavigationTarget.current = nextPage;
    const nextParams = new URLSearchParams(searchParams);
    if (nextPage === 1) nextParams.delete("page");
    else nextParams.set("page", String(nextPage));
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
          <h1>FAQを探す</h1>
          <p>必要な情報をすばやく検索</p>
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
            title="キーワード・分類・表示条件・並び順を初期状態に戻します"
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

          <div className="category-tree-controls" role="group" aria-label="分類ツリーの表示">
            <button
              type="button"
              onClick={() => setCollapsedCategories(new Set())}
              disabled={collapsedCategories.size === 0}
            >
              すべて開く
            </button>
            <button
              type="button"
              onClick={() => setCollapsedCategories(new Set(expandableCategoryIds))}
              disabled={expandableCategoryIds.size === 0 || collapsedCategories.size === expandableCategoryIds.size}
            >
              すべて閉じる
            </button>
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
            {visibleCategories.map((category) => {
              const hasChildren = expandableCategoryIds.has(category.id);
              const isCollapsed = collapsedCategories.has(category.id);
              return (
              <div
                key={category.id}
                className={`category-filter-row${selectedCategory === category.id ? " active" : ""}`}
                style={{ paddingInlineStart: `${5 + Math.max(0, category.depth - 1) * 13}px` }}
              >
                {hasChildren ? (
                  <button
                    type="button"
                    className="category-collapse-button"
                    aria-label={`${category.name}の下位分類を${isCollapsed ? "開く" : "閉じる"}`}
                    aria-expanded={!isCollapsed}
                    onClick={() => toggleCategory(category.id)}
                  >
                    <span aria-hidden="true" className={isCollapsed ? "collapsed" : ""}>›</span>
                  </button>
                ) : (
                  <span className="category-collapse-placeholder" aria-hidden="true" />
                )}
                <button
                  type="button"
                  className="category-filter-select"
                  aria-label={category.name}
                  aria-pressed={selectedCategory === category.id}
                  onClick={() => selectCategory(category.id)}
                >
                  <span className="category-filter-name">{category.name}</span>
                  <span className="category-filter-count" aria-label={`${category.articleCount}件`}>
                    {category.articleCount}
                  </span>
                </button>
              </div>
              );
            })}
          </nav>

          {categories.length > 0 && (
            <p className="category-sidebar-help">選んだ分類と、その配下にあるFAQを表示します。</p>
          )}
        </aside>

        <section className="search-results" aria-labelledby="search-results-heading">
          <div className="results-toolbar">
            <div>
              <span className="results-context">{categoryScopeLabel}</span>
              <h2 id="search-results-heading" ref={resultsHeadingRef} tabIndex={-1}>
                {loading ? "FAQを検索しています" : `${result.total}件のFAQ`}
              </h2>
            </div>
            <div className="results-toolbar-actions">
              {submittedQuery && <span className="submitted-query">「{submittedQuery}」の検索結果</span>}
              <label className="search-sort-control">
                <span>並び替え</span>
                <select
                  aria-label="検索結果の並び替え"
                  value={selectedSort}
                  disabled={loading}
                  onChange={(event) => setSort(event.target.value as SearchSort)}
                >
                  <option value="updatedDesc">更新日（新しい順）</option>
                  <option value="updatedAsc">更新日（古い順）</option>
                  <option value="importanceDesc">重要度（高い順）</option>
                  <option value="importanceAsc">重要度（低い順）</option>
                </select>
              </label>
            </div>
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
            <>
              <div className="article-list" role="list" aria-label="FAQ検索結果">
                {articles.map((article) => (
                  <article key={article.id} className="article-card" role="listitem">
                    <Link
                      to={`/articles/${article.id}`}
                      state={{ returnTo }}
                      className="article-card-link"
                      onClick={rememberSearchPosition}
                    >
                      <div className="article-card-main">
                        <div className="card-meta">
                          <span className="category-pill">{article.categoryName}</span>
                          <StatusBadge status={article.status} />
                          <ArticleDisplayBadges
                            newBadgeUntil={article.newBadgeUntil}
                            updatedBadgeUntil={article.updatedBadgeUntil}
                          />
                        </div>
                        <h3>{displayArticleTitle(article, categoryById, showTopCategoryInTitle)}</h3>
                        <p className="article-card-summary">{article.summary || "概要はまだ入力されていません。"}</p>
                      </div>
                      <div className="article-card-facts">
                        <div className="article-updated-at">
                          <span>更新日</span>
                          <time dateTime={article.updatedAt}>{formatUpdatedAt(article.updatedAt)}</time>
                        </div>
                        <span className="importance-dots" aria-label={`重要度 ${article.importance}`}>
                          <span>重要度</span>
                          <span aria-hidden="true">{"●".repeat(article.importance)}{"○".repeat(3 - article.importance)}</span>
                        </span>
                        <span className="card-open-mark" aria-hidden="true">›</span>
                      </div>
                    </Link>
                  </article>
                ))}
              </div>
              {result.total > result.pageSize && (
                <nav className="pagination search-pagination" aria-label="FAQ検索結果のページ">
                  <button
                    type="button"
                    className="button secondary"
                    disabled={result.page <= 1}
                    onClick={() => goToPage(result.page - 1)}
                  >
                    前へ
                  </button>
                  <div className="pagination-pages">
                    {paginationItems(result.page, totalPages).map((item) => typeof item === "number" ? (
                      <button
                        key={item}
                        type="button"
                        className={item === result.page ? "active" : ""}
                        aria-label={`${item}ページ目`}
                        aria-current={item === result.page ? "page" : undefined}
                        onClick={() => goToPage(item)}
                      >
                        {item}
                      </button>
                    ) : (
                      <span key={item} aria-hidden="true">…</span>
                    ))}
                  </div>
                  <button
                    type="button"
                    className="button secondary"
                    disabled={result.page >= totalPages}
                    onClick={() => goToPage(result.page + 1)}
                  >
                    次へ
                  </button>
                  <span className="pagination-summary">
                    {(result.page - 1) * result.pageSize + 1}〜{Math.min(result.page * result.pageSize, result.total)}件 / 全{result.total}件
                  </span>
                </nav>
              )}
            </>
          )}
        </section>
      </div>
    </div>
  );
}
