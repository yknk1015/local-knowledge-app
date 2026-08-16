import { lazy, Suspense } from "react";
import { Navigate, Outlet, Route, Routes, useLocation } from "react-router-dom";
import { AppShell } from "./AppShell";
import { LoadingState } from "../components/Feedback";
import { useAuth } from "./AuthContext";

const SearchPage = lazy(() => import("../pages/SearchPage").then((module) => ({ default: module.SearchPage })));
const ArticleDetailPage = lazy(() => import("../pages/ArticleDetailPage").then((module) => ({ default: module.ArticleDetailPage })));
const ArticleEditorPage = lazy(() => import("../pages/ArticleEditorPage").then((module) => ({ default: module.ArticleEditorPage })));
const CategoriesPage = lazy(() => import("../pages/CategoriesPage").then((module) => ({ default: module.CategoriesPage })));
const SettingsPage = lazy(() => import("../pages/SettingsPage").then((module) => ({ default: module.SettingsPage })));
const ArticleManagementPage = lazy(() => import("../pages/ArticleManagementPage").then((module) => ({ default: module.ArticleManagementPage })));
const CodexProposalsPage = lazy(() => import("../pages/CodexProposalsPage").then((module) => ({ default: module.CodexProposalsPage })));
const LoginPage = lazy(() => import("../pages/LoginPage").then((module) => ({ default: module.LoginPage })));
const UsersPage = lazy(() => import("../pages/UsersPage").then((module) => ({ default: module.UsersPage })));
const CsvImportPage = lazy(() => import("../pages/CsvImportPage").then((module) => ({ default: module.CsvImportPage })));

function RequireAuth({ admin = false }: { admin?: boolean }) {
  const { user, loading } = useAuth();
  const location = useLocation();
  if (loading) return <div className="page"><LoadingState label="ログイン状態を確認しています…" /></div>;
  if (!user) return <Navigate to="/login" replace state={{ from: location.pathname }} />;
  if (admin && user.role !== "admin") return <Navigate to="/search" replace />;
  return <Outlet />;
}

export function App() {
  return (
    <Suspense fallback={<div className="page"><LoadingState /></div>}>
      <Routes>
        <Route path="/login" element={<LoginPage />} />
        <Route element={<RequireAuth />}>
          <Route element={<AppShell />}>
            <Route path="/search" element={<SearchPage />} />
            <Route path="/articles/new" element={<ArticleEditorPage />} />
            <Route path="/articles/:articleId" element={<ArticleDetailPage />} />
            <Route path="/articles/:articleId/edit" element={<ArticleEditorPage />} />
            <Route path="/categories" element={<CategoriesPage />} />
            <Route path="/manage" element={<ArticleManagementPage />} />
            <Route path="/manage/csv-import" element={<CsvImportPage />} />
            <Route path="/codex-proposals" element={<CodexProposalsPage />} />
            <Route path="/settings" element={<SettingsPage />} />
            <Route element={<RequireAuth admin />}>
              <Route path="/users" element={<UsersPage />} />
            </Route>
            <Route path="*" element={<Navigate to="/search" replace />} />
          </Route>
        </Route>
      </Routes>
    </Suspense>
  );
}
