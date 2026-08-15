import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { ColorThemeProvider } from "../app/ColorTheme";
import { SettingsPage } from "./SettingsPage";

const mocks = vi.hoisted(() => ({
  open: vi.fn(),
  save: vi.fn(),
  getSystemInfo: vi.fn(),
  getBackupOverview: vi.fn(),
  createFullBackup: vi.fn(),
  inspectBackup: vi.fn(),
  restoreBackup: vi.fn(),
  getSettings: vi.fn(),
  saveSettings: vi.fn(),
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
      getSettings: mocks.getSettings,
      saveSettings: mocks.saveSettings,
    },
  };
});

function renderPage() {
  return render(
    <ColorThemeProvider>
      <SettingsPage />
    </ColorThemeProvider>,
  );
}

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
    mocks.getSettings.mockResolvedValue({ colorTheme: "green", showTopCategoryInTitle: true });
    mocks.saveSettings.mockImplementation(async (settings) => settings);
    document.documentElement.dataset.colorTheme = "green";
  });

  it("applies and persists the blue color theme", async () => {
    renderPage();

    fireEvent.click(screen.getByRole("radio", { name: "ブルー" }));

    await waitFor(() => expect(document.documentElement.dataset.colorTheme).toBe("blue"));
    expect(mocks.saveSettings).toHaveBeenCalledWith({
      colorTheme: "blue",
      showTopCategoryInTitle: true,
    });
    expect(screen.getByText("ブルーを使用中")).toBeInTheDocument();
  });

  it("restores the saved color theme when the page opens", async () => {
    mocks.getSettings.mockResolvedValue({ colorTheme: "blue", showTopCategoryInTitle: true });
    renderPage();

    await waitFor(() => expect(document.documentElement.dataset.colorTheme).toBe("blue"));
    expect(screen.getByRole("radio", { name: "ブルー" })).toBeChecked();
    expect(mocks.saveSettings).not.toHaveBeenCalled();
  });

  it("turns the top category title prefix off without changing saved FAQ titles", async () => {
    renderPage();

    const checkbox = await screen.findByRole("checkbox", { name: /トップ分類名を表示する/ });
    expect(checkbox).toBeChecked();
    fireEvent.click(checkbox);

    await waitFor(() => expect(checkbox).not.toBeChecked());
    expect(mocks.saveSettings).toHaveBeenCalledWith({
      colorTheme: "green",
      showTopCategoryInTitle: false,
    });
    expect(screen.getByText("現在はOFFです")).toBeInTheDocument();
  });

  it("returns to the previous theme when saving fails", async () => {
    mocks.saveSettings.mockRejectedValue({
      code: "SET-002",
      message: "画面の表示設定を保存できませんでした。",
      action: "設定内容を確認して、もう一度保存してください。",
    });
    renderPage();

    fireEvent.click(screen.getByRole("radio", { name: "ブルー" }));

    await waitFor(() => expect(screen.getByRole("alert")).toHaveTextContent("SET-002"));
    expect(document.documentElement.dataset.colorTheme).toBe("green");
    expect(screen.getByRole("radio", { name: "グリーン" })).toBeChecked();
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
    renderPage();

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
    renderPage();

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
    renderPage();

    fireEvent.click(screen.getByRole("button", { name: "バックアップから復元" }));

    await waitFor(() => expect(screen.getByRole("alert")).toHaveTextContent("BK-006"));
    expect(screen.getByRole("alert")).toHaveTextContent("破損しています");
  });
});
