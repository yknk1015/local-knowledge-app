import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { ColorThemeProvider } from "../app/ColorTheme";
import { SettingsPage } from "./SettingsPage";

const mocks = vi.hoisted(() => ({
  currentUser: {
    id: "initial-admin",
    loginId: "0000",
    displayName: "初期管理者",
    role: "admin",
    isActive: true,
  },
  open: vi.fn(),
  save: vi.fn(),
  getSystemInfo: vi.fn(),
    getStorageFolders: vi.fn().mockResolvedValue([]),
  getBackupOverview: vi.fn(),
  getPasswordPolicy: vi.fn(),
  savePasswordPolicy: vi.fn(),
  createFullBackup: vi.fn(),
  inspectBackup: vi.fn(),
  restoreBackup: vi.fn(),
  getSettings: vi.fn(),
  saveSettings: vi.fn(),
  hasCSharpBridge: vi.fn(),
  invokeCSharp: vi.fn(),
}));

vi.mock("../api/csharpBridge", () => ({
  hasCSharpBridge: mocks.hasCSharpBridge,
  invokeCSharp: mocks.invokeCSharp,
}));

vi.mock("@tauri-apps/plugin-dialog", () => ({
  open: mocks.open,
  save: mocks.save,
}));

vi.mock("../app/AuthContext", () => ({
  useAuth: () => ({ user: mocks.currentUser }),
}));

