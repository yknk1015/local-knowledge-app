import { lazy, Suspense } from "react";
import { Navigate, Route, Routes } from "react-router-dom";
import { AppShell } from "./AppShell";
import { LoadingState } from "../components/Feedback";

const SearchPage = lazy(() => import("../pages/SearchPage").then((module) => ({ default: module.SearchPage })));
const ArticleDetailPage = lazy(() => import("../pages/ArticleDetailPage").then((module) => ({ default: module.ArticleDetailPage })));
const ArticleEditorPage = lazy(() => import("../pages/ArticleEditorPage").then((module) => ({ default: module.ArticleEditorPage })));
const CategoriesPage = lazy(() => import("../pages/CategoriesPage").then((module) => ({ default: module.CategoriesPage })));
const SettingsPage = lazy(() => import("../pages/SettingsPage").then((module) => ({ default: module.SettingsPage })));
const ArticleManagementPage = lazy(() => import("../pages/ArticleManagementPage").then((module) => ({ default: module.ArticleManagementPage })));

export function App() {
  return (
    <Suspense fallback={<div className="page"><LoadingState /></div>}>
      <Routes>
        <Route element={<AppShell />}>
          <Route path="/search" element={<SearchPage />} />
          <Route path="/articles/new" element={<ArticleEditorPage />} />
          <Route path="/articles/:articleId" element={<ArticleDetailPage />} />
          <Route path="/articles/:articleId/edit" element={<ArticleEditorPage />} />
          <Route path="/categories" element={<CategoriesPage />} />
          <Route path="/manage" element={<ArticleManagementPage />} />
          <Route path="/settings" element={<SettingsPage />} />
          <Route path="*" element={<Navigate to="/search" replace />} />
        </Route>
      </Routes>
    </Suspense>
  );
}
