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
const SynonymsPage = lazy(() => import("../pages/SynonymsPage").then((module) => ({ default: module.SynonymsPage })));
const TagsPage = lazy(() => import("../pages/TagsPage").then((module) => ({ default: module.TagsPage })));
const HistoryPage = lazy(() => import("../pages/HistoryPage").then((module) => ({ default: module.HistoryPage })));
const JsonTransferPage = lazy(() => import("../pages/JsonTransferPage").then((module) => ({ default: module.JsonTransferPage })));

function RequireAuth({ admin = false, editor = false }: { admin?: boolean; editor?: boolean }) {
  const { user, loading } = useAuth();
  const location = useLocation();
  if (loading) return <div className="page"><LoadingState label="ログイン状態を確認しています…" /></div>;
  if (!user) return <Navigate to="/login" replace state={{ from: location.pathname }} />;
  if ((admin && user.role !== "admin") || (editor && user.role !== "admin" && user.role !== "editor")) return <Navigate to="/search" replace />;
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
            <Route element={<RequireAuth editor />}>
            <Route path="/articles/new" element={<ArticleEditorPage />} />
            </Route>
            <Route path="/articles/:articleId" element={<ArticleDetailPage />} />
            <Route element={<RequireAuth editor />}>
            <Route path="/articles/:articleId/edit" element={<ArticleEditorPage />} />
            </Route>
            <Route element={<RequireAuth admin />}>
            <Route path="/categories" element={<CategoriesPage />} />
            </Route>
            <Route element={<RequireAuth admin />}>
            <Route path="/synonyms" element={<SynonymsPage />} />
            </Route>
            <Route element={<RequireAuth admin />}>
            <Route path="/tags" element={<TagsPage />} />
            </Route>
            <Route path="/history" element={<HistoryPage />} />
            <Route element={<RequireAuth editor />}>
            <Route path="/manage" element={<ArticleManagementPage />} />
            </Route>
            <Route element={<RequireAuth admin />}>
            <Route path="/manage/csv-import" element={<CsvImportPage />} />
            </Route>
            <Route element={<RequireAuth admin />}>
            <Route path="/manage/json-transfer" element={<JsonTransferPage />} />
            </Route>
            <Route element={<RequireAuth editor />}>
            <Route path="/codex-proposals" element={<CodexProposalsPage />} />
            </Route>
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