vi.mock("../api/knowledgeApi", async (importOriginal) => {
  const original = await importOriginal<typeof import("../api/knowledgeApi")>();
  return {
    ...original,
    knowledgeApi: {
      getRecoveryKeyStatus: vi.fn().mockResolvedValue({ hasKey: false, needsSetup: false, issuedAt: null }),
      getSystemInfo: mocks.getSystemInfo,
      getStorageFolders: mocks.getStorageFolders,
      getConnectionSettings: vi.fn().mockResolvedValue({ settings: null, activeShared: false }),
      getCodexLocation: vi.fn().mockResolvedValue({ root: "", generation: 0 }),
      getBackupOverview: mocks.getBackupOverview,
      getPasswordPolicy: mocks.getPasswordPolicy,
      savePasswordPolicy: mocks.savePasswordPolicy,
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
    mocks.hasCSharpBridge.mockReturnValue(false);
    Object.assign(mocks.currentUser, {
      id: "initial-admin",
      loginId: "0000",
      displayName: "初期管理者",
      role: "admin",
      isActive: true,
    });
    Element.prototype.scrollIntoView = vi.fn();
    mocks.getSystemInfo.mockResolvedValue({
      appVersion: "0.1.0",
      dataRoot: "C:\\AppData\\KnowledgeApp",
      databasePath: "C:\\AppData\\KnowledgeApp\\data\\knowledge.db",
      codexCategoryCatalogPath: "C:\\AppData\\KnowledgeApp\\codex-bridge\\categories.json",
      codexInboxPath: "C:\\AppData\\KnowledgeApp\\codex-inbox",
    });
    mocks.getBackupOverview.mockResolvedValue({
      estimatedBytes: 4096,
      counts: { articles: 4, categories: 2, attachments: 0, manuals: 0 },
      defaultDirectory: "D:\\Backup",
    });
    mocks.getPasswordPolicy.mockResolvedValue({ allowEmptyPasswords: true });
    mocks.savePasswordPolicy.mockImplementation(async (settings) => settings);
    mocks.getSettings.mockResolvedValue({ colorTheme: "green", showTopCategoryInTitle: true, showMascot: true });
    mocks.saveSettings.mockImplementation(async (settings) => settings);
    document.documentElement.dataset.colorTheme = "green";
  });

  it.each([1, 5, 6])("shows schema %i migration information before the C# restore confirmation", async (version) => {
    mocks.hasCSharpBridge.mockReturnValue(true);
    mocks.invokeCSharp.mockResolvedValue("C:\\synthetic\\legacy.faqbackup");
    mocks.inspectBackup.mockResolvedValue({
      sourcePath: "C:\\synthetic\\legacy.faqbackup", displayName: "合成旧版", createdAt: "2026-09-05T00:00:00Z",
      appVersion: "synthetic-legacy", schemaVersion: version, backupFormatVersion: 1, totalBytes: 4096,
      counts: { articles: 1, categories: 1, attachments: 0, manuals: 0 },
    });
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(false);
    try {
      renderPage();
      fireEvent.click(await screen.findByRole("button", { name: "バックアップから復元" }));
      const note = await screen.findByRole("note", { name: "旧版バックアップの移行" });
      expect(note).toHaveTextContent(`DB第${version}版を第8版へ移行して復元します。`);
      expect(note).toHaveTextContent("元のバックアップは変更しません。");
      expect(note).toHaveTextContent(version < 6 ? "利用者機能がない旧版" : "既存の利用者・パスワードを引き継ぎます");
      fireEvent.click(screen.getByRole("button", { name: "この内容を復元" }));
      expect(confirm).toHaveBeenCalledWith(expect.stringContaining(note.textContent ?? "missing notice"));
      expect(mocks.restoreBackup).not.toHaveBeenCalled();
    } finally { confirm.mockRestore(); }
  });

  it("applies and persists the blue color theme", async () => {
    renderPage();

    fireEvent.click(screen.getByRole("radio", { name: "ブルー" }));

    await waitFor(() => expect(document.documentElement.dataset.colorTheme).toBe("blue"));
    expect(mocks.saveSettings).toHaveBeenCalledWith({
      colorTheme: "blue",
      showTopCategoryInTitle: true,
      showMascot: true,
    });
    expect(screen.getByText("ブルーを使用中")).toBeInTheDocument();
  });

  it("restores the saved color theme when the page opens", async () => {
    mocks.getSettings.mockResolvedValue({ colorTheme: "blue", showTopCategoryInTitle: true, showMascot: true });
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
      showMascot: true,
    });
    expect(screen.getByText("現在はOFFです")).toBeInTheDocument();
  });

  it("turns the FAQ mascot off and persists the display setting", async () => {
    renderPage();

    const checkbox = await screen.findByRole("checkbox", { name: /マスコットを表示する/ });
    expect(checkbox).toBeChecked();
    fireEvent.click(checkbox);

    await waitFor(() => expect(checkbox).not.toBeChecked());
    expect(mocks.saveSettings).toHaveBeenCalledWith({
      colorTheme: "green",
      showTopCategoryInTitle: true,
      showMascot: false,
    });
  });

  it("shows the fixed Codex storage paths as read-only information", async () => {
    renderPage();

    const storage = await screen.findByRole("region", { name: "Codex連携用の保存先" });
    expect(storage).toHaveTextContent("参照のみ");
    expect(storage).toHaveTextContent("設定画面からは変更できません");
    expect(storage).toHaveTextContent("C:\\AppData\\KnowledgeApp\\codex-bridge\\categories.json");
    expect(storage).toHaveTextContent("C:\\AppData\\KnowledgeApp\\codex-inbox");
    expect(storage.querySelector("input, select, textarea, button")).toBeNull();
  });

  it("keeps backup and restore operations unavailable to a general user", async () => {
    Object.assign(mocks.currentUser, { id: "general-user", loginId: "1000", role: "user" });
    renderPage();

    expect(screen.queryByRole("button", { name: "フルバックアップを作成" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "バックアップから復元" })).not.toBeInTheDocument();
    expect(screen.getByText("利用者情報を含むため、バックアップと復元は管理者だけが操作できます。")).toBeInTheDocument();
    expect(mocks.getBackupOverview).not.toHaveBeenCalled();
    expect(mocks.getPasswordPolicy).not.toHaveBeenCalled();
  });

  it("requires passwords for future user changes after an admin turns empty passwords off", async () => {
    renderPage();

    const checkbox = await screen.findByRole("checkbox", { name: /空欄のパスワードを許可する/ });
    expect(checkbox).toBeChecked();
    fireEvent.click(checkbox);

    await waitFor(() => expect(checkbox).not.toBeChecked());
    expect(mocks.savePasswordPolicy).toHaveBeenCalledWith({ allowEmptyPasswords: false });
    expect(screen.getByText("現在は許可していません")).toBeInTheDocument();
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
    expect(screen.getByText(/会社管理の外部資料本体は含みません/)).toBeInTheDocument();
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

  it.each(["BK-011", "BK-006"])("clears a consumed C# confirmation after %s and requires fresh selection", async (code) => {
    mocks.hasCSharpBridge.mockReturnValue(true);
    mocks.invokeCSharp.mockResolvedValue("C:\\synthetic\\selected.faqbackup");
    const preview = {
      sourcePath: "C:\\synthetic\\selected.faqbackup", displayName: "合成確認対象", createdAt: "2026-09-05T00:00:00Z",
      appVersion: "synthetic", schemaVersion: 7, backupFormatVersion: 1, totalBytes: 4096,
      counts: { articles: 1, categories: 1, attachments: 0, manuals: 0 }, confirmationToken: "first-opaque-token",
    };
    mocks.inspectBackup.mockResolvedValueOnce(preview).mockResolvedValueOnce({ ...preview, confirmationToken: "fresh-opaque-token" });
    mocks.restoreBackup.mockRejectedValueOnce({ code, message: "確認が無効です。現在のデータは変更していません。", action: "バックアップを選び直してください。" })
      .mockResolvedValueOnce({ sourcePath: preview.sourcePath, safetyBackupPath: "C:\\synthetic\\safety.faqbackup", counts: preview.counts });
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(true);
    try {
      renderPage();
      fireEvent.click(await screen.findByRole("button", { name: "バックアップから復元" }));
      const restore = await screen.findByRole("button", { name: "この内容を復元" });
      expect(screen.getByRole("note", { name: "復元確認の有効期限" })).toHaveTextContent("10分間");
      fireEvent.click(restore);
      await waitFor(() => expect(screen.getByRole("alert")).toHaveTextContent(code));
      expect(mocks.restoreBackup).toHaveBeenNthCalledWith(1, preview.sourcePath, "first-opaque-token");
      expect(screen.queryByRole("button", { name: "この内容を復元" })).not.toBeInTheDocument();
      fireEvent.click(screen.getByRole("button", { name: "バックアップから復元" }));
      fireEvent.click(await screen.findByRole("button", { name: "この内容を復元" }));
      await waitFor(() => expect(mocks.restoreBackup).toHaveBeenNthCalledWith(2, preview.sourcePath, "fresh-opaque-token"));
      expect(await screen.findByText("バックアップから復元しました")).toBeInTheDocument();
    } finally { confirm.mockRestore(); }
  });
});
