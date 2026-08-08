import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { SettingsPage } from "./SettingsPage";

const mocks = vi.hoisted(() => ({
  open: vi.fn(),
  save: vi.fn(),
  getSystemInfo: vi.fn(),
  getBackupOverview: vi.fn(),
  createFullBackup: vi.fn(),
  inspectBackup: vi.fn(),
  restoreBackup: vi.fn(),
}));

vi.mock("@tauri-apps/plugin-dialog", () => ({
  open: mocks.open,
  save: mocks.save,
}));

vi.mock("../api/knowledgeApi", async (importOriginal) => {
  const original = await importOriginal<typeof import("../api/knowledgeApi")>();
  return {
    ...original,
    knowledgeApi: {
      getSystemInfo: mocks.getSystemInfo,
      getBackupOverview: mocks.getBackupOverview,
      createFullBackup: mocks.createFullBackup,
      inspectBackup: mocks.inspectBackup,
      restoreBackup: mocks.restoreBackup,
    },
  };
});

describe("SettingsPage backup operations", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    Element.prototype.scrollIntoView = vi.fn();
    mocks.getSystemInfo.mockResolvedValue({
      appVersion: "0.1.0",
      dataRoot: "C:\\AppData\\KnowledgeApp",
      databasePath: "C:\\AppData\\KnowledgeApp\\data\\knowledge.db",
    });
    mocks.getBackupOverview.mockResolvedValue({
      estimatedBytes: 4096,
      counts: { articles: 4, categories: 2, attachments: 0, manuals: 0 },
      defaultDirectory: "D:\\Backup",
    });
  });

  it("creates a full backup at the path selected by the user", async () => {
    mocks.save.mockResolvedValue("D:\\Backup\\会社FAQ.faqbackup");
    mocks.createFullBackup.mockResolvedValue({
      destinationPath: "D:\\Backup\\会社FAQ.faqbackup",
      displayName: "会社FAQ",
      createdAt: "2026-08-08T12:00:00Z",
      totalBytes: 2048,
      counts: { articles: 4, categories: 2, attachments: 0, manuals: 0 },
    });
    render(<SettingsPage />);

    fireEvent.click(screen.getByRole("button", { name: "フルバックアップを作成" }));

    expect(await screen.findByText("フルバックアップを作成しました")).toBeInTheDocument();
    expect(mocks.createFullBackup).toHaveBeenCalledWith(
      "D:\\Backup\\会社FAQ.faqbackup",
      "会社FAQ",
    );
    expect(screen.getByText("FAQ 4件・2.0 KB")).toBeInTheDocument();
  });

  it("shows verified backup contents before restoration", async () => {
    mocks.open.mockResolvedValue("D:\\Backup\\会社FAQ.faqbackup");
    mocks.inspectBackup.mockResolvedValue({
      sourcePath: "D:\\Backup\\会社FAQ.faqbackup",
      displayName: "会社FAQ",
      createdAt: "2026-08-08T12:00:00Z",
      appVersion: "0.1.0",
      schemaVersion: 1,
      backupFormatVersion: 1,
      totalBytes: 1024,
      counts: { articles: 7, categories: 3, attachments: 2, manuals: 1 },
    });
    render(<SettingsPage />);

    fireEvent.click(screen.getByRole("button", { name: "バックアップから復元" }));

    expect(await screen.findByText("復元前の確認")).toBeInTheDocument();
    expect(screen.getByText("会社FAQ")).toBeInTheDocument();
    expect(screen.getByText("7件")).toBeInTheDocument();
    expect(mocks.restoreBackup).not.toHaveBeenCalled();
  });

  it("shows the rejection reason when a backup is invalid", async () => {
    mocks.open.mockResolvedValue("D:\\Backup\\broken.faqbackup");
    mocks.inspectBackup.mockRejectedValue({
      code: "BK-006",
      message: "バックアップ内のファイルが破損しています。",
      action: "別のバックアップファイルを選択してください。",
    });
    render(<SettingsPage />);

    fireEvent.click(screen.getByRole("button", { name: "バックアップから復元" }));

    await waitFor(() => expect(screen.getByRole("alert")).toHaveTextContent("BK-006"));
    expect(screen.getByRole("alert")).toHaveTextContent("破損しています");
  });
});
