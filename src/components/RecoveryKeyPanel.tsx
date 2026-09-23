import { useEffect, useRef, useState, type FormEvent, type ReactNode } from "react";
import { knowledgeApi, toAppError } from "../api/knowledgeApi";
import { useAuth } from "../app/AuthContext";
import type { AppError, RecoveryKeyStatus } from "../types/domain";
import { ErrorState, LoadingState } from "./Feedback";
import "./Recovery.css";

export function RecoveryKeyDisplay({ value, onDone }: { value: string; onDone: () => void }) {
  const [saved, setSaved] = useState(false);
  const heading = useRef<HTMLHeadingElement>(null);
  useEffect(() => { heading.current?.focus(); }, []);
  return <div className="recovery-result">
    <h3 ref={heading} tabIndex={-1}>新しい復旧キー</h3>
    <p>このキーは今だけ表示します。紙やパスワード管理アプリなどで別に保管してください。以前のキーは使えません。</p>
    <label><span>復旧キー（英数字16文字）</span><input className="recovery-key" readOnly value={value} autoComplete="off" spellCheck={false} onFocus={(event) => event.target.select()} /></label>
    <p>FAQ本文やGitHubへ保存しないでください。キーを知っている人は管理者のパスワードを再設定できます。</p>
    <label className="recovery-saved"><input type="checkbox" checked={saved} onChange={(event) => setSaved(event.target.checked)} />復旧キーを別の安全な場所に保管しました</label>
    <button type="button" className="button primary" disabled={!saved} onClick={onDone}>保管して閉じる</button>
  </div>;
}

export function RecoveryKeyPanel({ initialStatus, onDone }: { initialStatus?: RecoveryKeyStatus; onDone?: () => void }) {
  const [status, setStatus] = useState<RecoveryKeyStatus | null>(initialStatus ?? null);
  const [password, setPassword] = useState("");
  const [key, setKey] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<AppError | null>(null);
  const [retry, setRetry] = useState(0);
  const working = useRef(false);
  const live = useRef(true);
  useEffect(() => {
    live.current = true;
    setError(null);
    if (!initialStatus) void Promise.resolve().then(() => knowledgeApi.getRecoveryKeyStatus())
      .then(value => { if (live.current) setStatus(value); })
      .catch(caught => { if (live.current) setError(toAppError(caught)); });
    return () => { live.current = false; };
  }, [initialStatus, retry]);
  const issue = async (event: FormEvent) => {
    event.preventDefault();
    if (working.current) return;
    working.current = true; setBusy(true); setError(null);
    try {
      const result = await knowledgeApi.issueRecoveryKey(password);
      if (live.current) { setKey(result.key); setStatus({ hasKey: true, needsSetup: false, issuedAt: null }); }
    } catch (caught) { if (live.current) setError(toAppError(caught)); }
    finally { working.current = false; if (live.current) { setPassword(""); setBusy(false); } }
  };
  const skip = async () => {
    if (working.current) return;
    working.current = true; setBusy(true); setError(null);
    try { await knowledgeApi.skipRecoverySetup(); if (live.current) onDone?.(); }
    catch (caught) { if (live.current) setError(toAppError(caught)); }
    finally { working.current = false; if (live.current) setBusy(false); }
  };
  return <section className="panel recovery-panel" aria-label="管理者の復旧キー">
    <h2>管理者の復旧キー</h2>
    {error && <ErrorState error={error} onRetry={!status ? () => setRetry(value => value + 1) : undefined} />}
    {!status && !error && <LoadingState label="復旧キーの状態を確認しています…" />}
    {key ? <RecoveryKeyDisplay value={key} onDone={() => { setKey(null); onDone?.(); }} /> : status && <>
      <p>{status.hasKey ? "復旧キーは発行済みです。再発行すると以前のキーは無効になります。" : "復旧キーは未発行です。パスワードを忘れる前に作成し、別に保管してください。"}</p>
      <p>通常パスワードの変更・利用停止でもキーは無効になります。変更後にここで再発行してください。</p>
      <p>キーを作成しない場合、ご自身でパスワードを再設定できません。他の有効な管理者にも依頼できない場合、アクセスを復旧できなくなることがあります。</p>
      <form onSubmit={event => void issue(event)} className="login-form">
        <label><span>現在のパスワード（本人確認）</span><input type="password" autoComplete="current-password" maxLength={1024} value={password} disabled={busy} onChange={event => setPassword(event.target.value)} /></label>
        <small>初期パスワードが空欄の場合は、そのまま作成できます。「利用者の管理」で通常パスワードも設定し、その後キーを再発行してください。</small>
        <div className="recovery-actions">
          <button type="submit" className="button primary" disabled={busy}>{busy ? "処理しています…" : status.hasKey ? "復旧キーを再発行する" : "復旧キーを作成する"}</button>
          {onDone && <button type="button" className="button secondary" disabled={busy} onClick={() => void skip()}>後で設定する</button>}
        </div>
      </form>
    </>}
  </section>;
}

export function RecoverySetupGate({ children }: { children: ReactNode }) {
  const { user } = useAuth();
  const [status, setStatus] = useState<RecoveryKeyStatus | null>(null);
  const [error, setError] = useState<AppError | null>(null);
  const [retry, setRetry] = useState(0);
  useEffect(() => {
    let active = true;
    setStatus(null);
    setError(null);
    if (user?.role === "admin") void Promise.resolve().then(() => knowledgeApi.getRecoveryKeyStatus())
      .then(value => { if (active) setStatus(value); })
      .catch(caught => { if (active) setError(toAppError(caught)); });
    return () => { active = false; };
  }, [user, retry]);
  if (user?.role === "admin" && !status) return error
    ? <ErrorState error={error} onRetry={() => setRetry(value => value + 1)} />
    : <LoadingState label="復旧キーの状態を確認しています…" />;
  return status?.needsSetup ? <RecoveryKeyPanel initialStatus={status} onDone={() => setStatus({ ...status, needsSetup: false })} /> : <>{children}</>;
}
