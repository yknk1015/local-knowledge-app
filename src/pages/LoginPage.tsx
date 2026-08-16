import { FormEvent, useState } from "react";
import { Navigate, useLocation, useNavigate } from "react-router-dom";
import { toAppError } from "../api/knowledgeApi";
import { useAuth } from "../app/AuthContext";
import { ErrorState, LoadingState } from "../components/Feedback";
import type { AppError } from "../types/domain";

export function LoginPage() {
  const { user, loading, login } = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  const [loginId, setLoginId] = useState("0000");
  const [password, setPassword] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<AppError | null>(null);

  if (loading) return <div className="login-page"><LoadingState label="ログイン状態を確認しています…" /></div>;
  if (user) return <Navigate to="/search" replace />;

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
        <p>このPCに登録された利用者でログインしてください。</p>
        {error && <ErrorState error={error} />}
        <form onSubmit={submit} className="login-form">
          <label>
            <span>ログインID</span>
            <input autoFocus autoComplete="username" required maxLength={100} value={loginId} onChange={(event) => setLoginId(event.target.value)} />
          </label>
          <label>
            <span>パスワード</span>
            <input type="password" autoComplete="current-password" maxLength={1024} value={password} onChange={(event) => setPassword(event.target.value)} />
            <small>初期ユーザー「0000」はパスワード空欄でログインできます。</small>
          </label>
          <button type="submit" className="button primary" disabled={submitting}>{submitting ? "ログイン中…" : "ログイン"}</button>
        </form>
      </section>
    </main>
  );
}
