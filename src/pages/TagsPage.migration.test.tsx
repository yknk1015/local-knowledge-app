import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { TagsPage } from "./TagsPage";

const mocks = vi.hoisted(() => ({ listTags: vi.fn(), saveTag: vi.fn(), deleteTag: vi.fn() }));

vi.mock("../api/knowledgeApi", async (importOriginal) => {
  const original = await importOriginal<typeof import("../api/knowledgeApi")>();
  return { ...original, knowledgeApi: mocks };
});

const tags = [
  { id: "synthetic-used", name: "合成WiFi", usageCount: 2 },
  { id: "synthetic-unused", name: "合成未使用", usageCount: 0 },
];

describe("TagsPage C# migration contracts", () => {
  beforeEach(() => {
    vi.resetAllMocks();
    mocks.listTags.mockResolvedValue(tags);
    vi.spyOn(window, "confirm").mockReturnValue(true);
  });

  it("shows usage counts and filters full-width names without changing the selection", async () => {
    render(<TagsPage />);
    await screen.findByRole("button", { name: /合成WiFi\s*2\s*件のFAQで使用/ });
    expect(screen.getByLabelText(/タグ名/)).toHaveValue("合成WiFi");
    fireEvent.change(screen.getByLabelText("タグを検索"), { target: { value: "ｗｉｆｉ" } });
    expect(screen.queryByRole("button", { name: /合成未使用\s*0\s*件のFAQで使用/ })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: /合成WiFi\s*2\s*件のFAQで使用/ })).toBeVisible();
  });

  it("creates a tag with no existing ID", async () => {
    mocks.saveTag.mockResolvedValue({ id: "synthetic-new", name: "合成追加", usageCount: 0 });
    render(<TagsPage />);
    await screen.findByRole("button", { name: "新しいタグ" });
    fireEvent.click(screen.getByRole("button", { name: "新しいタグ" }));
    fireEvent.change(screen.getByLabelText(/タグ名/), { target: { value: "合成追加" } });
    fireEvent.click(screen.getByRole("button", { name: "タグを保存" }));
    await waitFor(() => expect(mocks.saveTag).toHaveBeenCalledWith(undefined, "合成追加"));
    expect(await screen.findByRole("status")).toHaveTextContent("タグを追加しました");
    expect(screen.getByRole("button", { name: /合成追加\s*0\s*件のFAQで使用/ })).toBeVisible();
  });

  it("renames a used tag with its original ID", async () => {
    mocks.saveTag.mockResolvedValue({ ...tags[0], name: "合成ネットワーク" });
    render(<TagsPage />);
    await screen.findByDisplayValue("合成WiFi");
    fireEvent.change(screen.getByLabelText(/タグ名/), { target: { value: "合成ネットワーク" } });
    fireEvent.click(screen.getByRole("button", { name: "タグを保存" }));
    await waitFor(() => expect(mocks.saveTag).toHaveBeenCalledWith("synthetic-used", "合成ネットワーク"));
    expect(await screen.findByRole("status")).toHaveTextContent("使用中のFAQにも反映されます");
  });

  it("keeps the edited name and shows duplicate errors", async () => {
    mocks.saveTag.mockRejectedValue({ code: "TAG-002", message: "同じ名前のタグがすでにあります。", action: "別のタグ名にしてください。" });
    render(<TagsPage />);
    await screen.findByDisplayValue("合成WiFi");
    fireEvent.change(screen.getByLabelText(/タグ名/), { target: { value: "合成重複" } });
    fireEvent.click(screen.getByRole("button", { name: "タグを保存" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("TAG-002");
    expect(screen.getByLabelText(/タグ名/)).toHaveValue("合成重複");
    expect(screen.getByRole("button", { name: "タグを保存" })).toBeEnabled();
  });

  it("keeps a used tag visible when deletion is refused", async () => {
    mocks.deleteTag.mockRejectedValue({ code: "TAG-003", message: "このタグはFAQで使用されています。", action: "先にFAQからタグを外してください。" });
    render(<TagsPage />);
    await screen.findByDisplayValue("合成WiFi");
    fireEvent.click(screen.getByRole("button", { name: "削除" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("TAG-003");
    expect(screen.getByRole("button", { name: /合成WiFi\s*2\s*件のFAQで使用/ })).toBeVisible();
  });

  it("does not delete when the confirmation is cancelled", async () => {
    vi.mocked(window.confirm).mockReturnValue(false);
    render(<TagsPage />);
    await screen.findByDisplayValue("合成WiFi");
    fireEvent.click(screen.getByRole("button", { name: "削除" }));
    expect(mocks.deleteTag).not.toHaveBeenCalled();
  });

  it("deletes only the selected unused tag after confirmation", async () => {
    mocks.deleteTag.mockResolvedValue(undefined);
    render(<TagsPage />);
    fireEvent.click(await screen.findByRole("button", { name: /合成未使用\s*0\s*件のFAQで使用/ }));
    fireEvent.click(screen.getByRole("button", { name: "削除" }));
    await waitFor(() => expect(mocks.deleteTag).toHaveBeenCalledWith("synthetic-unused"));
    expect(await screen.findByRole("status")).toHaveTextContent("タグを削除しました");
    expect(screen.queryByRole("button", { name: /合成未使用\s*0\s*件のFAQで使用/ })).not.toBeInTheDocument();
    expect(screen.getByLabelText(/タグ名/)).toHaveValue("合成WiFi");
  });

  it("keeps empty names disabled and reports a list load failure", async () => {
    mocks.listTags.mockRejectedValue({ code: "SYS-001", message: "一覧を取得できませんでした。", action: "もう一度お試しください。" });
    render(<TagsPage />);
    expect(await screen.findByRole("alert")).toHaveTextContent("SYS-001");
    expect(screen.getByRole("button", { name: "タグを保存" })).toBeDisabled();
  });
});
