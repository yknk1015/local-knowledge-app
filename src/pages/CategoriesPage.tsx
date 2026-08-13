import { FormEvent, useCallback, useEffect, useMemo, useState } from "react";
import { knowledgeApi, toAppError } from "../api/knowledgeApi";
import { ErrorState, LoadingState } from "../components/Feedback";
import type { AppError, Category } from "../types/domain";

function isDescendant(category: Category, ancestorId: string, byId: Map<string, Category>) {
  let parentId = category.parentId;
  while (parentId) {
    if (parentId === ancestorId) return true;
    parentId = byId.get(parentId)?.parentId ?? null;
  }
  return false;
}

export function CategoriesPage() {
  const [categories, setCategories] = useState<Category[]>([]);
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [parentId, setParentId] = useState("");
  const [editingId, setEditingId] = useState<string | null>(null);
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

  useEffect(() => { void load(); }, [load]);

  const byId = useMemo(() => new Map(categories.map((category) => [category.id, category])), [categories]);
  const editingCategory = editingId ? byId.get(editingId) ?? null : null;
  const availableParents = useMemo(() => {
    if (!editingCategory) return categories.filter((category) => category.depth < 5);
    const subtree = categories.filter(
      (category) => category.id === editingCategory.id || isDescendant(category, editingCategory.id, byId),
    );
    const relativeDepth = Math.max(...subtree.map((category) => category.depth - editingCategory.depth), 0);
    return categories.filter(
      (category) =>
        category.id !== editingCategory.id &&
        !isDescendant(category, editingCategory.id, byId) &&
        category.depth + 1 + relativeDepth <= 5,
    );
  }, [byId, categories, editingCategory]);

  const resetForm = () => {
    setEditingId(null);
    setName("");
    setDescription("");
    setParentId("");
  };

  const startEditing = (category: Category) => {
    setError(null);
    setNotice("");
    setEditingId(category.id);
    setName(category.name);
    setDescription(category.description);
    setParentId(category.parentId ?? "");
  };

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (!name.trim()) return;
    setSaving(true);
    setError(null);
    setNotice("");
    try {
      if (editingId) {
        const updated = await knowledgeApi.updateCategory(editingId, name.trim(), description.trim(), parentId || undefined);
        setNotice(`「${updated.name}」を更新しました。`);
      } else {
        const created = await knowledgeApi.createCategory(name.trim(), description.trim(), parentId || undefined);
        setNotice(`「${created.name}」を作成しました。`);
      }
      resetForm();
      await load();
    } catch (caught) {
      setError(toAppError(caught));
    } finally {
      setSaving(false);
    }
  };

  const deleteCategory = async (category: Category) => {
    if (!window.confirm(`分類「${category.name}」を削除しますか？\n配下分類またはFAQが残っている場合は削除できません。`)) return;
    setSaving(true);
    setError(null);
    setNotice("");
    try {
      await knowledgeApi.deleteCategory(category.id);
      if (editingId === category.id) resetForm();
      setNotice(`分類「${category.name}」を削除しました。`);
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
        <p>分類の追加、名称変更、階層移動、安全な削除を行えます。階層は最大5段です。</p>
      </div>

      {error && <ErrorState error={error} onRetry={() => void load()} />}
      {notice && <div className="success-notice category-notice" role="status">✓ {notice}</div>}

      <div className="two-column">
        <section className="panel">
          <div className="panel-heading">
            <h2>分類一覧</h2>
            <span>{categories.length}件</span>
          </div>
          {loading && <LoadingState />}
          {!loading && categories.length === 0 && (
            <p className="muted-block">分類はまだありません。右側の入力欄から最初の分類を作成してください。</p>
          )}
          {!loading && categories.length > 0 && (
            <ul className="category-list editable">
              {categories.map((category) => (
                <li key={category.id} className={editingId === category.id ? "selected" : ""} style={{ paddingLeft: `${(category.depth - 1) * 24 + 14}px` }}>
                  <span className="tree-line" aria-hidden="true">{category.depth > 1 ? "└" : "◆"}</span>
                  <div className="category-copy">
                    <strong>{category.name}</strong>
                    {category.description && <small className="category-description">{category.description}</small>}
                  </div>
                  <span>{category.articleCount}件</span>
                  <div className="category-row-actions">
                    <button type="button" onClick={() => startEditing(category)}>編集</button>
                    <button type="button" className="danger" disabled={saving} onClick={() => void deleteCategory(category)}>削除</button>
                  </div>
                </li>
              ))}
            </ul>
          )}
        </section>

        <section className="panel sticky-panel">
          <div className="panel-heading">
            <h2>{editingCategory ? "分類を編集" : "新しい分類"}</h2>
            {editingCategory && <button type="button" className="text-button" onClick={resetForm}>新規作成に戻る</button>}
          </div>
          <form className="form-stack" onSubmit={submit}>
            <label>
              <span>分類名 <b className="required">必須</b></span>
              <input value={name} onChange={(event) => setName(event.target.value)} maxLength={100} placeholder="例：Windows" />
            </label>
            <label>
              <span>分類の説明</span>
              <textarea
                value={description}
                onChange={(event) => setDescription(event.target.value)}
                maxLength={500}
                rows={3}
                placeholder="例：Windowsの起動、終了、画面操作に関するFAQ"
              />
              <small>{description.length} / 500文字</small>
            </label>
            <label>
              <span>{editingCategory ? "移動先" : "親となる分類"}</span>
              <select value={parentId} onChange={(event) => setParentId(event.target.value)}>
                <option value="">一番上の階層</option>
                {availableParents.map((category) => (
                  <option key={category.id} value={category.id}>
                    {"　".repeat(Math.max(0, category.depth - 1))}{category.name}
                  </option>
                ))}
              </select>
            </label>
            <p className="category-form-help">
              {editingCategory
                ? "移動すると配下分類も一緒に移動します。6階層以上や循環する移動はできません。"
                : "別の観点は分類を増やしすぎず、今後追加するタグで補います。"}
            </p>
            <button type="submit" className="button primary" disabled={saving || !name.trim()}>
              {saving ? "保存しています…" : editingCategory ? "変更を保存" : "分類を作成"}
            </button>
          </form>
        </section>
      </div>
    </div>
  );
}
