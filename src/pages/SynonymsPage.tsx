import { FormEvent, useEffect, useMemo, useState } from "react";
import { knowledgeApi, toAppError } from "../api/knowledgeApi";
import { ErrorState, LoadingState } from "../components/Feedback";
import { MultiValueInput } from "../components/MultiValueInput";
import type { AppError, SynonymGroup } from "../types/domain";
import "./SynonymsPage.css";

export function SynonymsPage() {
  const [groups, setGroups] = useState<SynonymGroup[]>([]);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [displayName, setDisplayName] = useState("");
  const [terms, setTerms] = useState<string[]>([]);
  const [filter, setFilter] = useState("");
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<AppError | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const filteredGroups = useMemo(() => {
    const query = filter.normalize("NFKC").toLocaleLowerCase("ja-JP").trim();
    if (!query) return groups;
    return groups.filter((group) => [group.displayName, ...group.terms]
      .some((value) => value.normalize("NFKC").toLocaleLowerCase("ja-JP").includes(query)));
  }, [filter, groups]);

  const select = (group: SynonymGroup) => {
    setSelectedId(group.id);
    setDisplayName(group.displayName);
    setTerms(group.terms);
    setError(null);
    setNotice(null);
  };

  const startNew = () => {
    setSelectedId(null);
    setDisplayName("");
    setTerms([]);
    setError(null);
    setNotice(null);
  };

  const load = async () => {
    setLoading(true);
    setError(null);
    try {
      const items = await knowledgeApi.listSynonymGroups();
      setGroups(items);
      if (selectedId) {
        const current = items.find((group) => group.id === selectedId);
        if (current) select(current);
        else startNew();
      } else if (items.length > 0) {
        select(items[0]!);
      }
    } catch (caught) {
      setError(toAppError(caught));
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => { void load(); }, []);

  const persist = async (allowConflicts: boolean) => {
    const saved = await knowledgeApi.saveSynonymGroup(
      selectedId ?? undefined,
      displayName,
      terms,
      allowConflicts,
    );
    setGroups((current) => [...current.filter((group) => group.id !== saved.id), saved]
      .sort((left, right) => left.displayName.localeCompare(right.displayName, "ja")));
    select(saved);
    setNotice("同義語グループを保存しました。次の検索から反映されます。");
  };

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    setSaving(true);
    setError(null);
    setNotice(null);
    try {
      await persist(false);
    } catch (caught) {
      const appError = toAppError(caught);
      if (appError.code === "SYN-003" && window.confirm(
        `${appError.message}\n\n${appError.action}\n\n重複を許可して保存しますか？`,
      )) {
        try {
          await persist(true);
        } catch (retryError) {
          setError(toAppError(retryError));
        }
      } else {
        setError(appError);
      }
    } finally {
      setSaving(false);
    }
  };

  const remove = async () => {
    if (!selectedId || !window.confirm(`「${displayName}」の同義語グループを削除しますか？`)) return;
    setSaving(true);
    setError(null);
    try {
      await knowledgeApi.deleteSynonymGroup(selectedId);
      setGroups((current) => current.filter((group) => group.id !== selectedId));
      startNew();
      setNotice("同義語グループを削除しました。");
    } catch (caught) {
      setError(toAppError(caught));
    } finally {
      setSaving(false);
    }
  };

  if (loading) return <div className="page"><LoadingState label="同義語辞書を読み込んでいます…" /></div>;

  return (
    <div className="page synonyms-page">
      <div className="page-heading">
        <div><h1>同義語の管理</h1><p>表記が違っても同じFAQを見つけられるようにします。</p></div>
        <button type="button" className="button primary" onClick={startNew}>新しいグループ</button>
      </div>
      {error && <ErrorState error={error} />}
      {notice && <div className="success-notice" role="status">{notice}</div>}
      <div className="synonym-workspace">
        <aside className="panel synonym-list-panel">
          <label htmlFor="synonym-filter">グループを検索</label>
          <input id="synonym-filter" type="search" value={filter} onChange={(event) => setFilter(event.target.value)} placeholder="代表語・同義語" />
          <nav aria-label="同義語グループ">
            {filteredGroups.map((group) => (
              <button key={group.id} type="button" className={selectedId === group.id ? "active" : ""} onClick={() => select(group)}>
                <strong>{group.displayName}</strong><small>{group.terms.length}件の同義語</small>
              </button>
            ))}
            {filteredGroups.length === 0 && <p>一致するグループはありません。</p>}
          </nav>
        </aside>
        <form className="panel synonym-editor-panel" onSubmit={submit}>
          <div className="synonym-editor-heading">
            <div><span className="eyebrow">SYNONYM</span><h2>{selectedId ? "グループを編集" : "新しいグループ"}</h2></div>
            {selectedId && <button type="button" className="button danger-outline" disabled={saving} onClick={() => void remove()}>削除</button>}
          </div>
          <label htmlFor="synonym-display-name">代表語 <b className="required">必須</b></label>
          <input id="synonym-display-name" value={displayName} maxLength={100} required onChange={(event) => setDisplayName(event.target.value)} placeholder="例：パソコン" />
          <small className="field-help">一覧でグループを識別する名前です。検索時は代表語自身も同義語として扱います。</small>
          <MultiValueInput
            label="同義語"
            description="同じ意味として検索する語"
            values={terms}
            onChange={setTerms}
            placeholder="例：PC"
            maximumLength={100}
          />
          <div className="synonym-editor-actions">
            <button type="submit" className="button primary large" disabled={saving || !displayName.trim()}>{saving ? "保存しています…" : "同義語を保存"}</button>
          </div>
        </form>
      </div>
    </div>
  );
}
