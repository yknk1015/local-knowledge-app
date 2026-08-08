import { FormEvent, useCallback, useEffect, useState } from "react";
import { knowledgeApi, toAppError } from "../api/knowledgeApi";
import { ErrorState, LoadingState } from "../components/Feedback";
import type { AppError, Category } from "../types/domain";

export function CategoriesPage() {
  const [categories, setCategories] = useState<Category[]>([]);
  const [name, setName] = useState("");
  const [parentId, setParentId] = useState("");
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<AppError | null>(null);
  const [notice, setNotice] = useState("");

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      setCategories(await knowledgeApi.listCategories());
    } catch (caught) {
      setError(toAppError(caught));
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (!name.trim()) return;
    setSaving(true);
    setError(null);
    setNotice("");
    try {
      const created = await knowledgeApi.createCategory(name.trim(), parentId || undefined);
      setName("");
      setNotice(`「${created.name}」を作成しました。`);
      await load();
    } catch (caught) {
      setError(toAppError(caught));
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="page categories-page">
      <div className="page-heading">
        <span className="eyebrow">整理する</span>
        <h1>分類の管理</h1>
        <p>FAQを見つけやすくするための分類を、最大5階層まで作成できます。</p>
      </div>

      <div className="two-column">
        <section className="panel">
          <div className="panel-heading">
            <h2>分類一覧</h2>
            <span>{categories.length}件</span>
          </div>
          {loading && <LoadingState />}
          {!loading && error && <ErrorState error={error} onRetry={() => void load()} />}
          {!loading && !error && categories.length === 0 && (
            <p className="muted-block">分類はまだありません。右側の入力欄から最初の分類を作成してください。</p>
          )}
          {!loading && !error && categories.length > 0 && (
            <ul className="category-list">
              {categories.map((category) => (
                <li key={category.id} style={{ paddingLeft: `${(category.depth - 1) * 24 + 14}px` }}>
                  <span className="tree-line" aria-hidden="true">{category.depth > 1 ? "└" : "◆"}</span>
                  <strong>{category.name}</strong>
                  <span>{category.articleCount}件</span>
                </li>
              ))}
            </ul>
          )}
        </section>

        <section className="panel sticky-panel">
          <div className="panel-heading"><h2>新しい分類</h2></div>
          <form className="form-stack" onSubmit={submit}>
            <label>
              <span>分類名 <b className="required">必須</b></span>
              <input value={name} onChange={(event) => setName(event.target.value)} maxLength={100} placeholder="例：Windows" />
            </label>
            <label>
              <span>親となる分類</span>
              <select value={parentId} onChange={(event) => setParentId(event.target.value)}>
                <option value="">一番上の階層に作成</option>
                {categories.filter((category) => category.depth < 5).map((category) => (
                  <option key={category.id} value={category.id}>
                    {"　".repeat(Math.max(0, category.depth - 1))}{category.name}
                  </option>
                ))}
              </select>
            </label>
            {notice && <div className="success-notice" role="status">✓ {notice}</div>}
            <button type="submit" className="button primary" disabled={saving || !name.trim()}>
              {saving ? "作成しています…" : "分類を作成"}
            </button>
          </form>
        </section>
      </div>
    </div>
  );
}
