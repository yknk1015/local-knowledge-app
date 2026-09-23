import { useEffect, useState } from "react";
import { knowledgeApi, toAppError } from "../api/knowledgeApi";
import type { AppError, StorageFolder } from "../types/domain";
import { ErrorState } from "./Feedback";

const labels: Record<string, string> = {
  "backup-export": "バックアップの保存先", "backup-import": "復元ファイルの選択先",
  "json-export": "JSONの書出先", "json-import": "JSONの取込元",
  "csv-export": "CSVの書出先", "csv-import": "CSVの取込元",
};
export function StorageSettingsPanel({ server = false }: { server?: boolean }) {
  const [folders, setFolders] = useState<StorageFolder[]>([]);
  const [error, setError] = useState<AppError | null>(null);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState("");
  useEffect(() => { knowledgeApi.getStorageFolders(server).then(items => setFolders(server ? items.filter(item => item.purpose === "backup-export") : items)).catch(e => setError(toAppError(e))); }, []);
  const act = async (folder: StorageFolder, action: "choose" | "check" | "reset") => {
    setBusy(true); setError(null); setNotice("");
    try {
      if (action === "check") {
        const result = await knowledgeApi.checkStorageFolder(folder.purpose, folder.customPath, server);
        setNotice(`${labels[folder.purpose]}：接続${result.writable ? "・書込" : ""}を確認しました。${result.availableBytes === null ? "" : `空き容量 ${(result.availableBytes / 1024 ** 3).toFixed(1)} GB`}`);
      } else {
        const path = action === "reset" ? null : await knowledgeApi.selectStorageFolder(server);
        if (action === "choose" && path === null) return;
        const changed = await knowledgeApi.saveStorageFolder(folder.purpose, path, server);
        setFolders(current => current.map(item => item.purpose === changed.purpose ? changed : item));
        setNotice(`${labels[folder.purpose]}を変更しました。既存ファイルは移動していません。`);
      }
    } catch (caught) { setError(toAppError(caught)); }
    finally { setBusy(false); }
  };
  return <section className="panel storage-settings" aria-label="保存先の設定">
    <h2>{server ? "共有サーバーのバックアップ退避先" : "保存先の設定"}</h2>
    <p>{server ? "共有サーバーから見たフォルダーパスを指定します。端末へのダウンロード先は保存時に選択します。" : "この端末の既定フォルダーを指定します。"}実データ・認証情報はGitリポジトリ内へ保存できません。設定の変更では既存ファイルを移動しません。</p>
    {error && <ErrorState error={error} />}
    {notice && <p role="status">{notice}</p>}
    {folders.map(folder => <div key={folder.purpose} style={{ marginBlock: "1rem" }}>
      <strong>{labels[folder.purpose]}</strong><small>（{folder.mode === "custom" ? "カスタム" : folder.mode === "unset" ? "未設定（バックアップは以前の保存先を優先）" : "標準"}）</small>
      <p style={{ overflowWrap: "anywhere" }}>{folder.effectivePath}</p>
      <div className="dialog-actions">
        <button className="button secondary" disabled={busy} onClick={() => void act(folder, "choose")}>フォルダーを選ぶ</button>
        <button className="button secondary" disabled={busy} onClick={() => void act(folder, "check")}>接続・権限を確認</button>
        <button className="button secondary" disabled={busy} onClick={() => void act(folder, "reset")}>標準に戻す</button>
      </div>
    </div>)}
  </section>;
}
