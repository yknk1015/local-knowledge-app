import { FormEvent, useEffect, useMemo, useState } from "react";
import { knowledgeApi, toAppError } from "../api/knowledgeApi";
import { ErrorState, LoadingState } from "../components/Feedback";
import type { AppError, TagMasterItem } from "../types/domain";
import "./TagsPage.css";

export function TagsPage() {
  const [tags, setTags] = useState<TagMasterItem[]>([]);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [name, setName] = useState("");
  const [filter, setFilter] = useState("");
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<AppError | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const filteredTags = useMemo(() => {
    const query = filter.normalize("NFKC").toLocaleLowerCase("ja-JP").trim();
    if (!query) return tags;
    return tags.filter((tag) => tag.name.normalize("NFKC").toLocaleLowerCase("ja-JP").includes(query));
  }, [filter, tags]);

  const select = (tag: TagMasterItem) => {
    setSelectedId(tag.id);
    setName(tag.name);
    setError(null);
    setNotice(null);
  };

  const startNew = () => {
    setSelectedId(null);
    setName("");
    setError(null);
    setNotice(null);
  };

  useEffect(() => {
    let active = true;
    void knowledgeApi.listTags().then(
      (items) => {
        if (!active) return;
        setTags(items);
        if (items.length > 0) select(items[0]!);
      },
      (caught) => { if (active) setError(toAppError(caught)); },
    ).finally(() => { if (active) setLoading(false); });
    return () => { active = false; };
  }, []);

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    setSaving(true);
    setError(null);
    setNotice(null);
    try {
      const saved = await knowledgeApi.saveTag(selectedId ?? undefined, name);
      setTags((current) => [...current.filter((tag) => tag.id !== saved.id), saved]
        .sort((left, right) => left.name.localeCompare(right.name, "ja")));
      setSelectedId(saved.id);
      setName(saved.name);
      setNotice(selectedId ? "タグ名を変更しました。使用中のFAQにも反映されます。" : "タグを追加しました。FAQ作成画面で選択できます。");
    } catch (caught) {
      setError(toAppError(caught));
    } finally {
      setSaving(false);
    }
  };

  const remove = async () => {
    const selected = tags.find((tag) => tag.id === selectedId);
    if (!selected || !window.confirm(`タグ「${selected.name}」を削除しますか？`)) return;
    setSaving(true);
    setError(null);
    setNotice(null);
    try {
      await knowledgeApi.deleteTag(selected.id);
      const remaining = tags.filter((tag) => tag.id !== selected.id);
      setTags(remaining);
      if (remaining.length > 0) {
        setSelectedId(remaining[0]!.id);
        setName(remaining[0]!.name);
      } else {
        setSelectedId(null);
        setName("");
      }
      setNotice("タグを削除しました。");
    } catch (caught) {
      setError(toAppError(caught));
    } finally {
      setSaving(false);
    }
  };

  if (loading) return <div className="page"><LoadingState label="タグマスターを読み込んでいます…" /></div>;

  const selected = tags.find((tag) => tag.id === selectedId);
  return (
    <div className="page tags-page">
      <div className="page-heading">
        <div><h1>タグマスター</h1><p>FAQを複数の観点で関連付ける共通タグを管理します。</p></div>
        <button type="button" className="button primary" onClick={startNew}>新しいタグ</button>
      </div>
      {error && <ErrorState error={error} />}
      {notice && <div className="success-notice" role="status">{notice}</div>}
      <div className="tag-workspace">
        <aside className="panel tag-list-panel">
          <label htmlFor="tag-filter">タグを検索</label>
          <input id="tag-filter" type="search" value={filter} onChange={(event) => setFilter(event.target.value)} placeholder="タグ名" />
          <nav aria-label="タグ一覧">
            {filteredTags.map((tag) => (
              <button key={tag.id} type="button" className={selectedId === tag.id ? "active" : ""} onClick={() => select(tag)}>
                <strong>{tag.name}</strong><small>{tag.usageCount}件のFAQで使用</small>
              </button>
            ))}
            {filteredTags.length === 0 && <p>{tags.length === 0 ? "タグはまだ登録されていません。" : "一致するタグはありません。"}</p>}
          </nav>
        </aside>
        <form className="panel tag-editor-panel" onSubmit={submit}>
          <div className="tag-editor-heading">
            <div><span className="eyebrow">TAG MASTER</span><h2>{selectedId ? "タグを編集" : "新しいタグ"}</h2></div>
            {selectedId && <button type="button" className="button danger-outline" disabled={saving} onClick={() => void remove()}>削除</button>}
          </div>
          <label htmlFor="tag-name">タグ名 <b className="required">必須</b></label>
          <input id="tag-name" value={name} maxLength={100} required onChange={(event) => setName(event.target.value)} placeholder="例：ディスプレイ" />
          <small className="field-help">同じ意味のタグを重複させず、短く分かりやすい名前にします。</small>
          {selected && selected.usageCount > 0 && (
            <p className="tag-usage-note">このタグは{selected.usageCount}件のFAQで使用中です。名前の変更は使用中のFAQへ一括反映されます。</p>
          )}
          <div className="tag-editor-actions">
            <button type="submit" className="button primary large" disabled={saving || !name.trim()}>{saving ? "保存しています…" : "タグを保存"}</button>
          </div>
        </form>
      </div>
    </div>
  );
}
