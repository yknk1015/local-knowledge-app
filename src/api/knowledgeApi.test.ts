import { describe, expect, it } from "vitest";
import { toAppError } from "./knowledgeApi";

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
