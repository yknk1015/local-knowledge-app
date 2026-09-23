import { useEffect, useState } from "react";
import { knowledgeApi, toAppError } from "../api/knowledgeApi";
import type { AppError } from "../types/domain";
import { ErrorState } from "./Feedback";
export function ConnectionSettingsPanel() {
  const [url, setUrl] = useState("");
  const [serverId, setServerId] = useState("");
  const [shared, setShared] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<AppError | null>(null);
  const [notice, setNotice] = useState("");
  useEffect(() => { knowledgeApi.getConnectionSettings().then(value => {
    setUrl(value.settings?.url ?? ""); setServerId(value.settings?.serverId ?? ""); setShared(value.settings !== null);
  }).catch(e => setError(toAppError(e))); }, []);
  return <section className="panel"><h2>接続方式</h2>
    <p>この端末だけで使用するか、管理者が用意した共有サーバーへ接続します。切り替えてもデータは移動・統合しません。</p>
    <form onSubmit={async event => {
      event.preventDefault(); setError(null); setNotice("");
      if (!window.confirm("次回起動時の接続先を変更します。未保存の入力を保存してからアプリを再起動してください。続けますか？")) return;
      setBusy(true);
      try {
        await knowledgeApi.saveConnectionSettings(shared ? { version: 1, url, serverId } : null);
        setNotice("設定しました。現在の接続は変更していません。アプリを閉じて起動し直してください。");
      } catch (e) { setError(toAppError(e)); } finally { setBusy(false); }
    }}>
      <label>利用方式<select value={shared ? "shared" : "local"} onChange={event => setShared(event.target.value === "shared")}>
        <option value="local">この端末だけで使用</option><option value="shared">共有サーバーへ接続</option>
      </select></label>
      {shared && <><label>共有サーバーURL<input type="url" required placeholder="https://faq-server:7443/" value={url} onChange={event => setUrl(event.target.value)} /></label>
      <label>サーバーID<input required value={serverId} onChange={event => setServerId(event.target.value)} /></label>
      <p>管理者から案内されたURLとIDを入力してください。証明書を検証できない接続先には保存できません。</p></>}
      <button className="button secondary" disabled={busy}>接続先を確認して保存</button>
    </form>{error && <ErrorState error={error} />}{notice && <p role="status">{notice}</p>}
  </section>;
}
