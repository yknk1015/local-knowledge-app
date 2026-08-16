import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { knowledgeApi } from "../api/knowledgeApi";
import { JsonTransferPage } from "./JsonTransferPage";

const dialog = vi.hoisted(() => ({ open: vi.fn(), save: vi.fn() }));
vi.mock("@tauri-apps/plugin-dialog", () => dialog);

const counts = { categories: 2, articles: 3, tags: 1, synonymGroups: 1, relations: 1, mergeRelations: 0 };

describe("JsonTransferPage", () => {
  afterEach(() => {
    vi.restoreAllMocks();
    dialog.open.mockReset();
    dialog.save.mockReset();
  });

  it("normalizes the export filename and shows exported entity counts", async () => {
    dialog.save.mockResolvedValue("C:\\Data\\knowledge.json");
    const exportJson = vi.spyOn(knowledgeApi, "exportJson").mockResolvedValue({
      destinationPath: "C:\\Data\\knowledge.knowledge-export.json",
      counts,
    });
    render(<MemoryRouter><JsonTransferPage /></MemoryRouter>);

    fireEvent.click(screen.getByRole("button", { name: "保存先を選んで書き出す" }));
    await waitFor(() => expect(exportJson).toHaveBeenCalledWith("C:\\Data\\knowledge.knowledge-export.json"));
    expect(await screen.findByText("JSONを書き出しました。")).toBeVisible();
    expect(screen.getByText("3件")).toBeVisible();
  });

  it("previews and imports only after confirmation", async () => {
    dialog.open.mockResolvedValue("C:\\Data\\in.knowledge-export.json");
    vi.spyOn(knowledgeApi, "inspectJson").mockResolvedValue({
      sourcePath: "C:\\Data\\in.knowledge-export.json",
      fileSha256: "abc",
      counts,
      createCount: 4,
      updateCount: 2,
      unchangedCount: 2,
      errorCount: 0,
      errors: [],
    });
    const importJson = vi.spyOn(knowledgeApi, "importJson").mockResolvedValue({
      sourcePath: "C:\\Data\\in.knowledge-export.json",
      safetyBackupPath: "C:\\Backup\\before.faqbackup",
      createdCount: 4,
      updatedCount: 2,
      unchangedCount: 2,
    });
    vi.spyOn(window, "confirm").mockReturnValue(true);
    render(<MemoryRouter><JsonTransferPage /></MemoryRouter>);

    fireEvent.click(screen.getByRole("button", { name: "JSONファイルを選択" }));
    expect(await screen.findByRole("heading", { name: "取込プレビュー" })).toBeVisible();
    fireEvent.click(screen.getByRole("button", { name: "この内容で取り込む" }));
    await waitFor(() => expect(importJson).toHaveBeenCalledWith("C:\\Data\\in.knowledge-export.json", "abc"));
    expect(await screen.findByText("JSONを取り込みました。")).toBeVisible();
  });
});
