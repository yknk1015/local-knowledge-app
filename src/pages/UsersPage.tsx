import { FormEvent, useCallback, useEffect, useState } from "react";
import { knowledgeApi, toAppError } from "../api/knowledgeApi";
import { useAuth } from "../app/AuthContext";
import { EmptyState, ErrorState, LoadingState } from "../components/Feedback";
import type { AppError, UserRole, UserSummary } from "../types/domain";

export function UsersPage() {
  const { user: currentUser } = useAuth();
  const [users, setUsers] = useState<UserSummary[]>([]);
  const [loginId, setLoginId] = useState("");
  const [displayName, setDisplayName] = useState("");
  const [password, setPassword] = useState("");
  const [role, setRole] = useState<UserRole>("user");
  const [allowEmptyPasswords, setAllowEmptyPasswords] = useState(true);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<AppError | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [passwordResetTarget, setPasswordResetTarget] = useState<UserSummary | null>(null);
  const [newPassword, setNewPassword] = useState("");
  const [newPasswordConfirmation, setNewPasswordConfirmation] = useState("");
  const [passwordResetError, setPasswordResetError] = useState<AppError | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const [loadedUsers, passwordPolicy] = await Promise.all([
        knowledgeApi.listUsers(),
        knowledgeApi.getPasswordPolicy(),
      ]);
      setUsers(loadedUsers);
      setAllowEmptyPasswords(passwordPolicy.allowEmptyPasswords);
    }
    catch (caught) { setError(toAppError(caught)); }
    finally { setLoading(false); }
  }, []);

  useEffect(() => { void load(); }, [load]);

  const create = async (event: FormEvent) => {
    event.preventDefault();
    setBusy(true); setError(null); setNotice(null);
    try {
      await knowledgeApi.createUser(loginId, displayName, password, role);
      setLoginId(""); setDisplayName(""); setPassword(""); setRole("user");
      setNotice("利用者を追加しました。");
      await load();
    } catch (caught) { setError(toAppError(caught)); }
    finally { setBusy(false); }
  };

  const toggleActive = async (target: UserSummary) => {
    const next = !target.isActive;
    if (!window.confirm(`${target.displayName}を${next ? "利用再開" : "利用停止"}にしますか？`)) return;
    setBusy(true); setError(null); setNotice(null);
    try {
      await knowledgeApi.setUserActive(target.id, next);
      setNotice(`${target.displayName}を${next ? "利用再開" : "利用停止"}にしました。`);
      await load();
    } catch (caught) { setError(toAppError(caught)); }
    finally { setBusy(false); }
  };

  const openPasswordReset = (target: UserSummary) => {
    setNotice(null);
    setError(null);
    setNewPassword("");
    setNewPasswordConfirmation("");
    setPasswordResetError(null);
    setPasswordResetTarget(target);
  };

  const closePasswordReset = () => {
    if (busy) return;
    setPasswordResetTarget(null);
    setNewPassword("");
    setNewPasswordConfirmation("");
    setPasswordResetError(null);
  };

  const resetPassword = async (event: FormEvent) => {
    event.preventDefault();
    if (!passwordResetTarget) return;
    if (!allowEmptyPasswords && newPassword === "") {
      setPasswordResetError({
        code: "USR-004",
        message: "空欄のパスワードは現在許可されていません。",
        action: "1文字以上のパスワードを入力してください。",
      });
      return;
    }
    if (newPassword !== newPasswordConfirmation) {
      setPasswordResetError({
        code: "USR-001",
        message: "確認用パスワードが一致しません。",
        action: "同じパスワードを2つの入力欄へ入力してください。",
      });
      return;
    }
    setBusy(true); setPasswordResetError(null); setNotice(null);
    try {
      await knowledgeApi.resetUserPassword(passwordResetTarget.id, newPassword);
      const displayName = passwordResetTarget.displayName;
      setPasswordResetTarget(null);
      setNewPassword("");
      setNewPasswordConfirmation("");
      setNotice(`${displayName}のパスワードを再設定しました。`);
      await load();
    } catch (caught) { setPasswordResetError(toAppError(caught)); }
    finally { setBusy(false); }
  };

  return (
    <div className="page users-page">
      <div className="page-heading">
        <span className="eyebrow">管理者専用</span>
        <h1>利用者の管理</h1>
        <p>利用者の追加、利用停止・再開、パスワード再設定を行います。利用者は削除せず履歴を保持します。</p>
      </div>
      <form className="panel user-create-form" onSubmit={create}>
        <h2>利用者を追加</h2>
        <label><span>ログインID</span><input required maxLength={100} value={loginId} onChange={(event) => setLoginId(event.target.value)} /></label>
        <label><span>表示名</span><input required maxLength={100} value={displayName} onChange={(event) => setDisplayName(event.target.value)} /></label>
        <label><span>初期パスワード</span><input type="password" required={!allowEmptyPasswords} maxLength={1024} value={password} onChange={(event) => setPassword(event.target.value)} /></label>
        <label><span>権限</span><select value={role} onChange={(event) => setRole(event.target.value as UserRole)}><option value="user">一般利用者</option><option value="admin">管理者</option></select></label>
        <button type="submit" className="button primary" disabled={busy}>追加する</button>
      </form>
      {notice && <div className="success-notice" role="status">{notice}</div>}
      {error && <ErrorState error={error} onRetry={() => void load()} />}
      {loading && <LoadingState label="利用者一覧を読み込んでいます…" />}
      {!loading && !error && users.length === 0 && <EmptyState title="利用者がいません" description="利用者を追加してください。" />}
      {!loading && users.length > 0 && (
        <div className="panel management-table-wrap">
          <table className="management-table users-table">
            <thead><tr><th>利用者</th><th>権限</th><th>状態</th><th>最終ログイン</th><th><span className="sr-only">操作</span></th></tr></thead>
            <tbody>{users.map((target) => (
              <tr key={target.id}>
                <td><strong>{target.displayName}</strong><small className="table-subline">ID: {target.loginId}</small></td>
                <td>{target.role === "admin" ? "管理者" : "一般利用者"}</td>
                <td><span className={`status-badge ${target.isActive ? "published" : "archived"}`}>{target.isActive ? "利用中" : "利用停止"}</span></td>
                <td>{target.lastLoginAt ? new Date(target.lastLoginAt).toLocaleString("ja-JP") : "未ログイン"}</td>
                <td className="management-row-actions">
                  <button type="button" className="button secondary" disabled={busy} onClick={() => openPasswordReset(target)}>パスワード再設定</button>
                  <button type="button" className={target.isActive ? "button danger-outline" : "button secondary"} disabled={busy || target.id === currentUser?.id} onClick={() => void toggleActive(target)}>{target.isActive ? "利用停止" : "利用再開"}</button>
                </td>
              </tr>
            ))}</tbody>
          </table>
        </div>
      )}
      {passwordResetTarget && (
        <div className="modal-backdrop" role="presentation">
          <section
            className="password-reset-dialog"
            role="dialog"
            aria-modal="true"
            aria-labelledby="password-reset-title"
          >
            <span className="eyebrow">利用者の認証情報</span>
            <h2 id="password-reset-title">{passwordResetTarget.displayName}のパスワード再設定</h2>
            <p>入力間違いを防ぐため、新しいパスワードを2回入力してください。</p>
            <form onSubmit={(event) => void resetPassword(event)}>
              <label>
                <span>新しいパスワード</span>
                <input
                  type="password"
                  autoFocus
                  required={!allowEmptyPasswords}
                  maxLength={1024}
                  autoComplete="new-password"
                  value={newPassword}
                  onChange={(event) => {
                    setNewPassword(event.target.value);
                    setPasswordResetError(null);
                  }}
                />
              </label>
              <label>
                <span>新しいパスワード（確認）</span>
                <input
                  type="password"
                  required={!allowEmptyPasswords}
                  maxLength={1024}
                  autoComplete="new-password"
                  value={newPasswordConfirmation}
                  onChange={(event) => {
                    setNewPasswordConfirmation(event.target.value);
                    setPasswordResetError(null);
                  }}
                />
              </label>
              {allowEmptyPasswords && (
                <p className="password-reset-note">両方を空欄にして再設定すると、パスワードなしになります。</p>
              )}
              {passwordResetError && (
                <div className="password-reset-error" role="alert">
                  <strong>{passwordResetError.code}：{passwordResetError.message}</strong>
                  <span>{passwordResetError.action}</span>
                </div>
              )}
              <div className="dialog-actions">
                <button type="button" className="button secondary" disabled={busy} onClick={closePasswordReset}>キャンセル</button>
                <button type="submit" className="button primary" disabled={busy}>{busy ? "再設定中…" : "再設定する"}</button>
              </div>
            </form>
          </section>
        </div>
      )}
    </div>
  );
}
