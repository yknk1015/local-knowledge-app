import { hasCSharpBridge } from "../api/csharpBridge";
import { ConnectionSettingsPanel } from "../components/ConnectionSettingsPanel";
import { isSharedConnection } from "../utils/connectionMode";
import { FormEvent, useState } from "react";
import { Navigate, useLocation, useNavigate } from "react-router-dom";
import { toAppError } from "../api/knowledgeApi";
import { useAuth } from "../app/AuthContext";
import { ErrorState, LoadingState } from "../components/Feedback";
import type { AppError } from "../types/domain";
import { PasswordRecovery } from "../components/PasswordRecovery";

export function LoginPage() {
  const { user, loading, login } = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  const [loginId, setLoginId] = useState("0000");
  const [password, setPassword] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<AppError | null>(null);
  const [recovering, setRecovering] = useState(false);

  if (loading) return <div className="login-page"><LoadingState label="ログイン状態を確認しています…" /></div>;
  if (user) return <Navigate to="/search" replace />;
  if (recovering) return <main className="login-page"><PasswordRecovery onBack={() => setRecovering(false)} /></main>;

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    setSubmitting(true);
    setError(null);
    try {
      await login(loginId, password);
      const destination = (location.state as { from?: string } | null)?.from ?? "/search";
      navigate(destination, { replace: true });
    } catch (caught) {
      setError(toAppError(caught));
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <main className="login-page">
      <section className="login-card panel" aria-labelledby="login-title">
        <div className="login-brand" aria-hidden="true">K</div>
        <span className="eyebrow">KnowledgeApp</span>
        <h1 id="login-title">ログイン</h1>
        <p>{isSharedConnection() ? "共有サーバーに登録された利用者でログインしてください。" : "このPCに登録された利用者でログインしてください。"}</p>
        {error && <ErrorState error={error} />}
        <form onSubmit={submit} className="login-form">
          <label>
            <span>ログインID</span>
            <input autoFocus autoComplete="username" required maxLength={100} value={loginId} onChange={(event) => setLoginId(event.target.value)} />
          </label>
          <label>
            <span>パスワード</span>
            <input type="password" autoComplete="current-password" maxLength={1024} value={password} onChange={(event) => setPassword(event.target.value)} />
            <small>{isSharedConnection() ? "共有利用では空欄のパスワードではログインできません。" : "初期ユーザー「0000」は、初回登録直後のみパスワード空欄でログインできます。"}</small>
          </label>
          <button type="submit" className="button primary" disabled={submitting}>{submitting ? "ログイン中…" : "ログイン"}</button>
        </form>
        <button type="button" className="button secondary" disabled={submitting} onClick={() => { setPassword(""); setError(null); setRecovering(true); }}>パスワードを忘れた場合</button>
      </section>
      {hasCSharpBridge() && <ConnectionSettingsPanel />}
    </main>
  );
}
