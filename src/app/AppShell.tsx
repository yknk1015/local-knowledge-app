import type { ReactNode } from "react";
import { NavLink, Outlet } from "react-router-dom";
import { FaqMascot } from "../components/FaqMascot";
import { useDisplaySettings } from "./ColorTheme";
import { useAuth } from "./AuthContext";

type IconName = "search" | "plus" | "sparkles" | "list" | "folder" | "settings" | "check" | "users";

const navItems: Array<{ to: string; label: string; icon: IconName }> = [
  { to: "/search", label: "FAQを探す", icon: "search" },
  { to: "/articles/new", label: "新しいFAQ", icon: "plus" },
  { to: "/codex-proposals", label: "Codexからの提案", icon: "sparkles" },
  { to: "/manage", label: "FAQの管理", icon: "list" },
  { to: "/categories", label: "分類の管理", icon: "folder" },
  { to: "/settings", label: "設定・情報", icon: "settings" },
];

function AppIcon({ name }: { name: IconName }) {
  const paths: Record<IconName, ReactNode> = {
    search: <><circle cx="10.5" cy="10.5" r="6.5" /><path d="m15.5 15.5 4.5 4.5" /></>,
    plus: <><path d="M12 5v14M5 12h14" /></>,
    sparkles: <><path d="m12 3 1.2 3.3L16.5 7.5l-3.3 1.2L12 12l-1.2-3.3-3.3-1.2 3.3-1.2z" /><path d="m18 13 .8 2.2L21 16l-2.2.8L18 19l-.8-2.2L15 16l2.2-.8zM5 13l.7 1.8 1.8.7-1.8.7L5 18l-.7-1.8-1.8-.7 1.8-.7z" /></>,
    list: <><path d="M8 6h12M8 12h12M8 18h12" /><circle cx="4" cy="6" r=".8" fill="currentColor" stroke="none" /><circle cx="4" cy="12" r=".8" fill="currentColor" stroke="none" /><circle cx="4" cy="18" r=".8" fill="currentColor" stroke="none" /></>,
    folder: <path d="M3.5 7.5h6l2-2h9v13h-17z" />,
    settings: <><circle cx="12" cy="12" r="3" /><path d="M19 12a7.6 7.6 0 0 0-.1-1l2-1.6-2-3.4-2.5 1a8 8 0 0 0-1.7-1L14.3 3h-4.1L9.8 6a8 8 0 0 0-1.7 1L5.6 6 3.5 9.4l2 1.6a7.6 7.6 0 0 0 0 2l-2 1.6L5.6 18l2.5-1a8 8 0 0 0 1.7 1l.4 3h4.1l.4-3a8 8 0 0 0 1.7-1l2.5 1 2-3.4-2-1.6a7.6 7.6 0 0 0 .1-1z" /></>,
    check: <><circle cx="12" cy="12" r="8.5" /><path d="m8.5 12.2 2.2 2.2 4.8-5" /></>,
    users: <><circle cx="9" cy="8" r="3" /><path d="M3.5 19c.4-4 2.2-6 5.5-6s5.1 2 5.5 6M15 6.5a2.5 2.5 0 0 1 0 5M16 13c2.7.3 4.1 2.2 4.5 5" /></>,
  };

  return <svg viewBox="0 0 24 24" aria-hidden="true">{paths[name]}</svg>;
}

export function AppShell() {
  const { showMascot, updateShowMascot } = useDisplaySettings();
  const { user, logout } = useAuth();
  const visibleNavItems = user?.role === "admin"
    ? [...navItems, { to: "/users", label: "利用者の管理", icon: "users" as IconName }]
    : navItems;

  return (
    <div className="app-shell">
      <header className="topbar">
        <NavLink to="/search" className="brand" aria-label="KnowledgeApp ホーム">
          <span className="brand-mark" aria-hidden="true">K</span>
          <span>
            <strong>KnowledgeApp</strong>
            <small>Knowledge Base</small>
          </span>
        </NavLink>
        <div className="topbar-account">
          <div className="privacy-badge" title="FAQデータはこのPC内に保存されます">
            <AppIcon name="check" />
            ローカルに保存済み
          </div>
          <span className="signed-in-user">{user?.displayName}<small>{user?.loginId}</small></span>
          <button type="button" className="button secondary compact" onClick={() => void logout()}>ログアウト</button>
        </div>
      </header>

      <div className="app-body">
        <nav className="side-nav" aria-label="メインメニュー">
          <div className="side-nav-main">
            {visibleNavItems.map((item) => (
              <NavLink
                key={item.to}
                to={item.to}
                className={({ isActive }) => `nav-item${isActive ? " active" : ""}`}
              >
                <span className="nav-icon"><AppIcon name={item.icon} /></span>
                <span>{item.label}</span>
              </NavLink>
            ))}
          </div>
        </nav>

        <main className="main-content">
          <Outlet />
        </main>
        <FaqMascot visible={showMascot} onRequestHide={() => updateShowMascot(false)} />
      </div>
    </div>
  );
}
