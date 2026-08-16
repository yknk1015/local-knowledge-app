import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { knowledgeApi } from "../api/knowledgeApi";
import { TagsPage } from "./TagsPage";

describe("TagsPage", () => {
  afterEach(() => vi.restoreAllMocks());

  it("creates and renames tags while showing their FAQ usage", async () => {
    const existing = {
      id: "tag-1",
      name: "画面",
      usageCount: 2,
      updatedAt: "2026-08-16T00:00:00Z",
    };
    vi.spyOn(knowledgeApi, "listTags").mockResolvedValue([existing]);
    const save = vi.spyOn(knowledgeApi, "saveTag").mockResolvedValue({ ...existing, name: "ディスプレイ" });
    render(<TagsPage />);

    expect(await screen.findByText("2件のFAQで使用")).toBeVisible();
    expect(screen.getByText(/名前の変更は使用中のFAQへ一括反映/)).toBeVisible();
    fireEvent.change(screen.getByLabelText(/タグ名/), { target: { value: "ディスプレイ" } });
    fireEvent.click(screen.getByRole("button", { name: "タグを保存" }));

    await waitFor(() => expect(save).toHaveBeenCalledWith(existing.id, "ディスプレイ"));
    expect(await screen.findByText(/使用中のFAQにも反映/)).toBeVisible();
  });

  it("shows the reason when a tag in use cannot be deleted", async () => {
    const existing = {
      id: "tag-1",
      name: "ネットワーク",
      usageCount: 1,
      updatedAt: "2026-08-16T00:00:00Z",
    };
    vi.spyOn(knowledgeApi, "listTags").mockResolvedValue([existing]);
    vi.spyOn(knowledgeApi, "deleteTag").mockRejectedValue({
      code: "TAG-003",
      message: "このタグは1件のFAQで使用されているため削除できません。",
      action: "使用中のFAQからタグを外してから削除してください。",
    });
    vi.spyOn(window, "confirm").mockReturnValue(true);
    render(<TagsPage />);

    fireEvent.click(await screen.findByRole("button", { name: "削除" }));
    expect(await screen.findByText(/1件のFAQで使用されているため削除できません/)).toBeVisible();
  });
});
