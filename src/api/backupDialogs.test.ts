import { beforeEach, describe, expect, it, vi } from "vitest";
import { selectFullBackupDestination, selectRestoreBackupSource } from "./backupDialogs";

const mocks = vi.hoisted(() => ({
  hasCSharpBridge: vi.fn(),
  invokeCSharp: vi.fn(),
  open: vi.fn(),
  save: vi.fn(),
}));

vi.mock("./csharpBridge", () => ({
  hasCSharpBridge: mocks.hasCSharpBridge,
  invokeCSharp: mocks.invokeCSharp,
}));

vi.mock("@tauri-apps/plugin-dialog", () => ({
  open: mocks.open,
  save: mocks.save,
}));

describe("backup dialogs", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("uses the C# host for backup save and restore source dialogs", async () => {
    mocks.hasCSharpBridge.mockReturnValue(true);
    mocks.invokeCSharp
      .mockResolvedValueOnce("D:\\Backup\\会社FAQ.faqbackup")
      .mockResolvedValueOnce("D:\\Backup\\復元元.faqbackup");

    await expect(selectFullBackupDestination("KnowledgeApp_20260830.faqbackup"))
      .resolves.toBe("D:\\Backup\\会社FAQ.faqbackup");
    await expect(selectRestoreBackupSource())
      .resolves.toBe("D:\\Backup\\復元元.faqbackup");
    expect(mocks.invokeCSharp).toHaveBeenNthCalledWith(
      1,
      "select_full_backup_destination",
      { defaultPath: "KnowledgeApp_20260830.faqbackup" },
    );
    expect(mocks.invokeCSharp).toHaveBeenNthCalledWith(2, "select_restore_backup_source");
    expect(mocks.save).not.toHaveBeenCalled();
    expect(mocks.open).not.toHaveBeenCalled();
  });

  it("keeps the existing Tauri dialogs outside the C# host", async () => {
    mocks.hasCSharpBridge.mockReturnValue(false);
    mocks.save.mockResolvedValue("D:\\Backup\\会社FAQ.faqbackup");
    mocks.open.mockResolvedValue("D:\\Backup\\復元元.faqbackup");

    await selectFullBackupDestination("KnowledgeApp_20260830.faqbackup");
    await selectRestoreBackupSource();

    expect(mocks.save).toHaveBeenCalledWith(expect.objectContaining({
      defaultPath: "KnowledgeApp_20260830.faqbackup",
      filters: [{ name: "KnowledgeAppフルバックアップ", extensions: ["faqbackup"] }],
    }));
    expect(mocks.open).toHaveBeenCalledWith(expect.objectContaining({
      multiple: false,
      directory: false,
    }));
  });
});
