import { useEffect, useRef, useState, type FormEvent } from "react";
import { knowledgeApi, toAppError } from "../api/knowledgeApi";
import type { AppError, RecoveryAuthorization } from "../types/domain";
import { ErrorState } from "./Feedback";
import { RecoveryKeyDisplay } from "./RecoveryKeyPanel";

export function PasswordRecovery({ onBack }: { onBack: () => void }) {
  const [loginId, setLoginId] = useState("0000");
  const [key, setKey] = useState("");
  const [authorization, setAuthorization] = useState<RecoveryAuthorization | null>(null);
  const [password, setPassword] = useState("");
  const [confirmation, setConfirmation] = useState("");
  const [issuedKey, setIssuedKey] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<AppError | null>(null);
  const working = useRef(false);
  const live = useRef(true);
  const passwordInput = useRef<HTMLInputElement>(null);
  useEffect(() => {
    live.current = true;
    return () => { live.current = false; void knowledgeApi.cancelPasswordRecovery().catch(() => undefined); };
  }, []);
  useEffect(() => { if (authorization) passwordInput.current?.focus(); }, [authorization]);
  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (working.current) return;
    if (authorization && (!password || password !== confirmation)) {
      setError({ code: "REC-005", message: "新しいパスワードを確認してください。", action: "空欄にせず、確認欄にも同じパスワードを入力してください。" }); return;
    }
    working.current = true; setBusy(true); setError(null);
    try {
      if (!authorization) {
        const result = await knowledgeApi.verifyRecoveryKey(loginId, key);
        if (live.current) { setAuthorization(result); setKey(""); }
      } else {
        const result = await knowledgeApi.completePasswordRecovery(authorization.token, password, confirmation);
        if (live.current) { setIssuedKey(result.key); setAuthorization(null); setPassword(""); setConfirmation(""); }
      }
    } catch (caught) {
      if (live.current) {
        const problem = toAppError(caught); setError(problem);
        if (problem.code === "REC-004") { setAuthorization(null); setPassword(""); setConfirmation(""); }
      }
    } finally { working.current = false; if (live.current) setBusy(false); }
  };
  return <section className="login-card panel recovery-panel" aria-labelledby="recovery-title">
    <h1 id="recovery-title">パスワードを忘れた場合</h1>
    {issuedKey ? <>
      <p role="status">パスワードを再設定しました。新しい復旧キーを保管し、新しいパスワードでログインしてください。</p>
      <RecoveryKeyDisplay value={issuedKey} onDone={onBack} />
    </> : <>
      <p>一般利用者は、管理者にパスワードの再設定を依頼してください。</p>
      <p>管理者は、ご自身の復旧キーで新しいパスワードを設定できます。</p>
      {error && <ErrorState error={error} />}
      <form className="login-form" onSubmit={event => void submit(event)}>
        {!authorization ? <>
          <label><span>管理者のログインID</span><input autoFocus autoComplete="username" maxLength={100} required disabled={busy} value={loginId} onChange={event => setLoginId(event.target.value)} /></label>
          <label><span>復旧キー</span><input aria-describedby="recovery-key-help" autoComplete="off" spellCheck={false} maxLength={64} required disabled={busy} value={key} onChange={event => setKey(event.target.value)} /></label><small id="recovery-key-help">英数字16文字。区切りのハイフン・空白は省略できます。</small>
        </> : <>
          <p role="status">復旧キーを確認しました。5分以内に新しいパスワードを設定してください。</p>
          <label><span>新しいパスワード</span><input ref={passwordInput} type="password" autoComplete="new-password" maxLength={1024} required disabled={busy} value={password} onChange={event => setPassword(event.target.value)} /></label>
          <label><span>新しいパスワード（確認）</span><input type="password" autoComplete="new-password" maxLength={1024} required disabled={busy} value={confirmation} onChange={event => setConfirmation(event.target.value)} /></label>
        </>}
        <button type="submit" className="button primary" disabled={busy}>{busy ? "処理しています…" : authorization ? "新しいパスワードを設定する" : "復旧キーを確認する"}</button>
        <button type="button" className="button secondary" disabled={busy} onClick={onBack}>ログインへ戻る</button>
      </form>
    </>}
  </section>;
}
