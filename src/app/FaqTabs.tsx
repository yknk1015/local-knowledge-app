import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
  type MouseEvent,
  type ReactNode,
} from "react";
import { Link, useLocation, useNavigate, type LinkProps } from "react-router-dom";

export const MAX_FAQ_TABS = 10;
export const FAQ_TAB_LIMIT_MESSAGE = "開いているFAQタブが10件あります。既存のタブを閉じてからもう一度開いてください。";
export const FAQ_SEARCH_SCROLL_KEY = "faq-search";

export function faqArticleScrollKey(articleId: string) {
  return `faq-article:${articleId}`;
}

export interface FaqTab {
  articleId: string;
  title: string;
}

interface FaqTabsContextValue {
  tabs: FaqTab[];
  activeArticleId: string | null;
  limitMessage: string | null;
  openArticleTab: (articleId: string, title: string) => boolean;
  activateArticleTab: (articleId: string) => void;
  closeArticleTab: (articleId: string) => void;
  clearLimitMessage: () => void;
  saveViewScrollPosition: (viewKey: string, scrollY: number) => void;
  readViewScrollPosition: (viewKey: string) => number;
}

const FaqTabsContext = createContext<FaqTabsContextValue | null>(null);

function articleIdFromPath(pathname: string): string | null {
  const match = /^\/articles\/([^/]+)(?:\/edit)?$/.exec(pathname);
  return match ? decodeURIComponent(match[1]!) : null;
}

export function FaqTabsProvider({ children }: { children: ReactNode }) {
  const navigate = useNavigate();
  const location = useLocation();
  const [tabs, setTabs] = useState<FaqTab[]>([]);
  const tabsRef = useRef<FaqTab[]>([]);
  const viewScrollPositionsRef = useRef(new Map<string, number>());
  const [limitMessage, setLimitMessage] = useState<string | null>(null);
  const activeArticleId = articleIdFromPath(location.pathname);

  const replaceTabs = useCallback((nextTabs: FaqTab[]) => {
    tabsRef.current = nextTabs;
    setTabs(nextTabs);
  }, []);

  const saveViewScrollPosition = useCallback((viewKey: string, scrollY: number) => {
    viewScrollPositionsRef.current.set(viewKey, Number.isFinite(scrollY) ? Math.max(0, scrollY) : 0);
  }, []);

  const readViewScrollPosition = useCallback((viewKey: string) => (
    viewScrollPositionsRef.current.get(viewKey) ?? 0
  ), []);

  const openArticleTab = useCallback((articleId: string, title: string) => {
    const normalizedTitle = title.trim() || "タイトル未設定";
    const currentTabs = tabsRef.current;
    const existingIndex = currentTabs.findIndex((tab) => tab.articleId === articleId);
    if (existingIndex >= 0) {
      const existing = currentTabs[existingIndex]!;
      if (existing.title !== normalizedTitle) {
        const nextTabs = [...currentTabs];
        nextTabs[existingIndex] = { articleId, title: normalizedTitle };
        replaceTabs(nextTabs);
      }
      setLimitMessage(null);
      return true;
    }

    if (currentTabs.length >= MAX_FAQ_TABS) {
      setLimitMessage(FAQ_TAB_LIMIT_MESSAGE);
      return false;
    }

    replaceTabs([...currentTabs, { articleId, title: normalizedTitle }]);
    setLimitMessage(null);
    return true;
  }, [replaceTabs]);

  const activateArticleTab = useCallback((articleId: string) => {
    navigate(`/articles/${articleId}`);
  }, [navigate]);

  const closeArticleTab = useCallback((articleId: string) => {
    const currentTabs = tabsRef.current;
    const closingIndex = currentTabs.findIndex((tab) => tab.articleId === articleId);
    if (closingIndex < 0) return;

    const nextTabs = currentTabs.filter((tab) => tab.articleId !== articleId);
    replaceTabs(nextTabs);
    setLimitMessage(null);

    if (activeArticleId !== articleId) return;
    const nextActive = nextTabs[closingIndex] ?? nextTabs[closingIndex - 1];
    navigate(nextActive ? `/articles/${nextActive.articleId}` : "/search");
  }, [activeArticleId, navigate, replaceTabs]);

  const value = useMemo<FaqTabsContextValue>(() => ({
    tabs,
    activeArticleId,
    limitMessage,
    openArticleTab,
    activateArticleTab,
    closeArticleTab,
    clearLimitMessage: () => setLimitMessage(null),
    saveViewScrollPosition,
    readViewScrollPosition,
  }), [
    tabs,
    activeArticleId,
    limitMessage,
    openArticleTab,
    activateArticleTab,
    closeArticleTab,
    saveViewScrollPosition,
    readViewScrollPosition,
  ]);

  return <FaqTabsContext.Provider value={value}>{children}</FaqTabsContext.Provider>;
}

