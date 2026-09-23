import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter, useLocation, useNavigate } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import {
  faqArticleScrollKey,
  FAQ_SEARCH_SCROLL_KEY,
  FAQ_TAB_LIMIT_MESSAGE,
  FaqTabBar,
  FaqTabsProvider,
  MAX_FAQ_TABS,
  useFaqViewScrollPosition,
  useFaqTabs,
} from "./FaqTabs";

function Harness() {
  const { tabs, openArticleTab } = useFaqTabs();
  const location = useLocation();
  return (
    <>
      <div data-testid="location">{location.pathname}</div>
      <div data-testid="count">{tabs.length}</div>
      {Array.from({ length: MAX_FAQ_TABS + 1 }, (_, index) => (
        <button
          type="button"
          key={index}
          onClick={() => openArticleTab(`faq-${index + 1}`, `FAQ ${index + 1}`)}
        >
          FAQ {index + 1}を開く
        </button>
      ))}
      <FaqTabBar />
    </>
  );
}

function renderTabs() {
  render(
    <MemoryRouter initialEntries={["/search"]}>
      <FaqTabsProvider>
        <Harness />
      </FaqTabsProvider>
    </MemoryRouter>,
  );
}

function ScrollHarness() {
  const { openArticleTab, activateArticleTab } = useFaqTabs();
  const location = useLocation();
  const navigate = useNavigate();
  const match = /^\/articles\/([^/]+)$/.exec(location.pathname);
  const viewKey = match ? faqArticleScrollKey(decodeURIComponent(match[1]!)) : FAQ_SEARCH_SCROLL_KEY;
  useFaqViewScrollPosition(viewKey);

  const open = (articleId: string) => {
    if (openArticleTab(articleId, articleId.toUpperCase())) activateArticleTab(articleId);
  };

  return (
    <>
      <div data-testid="location">{location.pathname}</div>
      <button type="button" onClick={() => open("faq-1")}>FAQ 1を開く</button>
      <button type="button" onClick={() => open("faq-2")}>FAQ 2を開く</button>
      <button type="button" onClick={() => navigate("/search")}>FAQを探す</button>
      <FaqTabBar />
    </>
  );
}

afterEach(() => {
  vi.restoreAllMocks();
});

describe("FaqTabs", () => {
  it("同じFAQは既存タブを使い、異なるFAQを最大10件まで開く", () => {
    renderTabs();

    fireEvent.click(screen.getByRole("button", { name: "FAQ 1を開く" }));
    fireEvent.click(screen.getByRole("button", { name: "FAQ 1を開く" }));
    expect(screen.getByTestId("count")).toHaveTextContent("1");

    for (let index = 2; index <= MAX_FAQ_TABS; index += 1) {
      fireEvent.click(screen.getByRole("button", { name: `FAQ ${index}を開く` }));
    }
    expect(screen.getByTestId("count")).toHaveTextContent(String(MAX_FAQ_TABS));
    expect(screen.getAllByRole("tab")).toHaveLength(MAX_FAQ_TABS);
    expect((screen.getByRole("tablist") as HTMLDivElement).style.gridTemplateColumns)
      .toBe("repeat(10, minmax(0, 260px))");
  });

  it("11件目では既存タブを閉じず、所定メッセージを表示する", () => {
    renderTabs();

    for (let index = 1; index <= MAX_FAQ_TABS + 1; index += 1) {
      fireEvent.click(screen.getByRole("button", { name: `FAQ ${index}を開く` }));
    }

    expect(screen.getByTestId("count")).toHaveTextContent(String(MAX_FAQ_TABS));
    expect(screen.getByRole("alert")).toHaveTextContent(FAQ_TAB_LIMIT_MESSAGE);
    expect(screen.queryByRole("tab", { name: "FAQ 11" })).not.toBeInTheDocument();
  });

  it("表示中タブを閉じると隣接タブへ移り、最後は検索へ戻る", () => {
    renderTabs();
    fireEvent.click(screen.getByRole("button", { name: "FAQ 1を開く" }));
    fireEvent.click(screen.getByRole("button", { name: "FAQ 2を開く" }));

    fireEvent.click(screen.getByRole("tab", { name: "FAQ 2" }));
    expect(screen.getByTestId("location")).toHaveTextContent("/articles/faq-2");

    fireEvent.click(screen.getByRole("button", { name: "「FAQ 2」のタブを閉じる" }));
    expect(screen.getByTestId("location")).toHaveTextContent("/articles/faq-1");

    fireEvent.click(screen.getByRole("button", { name: "「FAQ 1」のタブを閉じる" }));
    expect(screen.getByTestId("location")).toHaveTextContent("/search");
    expect(screen.getByTestId("count")).toHaveTextContent("0");
  });

  it("FAQ一覧とFAQごとに個別の縦スクロール位置を復元する", async () => {
    let currentScrollY = 0;
    Object.defineProperty(window, "scrollY", {
      configurable: true,
      get: () => currentScrollY,
    });
    const scrollTo = vi.spyOn(window, "scrollTo").mockImplementation(((optionsOrX?: ScrollToOptions | number, y?: number) => {
      currentScrollY = typeof optionsOrX === "number"
        ? Math.max(0, y ?? 0)
        : Math.max(0, optionsOrX?.top ?? currentScrollY);
    }) as typeof window.scrollTo);

    render(
      <MemoryRouter initialEntries={["/search"]}>
        <FaqTabsProvider>
          <ScrollHarness />
        </FaqTabsProvider>
      </MemoryRouter>,
    );
    await waitFor(() => expect(scrollTo).toHaveBeenLastCalledWith({ top: 0, behavior: "auto" }));

    currentScrollY = 420;
    fireEvent.click(screen.getByRole("button", { name: "FAQ 1を開く" }));
    await waitFor(() => expect(screen.getByTestId("location")).toHaveTextContent("/articles/faq-1"));

    currentScrollY = 610;
    fireEvent.click(screen.getByRole("button", { name: "FAQ 2を開く" }));
    await waitFor(() => expect(screen.getByTestId("location")).toHaveTextContent("/articles/faq-2"));

    currentScrollY = 730;
    scrollTo.mockClear();
    fireEvent.click(screen.getByRole("tab", { name: "FAQ-1" }));
    await waitFor(() => expect(scrollTo).toHaveBeenLastCalledWith({ top: 610, behavior: "auto" }));

    scrollTo.mockClear();
    fireEvent.click(screen.getByRole("button", { name: "FAQを探す" }));
    await waitFor(() => expect(scrollTo).toHaveBeenLastCalledWith({ top: 420, behavior: "auto" }));
  });
});
