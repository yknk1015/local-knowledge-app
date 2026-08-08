import { NavLink, Outlet } from "react-router-dom";

const navItems = [
  { to: "/search", label: "FAQを探す", icon: "⌕" },
  { to: "/articles/new", label: "新しいFAQ", icon: "+" },
  { to: "/manage", label: "FAQの管理", icon: "☷" },
  { to: "/categories", label: "分類の管理", icon: "▦" },
  { to: "/settings", label: "設定・情報", icon: "⚙" },
];

export function AppShell() {
  return (
    <div className="app-shell">
      <header className="topbar">
        <NavLink to="/search" className="brand" aria-label="KnowledgeApp ホーム">
          <span className="brand-mark" aria-hidden="true">K</span>
          <span>
            <strong>KnowledgeApp</strong>
            <small>自分の知識を、すぐ見つかる形に</small>
          </span>
        </NavLink>
        <div className="privacy-badge" title="FAQデータはこのPC内に保存されます">
          <span aria-hidden="true">●</span> ローカル保存
        </div>
      </header>

      <div className="app-body">
        <nav className="side-nav" aria-label="メインメニュー">
          {navItems.map((item) => (
            <NavLink
              key={item.to}
              to={item.to}
              className={({ isActive }) => `nav-item${isActive ? " active" : ""}`}
            >
              <span className="nav-icon" aria-hidden="true">{item.icon}</span>
              {item.label}
            </NavLink>
          ))}
          <div className="side-note">
            <strong>安全のために</strong>
            <p>パスワードや秘密鍵、個人情報はFAQへ登録しないでください。</p>
          </div>
        </nav>

        <main className="main-content">
          <Outlet />
        </main>
      </div>
    </div>
  );
}