export function useFaqTabs() {
  const value = useContext(FaqTabsContext);
  if (!value) throw new Error("FaqTabsProviderの内側でuseFaqTabsを使用してください。");
  return value;
}

export function useOptionalFaqTabs() {
  return useContext(FaqTabsContext);
}

export function useFaqViewScrollPosition(viewKey: string, ready = true) {
  const faqTabs = useOptionalFaqTabs();
  const saveViewScrollPosition = faqTabs?.saveViewScrollPosition;
  const readViewScrollPosition = faqTabs?.readViewScrollPosition;
  const currentKeyRef = useRef<string | null>(null);
  const restoredKeyRef = useRef<string | null>(null);

  useLayoutEffect(() => {
    if (!saveViewScrollPosition || !readViewScrollPosition) return undefined;

    if (currentKeyRef.current !== viewKey) {
      if (currentKeyRef.current) {
        saveViewScrollPosition(currentKeyRef.current, window.scrollY);
      }
      currentKeyRef.current = viewKey;
      restoredKeyRef.current = null;
      window.scrollTo({ top: readViewScrollPosition(viewKey), behavior: "auto" });
    }

    if (!ready || restoredKeyRef.current === viewKey) return undefined;
    const scrollY = readViewScrollPosition(viewKey);
    restoredKeyRef.current = viewKey;
    const frame = window.requestAnimationFrame(() => {
      window.scrollTo({ top: scrollY, behavior: "auto" });
    });
    return () => window.cancelAnimationFrame(frame);
  }, [readViewScrollPosition, ready, saveViewScrollPosition, viewKey]);

  useEffect(() => () => {
    if (saveViewScrollPosition && currentKeyRef.current) {
      saveViewScrollPosition(currentKeyRef.current, window.scrollY);
    }
  }, [saveViewScrollPosition]);
}

interface FaqArticleLinkProps extends Omit<LinkProps, "to"> {
  articleId: string;
  articleTitle: string;
  to?: LinkProps["to"];
}

export function FaqArticleLink({
  articleId,
  articleTitle,
  to,
  onClick,
  ...props
}: FaqArticleLinkProps) {
  const faqTabs = useOptionalFaqTabs();
  const handleClick = (event: MouseEvent<HTMLAnchorElement>) => {
    onClick?.(event);
    if (event.defaultPrevented || !faqTabs) return;
    if (!faqTabs.openArticleTab(articleId, articleTitle)) event.preventDefault();
  };

  return (
    <Link
      {...props}
      to={to ?? `/articles/${articleId}`}
      onClick={handleClick}
    />
  );
}

export function FaqTabBar() {
  const {
    tabs,
    activeArticleId,
    limitMessage,
    activateArticleTab,
    closeArticleTab,
    clearLimitMessage,
  } = useFaqTabs();

  if (tabs.length === 0 && !limitMessage) return null;

  return (
    <section className="faq-tabs-region" aria-label="開いているFAQ">
      {limitMessage && (
        <div className="faq-tab-limit-message" role="alert">
          <span>{limitMessage}</span>
          <button type="button" onClick={clearLimitMessage} aria-label="メッセージを閉じる">×</button>
        </div>
      )}
      {tabs.length > 0 && (
        <div
          className="faq-tabs"
          role="tablist"
          aria-label={`開いているFAQ ${tabs.length}件（最大${MAX_FAQ_TABS}件）`}
          style={{ gridTemplateColumns: `repeat(${tabs.length}, minmax(0, 260px))` }}
        >
          {tabs.map((tab) => {
            const active = tab.articleId === activeArticleId;
            return (
              <div className={`faq-tab${active ? " active" : ""}`} key={tab.articleId}>
                <button
                  type="button"
                  role="tab"
                  aria-selected={active}
                  title={tab.title}
                  onClick={() => activateArticleTab(tab.articleId)}
                >
                  {tab.title}
                </button>
                <button
                  type="button"
                  className="faq-tab-close"
                  aria-label={`「${tab.title}」のタブを閉じる`}
                  title="タブを閉じる"
                  onClick={() => closeArticleTab(tab.articleId)}
                >
                  ×
                </button>
              </div>
            );
          })}
        </div>
      )}
    </section>
  );
}
