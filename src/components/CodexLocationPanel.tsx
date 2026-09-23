import { useEffect, useState } from "react";
import { knowledgeApi, toAppError } from "../api/knowledgeApi";
import type { AppError } from "../types/domain";
import { ErrorState } from "./Feedback";
export function CodexLocationPanel() {
  const [root, setRoot] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<AppError | null>(null);
  const [notice, setNotice] = useState("");
  useEffect(() => { knowledgeApi.getCodexLocation().then(v => setRoot(v.root)).catch(e => setError(toAppError(e))); }, []);
  const change = async (standard: boolean) => {
    setBusy(true); setError(null); setNotice("");
    try {
      const path = standard ? null : await knowledgeApi.selectStorageFolder();
      if (!standard && path === null) return;
      if (!window.confirm(`Codex連携先を${path ?? "標準のローカル領域"}へ切り替えます。専用フォルダーを新しく作成し、委譲履歴を複製します。元のファイルは残します。\n進行中のCodex依頼は停止し、切替後に新しい環境・世代で依頼してください。未処理の提案がある場合は切り替えできません。続けますか？`)) return;
      const changed = await knowledgeApi.changeCodexLocation(path, true);
      setRoot(changed.root); setNotice("連携先を切り替えました。更新済みのKnowledgeAppプラグインを使用してください。");
    } catch (e) { setError(toAppError(e)); }
    finally { setBusy(false); }
  };
  return <section className="panel"><h2>Codex連携先</h2>
    <p>この端末のローカルフォルダー内に、環境ごとの専用領域を作成します。FAQデータベースやメール原本は移動しません。</p>
    <p style={{ overflowWrap: "anywhere" }}>{root}</p>
    {error && <ErrorState error={error} />}{notice && <p role="status">{notice}</p>}
    <div className="dialog-actions"><button className="button secondary" disabled={busy} onClick={() => void change(false)}>連携先を選ぶ</button>
    <button className="button secondary" disabled={busy} onClick={() => void change(true)}>標準の連携先へ切り替える</button></div>
  </section>;
}
