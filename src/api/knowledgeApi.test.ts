import { beforeEach, describe, expect, it, vi } from "vitest";
import { knowledgeApi, toAppError } from "./knowledgeApi";

const mocks = vi.hoisted(() => ({ invoke: vi.fn(), invokeCSharp: vi.fn(), hasCSharpBridge: vi.fn() }));
vi.mock("@tauri-apps/api/core", () => ({ invoke: mocks.invoke }));
vi.mock("./csharpBridge", () => ({ invokeCSharp: mocks.invokeCSharp, hasCSharpBridge: mocks.hasCSharpBridge }));

describe("toAppError", () => {
  it("keeps a structured application error", () => {
    const result = toAppError({
      code: "ART-001",
      message: "タイトルが必要です。",
      action: "タイトルを入力してください。",
    });

    expect(result).toEqual({
      code: "ART-001",
      message: "タイトルが必要です。",
      action: "タイトルを入力してください。",
    });
  });

  it("parses errors serialized by the desktop bridge", () => {
    const result = toAppError(
      JSON.stringify({
        code: "DB-001",
        message: "データベースを開けません。",
        action: "保存先を確認してください。",
      }),
    );

    expect(result.code).toBe("DB-001");
    expect(result.action).toContain("保存先");
  });

  it("does not expose unknown technical objects", () => {
    const result = toAppError({ stack: "sensitive stack trace" });

    expect(result.code).toBe("SYS-001");
    expect(result.message).not.toContain("stack");
  });
});

describe("backup confirmation transport", () => {
  beforeEach(() => { vi.clearAllMocks(); window.location.hash = "/settings"; });

  it("sends the opaque confirmation only inside the typed C# restore input", async () => {
    mocks.hasCSharpBridge.mockReturnValue(true);
    mocks.invokeCSharp.mockResolvedValue({ sourcePath: "C:\\synthetic\\saved.faqbackup" });
    await knowledgeApi.restoreBackup("C:\\synthetic\\saved.faqbackup", "synthetic-opaque-token");
    expect(mocks.invokeCSharp).toHaveBeenCalledWith("restore_backup", {
      input: { path: "C:\\synthetic\\saved.faqbackup", confirmationToken: "synthetic-opaque-token" },
    });
    expect(mocks.invoke).not.toHaveBeenCalled();
    expect(window.location.hash).toBe("#/login");
  });

  it("preserves the Tauri path-only contract even when an optional token was supplied", async () => {
    mocks.hasCSharpBridge.mockReturnValue(false);
    mocks.invoke.mockResolvedValue({ sourcePath: "C:\\synthetic\\saved.faqbackup" });
    await knowledgeApi.restoreBackup("C:\\synthetic\\saved.faqbackup", "never-send-to-tauri");
    expect(mocks.invoke).toHaveBeenCalledWith("restore_backup", { path: "C:\\synthetic\\saved.faqbackup" });
    expect(mocks.invokeCSharp).not.toHaveBeenCalled();
  });

  it("does not expire the login or navigate after C# refuses a stale confirmation", async () => {
    mocks.hasCSharpBridge.mockReturnValue(true);
    const problem = { code: "BK-011", message: "確認が無効です。", action: "選び直してください。" };
    mocks.invokeCSharp.mockRejectedValue(problem);
    const expired = vi.fn(); window.addEventListener("knowledge-auth-expired", expired);
    try {
      await expect(knowledgeApi.restoreBackup("C:\\synthetic\\saved.faqbackup")).rejects.toEqual(problem);
      expect(mocks.invokeCSharp).toHaveBeenCalledWith("restore_backup", {
        input: { path: "C:\\synthetic\\saved.faqbackup", confirmationToken: null },
      });
      expect(expired).not.toHaveBeenCalled();
      expect(window.location.hash).toBe("#/settings");
    } finally { window.removeEventListener("knowledge-auth-expired", expired); }
  });
});
