import { describe, expect, it } from "vitest";
import { legacyBackupNotice } from "./legacyBackupNotice";

describe("legacy backup migration notice", () => {
  it.each([1, 2, 3, 4, 5])("explains pre-auth schema %i without modifying the source", (version) => {
    expect(legacyBackupNotice(version, true)).toContain(`DB第${version}版を第7版`);
    expect(legacyBackupNotice(version, true)).toContain("元のバックアップは変更しません");
    expect(legacyBackupNotice(version, true)).toContain("初期管理者「0000」・空パスワード");
  });
  it("preserves existing credentials in schema 6 and describes only the empty legacy exception", () => {
    expect(legacyBackupNotice(6, true)).toContain("既存の利用者・パスワードを引き継ぎます");
    expect(legacyBackupNotice(6, true)).toContain("利用者情報がまだない正規の旧版に限り");
  });
  it.each([0, -1, 1.5, 7, 8, NaN, Infinity])("does not offer migration for %s", (version) => {
    expect(legacyBackupNotice(version, true)).toBeNull();
  });
  it("does not change the Tauri host's notice", () => {
    expect(legacyBackupNotice(5, false)).toBeNull();
  });
});
