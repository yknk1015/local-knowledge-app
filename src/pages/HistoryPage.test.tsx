import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { knowledgeApi } from "../api/knowledgeApi";
import { HistoryPage } from "./HistoryPage";

const emptyViewPage = { items: [], total: 0, page: 1, pageSize: 50 };

describe("HistoryPage", () => {
  afterEach(() => vi.restoreAllMocks());

  it("shows zero-result searches and applies text and period filters", async () => {
    const listSearchLogs = vi.spyOn(knowledgeApi, "listSearchLogs").mockResolvedValue({
      items: [{
        id: "log-1",
        queryText: "見つからない語",
        normalizedQuery: "見つからない語",
        scope: "all",
        categoryName: null,
        resultCount: 0,
        createdAt: "2026-08-16T12:00:00Z",
      }],
      total: 1,
      page: 1,
      pageSize: 50,
    });
    vi.spyOn(knowledgeApi, "listViewLogs").mockResolvedValue(emptyViewPage);
    render(<MemoryRouter><HistoryPage /></MemoryRouter>);

    expect(await screen.findByText("見つからない語")).toBeVisible();
    fireEvent.click(screen.getByRole("tab", { name: "0件検索" }));
    await waitFor(() => expect(listSearchLogs).toHaveBeenLastCalledWith("", undefined, undefined, true, 1));
    fireEvent.change(screen.getByLabelText("文字列"), { target: { value: "未解決" } });
    fireEvent.change(screen.getByLabelText("開始日"), { target: { value: "2026-08-01" } });
    fireEvent.change(screen.getByLabelText("終了日"), { target: { value: "2026-08-16" } });
    fireEvent.click(screen.getByRole("button", { name: "絞り込む" }));
    await waitFor(() => expect(listSearchLogs).toHaveBeenLastCalledWith(
      "未解決",
      "2026-08-01",
      "2026-08-16",
      true,
      1,
    ));
  });

  it("requires confirmation and deletes the active history type", async () => {
    vi.spyOn(knowledgeApi, "listSearchLogs").mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 50 });
    vi.spyOn(knowledgeApi, "listViewLogs").mockResolvedValue({ ...emptyViewPage, total: 3 });
    const remove = vi.spyOn(knowledgeApi, "deleteHistory").mockResolvedValue(3);
    vi.spyOn(window, "confirm").mockReturnValue(true);
    render(<MemoryRouter><HistoryPage /></MemoryRouter>);

    await screen.findByText("該当する履歴はありません");
    fireEvent.click(screen.getByRole("tab", { name: "閲覧履歴" }));
    fireEvent.change(screen.getByLabelText("開始日"), { target: { value: "2026-08-01" } });
    fireEvent.click(screen.getByRole("button", { name: "絞り込む" }));
    await waitFor(() => expect(screen.getByRole("button", { name: "指定期間を削除" })).toBeEnabled());
    fireEvent.click(screen.getByRole("button", { name: "指定期間を削除" }));

    await waitFor(() => expect(remove).toHaveBeenCalledWith("view", "2026-08-01", undefined, false));
    expect(await screen.findByText("閲覧履歴を3件削除しました。")).toBeVisible();
  });
});
