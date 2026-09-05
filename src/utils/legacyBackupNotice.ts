export function legacyBackupNotice(schemaVersion: number, csharpHost: boolean): string | null {
  if (!csharpHost || !Number.isInteger(schemaVersion) || schemaVersion < 1 || schemaVersion > 6) return null;
  const migration = `DB第${schemaVersion}版を第7版へ移行して復元します。元のバックアップは変更しません。`;
  const authentication = schemaVersion < 6
    ? "利用者機能がない旧版のため、復元後は初期管理者「0000」・空パスワードでログインしてください。"
    : "既存の利用者・パスワードを引き継ぎます。利用者情報がまだない正規の旧版に限り、初期管理者「0000」・空パスワードを作成します。";
  return `${migration}${authentication}`;
}
