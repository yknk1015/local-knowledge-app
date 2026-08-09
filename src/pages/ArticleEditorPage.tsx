import { FormEvent, useEffect, useMemo, useRef, useState } from "react";
import { open } from "@tauri-apps/plugin-dialog";
import { Link, useNavigate, useParams } from "react-router-dom";
import { knowledgeApi, toAppError } from "../api/knowledgeApi";
import { ErrorState, LoadingState } from "../components/Feedback";
import { RichTextEditor, type ManagedImageSource } from "../components/RichTextEditor";
import type { AppError, ArticleStatus, Category } from "../types/domain";

const EMPTY_DOCUMENT: Record<string, unknown> = {
  type: "doc",
  content: [{ type: "paragraph" }],
};

export function ArticleEditorPage() {
  const { articleId } = useParams();
  const navigate = useNavigate();
  const isEditing = Boolean(articleId);
  const [categories, setCategories] = useState<Category[]>([]);
  const [title, setTitle] = useState("");
  const [categoryId, setCategoryId] = useState("");
  const [summary, setSummary] = useState("");
  const [bodyDoc, setBodyDoc] = useState<Record<string, unknown>>(EMPTY_DOCUMENT);
  const [imageSources, setImageSources] = useState<ManagedImageSource[]>([]);
  const [status, setStatus] = useState<ArticleStatus>("draft");
  const [importance, setImportance] = useState(1);
  const [newBadgeEnabled, setNewBadgeEnabled] = useState(false);
  const [newBadgeUntil, setNewBadgeUntil] = useState("");
  const [updatedBadgeEnabled, setUpdatedBadgeEnabled] = useState(false);
  const [updatedBadgeUntil, setUpdatedBadgeUntil] = useState("");
  const [isHidden, setIsHidden] = useState(false);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<AppError | null>(null);
  const [validation, setValidation] = useState<string[]>([]);
  const saveFeedbackRef = useRef<HTMLDivElement>(null);
  const stagedImageIdsRef = useRef(new Set<string>());

  useEffect(() => () => {
    for (const id of stagedImageIdsRef.current) {
      void knowledgeApi.discardStagedArticleImage(id);
    }
  }, []);

  useEffect(() => {
    let active = true;
    const load = async () => {
      setLoading(true);
      try {
        const categoryItems = await knowledgeApi.listCategories();
        if (!active) return;
        setCategories(categoryItems);
        if (!articleId) {
          setCategoryId(categoryItems[0]?.id ?? "");
          return;
        }
        const article = await knowledgeApi.getArticle(articleId);
        if (!active) return;
        if (article.deletedAt) {
          setError({
            code: "ART-006",
            message: "削除済みFAQは編集できません。",
            action: "FAQ詳細画面またはFAQ管理画面から復元してから編集してください。",
          });
          return;
        }
        setTitle(article.title);
        setCategoryId(article.categoryId);
        setSummary(article.summary);
        setBodyDoc(article.bodyDoc);
        setImageSources(
          article.attachments.map((attachment) => ({
            id: attachment.id,
            assetPath: attachment.assetPath,
            altText: attachment.altText,
          })),
        );
        setStatus(article.status);
        setImportance(article.importance);
        setNewBadgeEnabled(Boolean(article.newBadgeUntil));
        setNewBadgeUntil(article.newBadgeUntil ?? "");
        setUpdatedBadgeEnabled(Boolean(article.updatedBadgeUntil));
        setUpdatedBadgeUntil(article.updatedBadgeUntil ?? "");
        setIsHidden(article.isHidden);
      } catch (caught) {
        if (active) setError(toAppError(caught));
      } finally {
        if (active) setLoading(false);
      }
    };
    void load();
    return () => { active = false; };
  }, [articleId]);

  useEffect(() => {
    if (!error && validation.length === 0) return;
    const feedback = saveFeedbackRef.current;
    if (!feedback) return;

    feedback.focus({ preventScroll: true });
    feedback.scrollIntoView({ behavior: "smooth", block: "start" });
  }, [error, validation]);

  const canSave = useMemo(() => title.trim() !== "" && categoryId !== "", [categoryId, title]);

  const requestImage = async (file?: File): Promise<ManagedImageSource | null> => {
    setError(null);
    try {
      const staged = file
        ? await knowledgeApi.stageArticleImageBytes(
            file.name || "pasted-image",
            Array.from(new Uint8Array(await file.arrayBuffer())),
          )
        : await (async () => {
            const selected = await open({
              multiple: false,
              directory: false,
              filters: [
                {
                  name: "画像（10MB以下）",
                  extensions: ["png", "jpg", "jpeg", "webp", "gif"],
                },
              ],
            });
            return typeof selected === "string"
              ? knowledgeApi.stageArticleImage(selected)
              : null;
          })();
      if (!staged) return null;
      const source = {
        id: staged.id,
        assetPath: staged.assetPath,
        altText: staged.altText,
      };
      stagedImageIdsRef.current.add(staged.id);
      setImageSources((current) => [...current, source]);
      return source;
    } catch (caught) {
      setError(toAppError(caught));
      return null;
    }
  };

  const discardImage = async (id: string) => {
    stagedImageIdsRef.current.delete(id);
    setImageSources((current) => current.filter((image) => image.id !== id));
    await knowledgeApi.discardStagedArticleImage(id);
  };

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    const problems: string[] = [];
    if (!title.trim()) problems.push("タイトルを入力してください。");
    if (!categoryId) problems.push("所属分類を選択してください。");
    if (status === "published" && !summary.trim()) problems.push("公開する場合は概要を入力してください。");
    if (status === "published" && JSON.stringify(bodyDoc) === JSON.stringify(EMPTY_DOCUMENT)) {
      problems.push("公開する場合は回答を入力してください。");
    }
    if (newBadgeEnabled && !newBadgeUntil) problems.push("新着フラグの表示終了日を選択してください。");
    if (updatedBadgeEnabled && !updatedBadgeUntil) problems.push("更新フラグの表示終了日を選択してください。");
    setValidation(problems);
    if (problems.length > 0) return;

    setSaving(true);
    setError(null);
    try {
      const saved = await knowledgeApi.saveArticle({
        id: articleId,
        title: title.trim(),
        categoryId,
        summary: summary.trim(),
        bodyDoc,
        status,
        importance,
        newBadgeUntil: newBadgeEnabled ? newBadgeUntil : null,
        updatedBadgeUntil: updatedBadgeEnabled ? updatedBadgeUntil : null,
        isHidden,
      });
      navigate(`/articles/${saved.id}`);
    } catch (caught) {
      setError(toAppError(caught));
    } finally {
      setSaving(false);
    }
  };

  if (loading) return <div className="page"><LoadingState label="編集画面を準備しています…" /></div>;
  if (error && isEditing && !title) return <div className="page"><ErrorState error={error} /></div>;

  if (categories.length === 0) {
    return (
      <div className="page">
        <div className="page-heading"><h1>FAQを登録する準備</h1></div>
        <div className="empty-state compact">
          <h2>先に分類を作成してください</h2>
          <p>FAQは必ず1つの分類に所属します。</p>
          <Link to="/categories" className="button primary">分類を作成する</Link>
        </div>
      </div>
    );
  }

  return (
    <div className="page editor-page">
      <div className="page-heading split">
        <div>
          <span className="eyebrow">{isEditing ? "内容を更新" : "知識を追加"}</span>
          <h1>{isEditing ? "FAQを編集" : "新しいFAQ"}</h1>
          <p>下書きのまま保存し、内容を整えてから公開できます。</p>
        </div>
        <Link to={articleId ? `/articles/${articleId}` : "/search"} className="button ghost">キャンセル</Link>
      </div>

      {(error || validation.length > 0) && (
        <div ref={saveFeedbackRef} className="save-feedback" tabIndex={-1}>
          {error && <ErrorState error={error} />}
          {validation.length > 0 && (
            <div className="validation-summary" role="alert">
              <strong>FAQを保存できませんでした。入力内容を確認してください</strong>
              <ul>{validation.map((problem) => <li key={problem}>{problem}</li>)}</ul>
            </div>
          )}
        </div>
      )}

      <form onSubmit={submit} className="editor-form">
        <section className="panel form-section">
          <div className="section-number">1</div>
          <div className="section-content">
            <h2>基本情報</h2>
            <div className="form-grid">
              <label className="full-width">
                <span>タイトル（質問文） <b className="required">必須</b></span>
                <input value={title} onChange={(event) => setTitle(event.target.value)} maxLength={200} placeholder="例：Windowsの画面が真っ暗になったときは？" />
              </label>
              <label>
                <span>所属分類 <b className="required">必須</b></span>
                <select value={categoryId} onChange={(event) => setCategoryId(event.target.value)}>
                  <option value="">選択してください</option>
                  {categories.map((category) => (
                    <option key={category.id} value={category.id}>
                      {"　".repeat(Math.max(0, category.depth - 1))}{category.name}
                    </option>
                  ))}
                </select>
              </label>
              <label>
                <span>重要度</span>
                <select value={importance} onChange={(event) => setImportance(Number(event.target.value))}>
                  <option value={1}>1 — 通常</option>
                  <option value={2}>2 — 重要</option>
                  <option value={3}>3 — 最重要</option>
                </select>
              </label>
              <label className="full-width">
                <span>概要 {status === "published" && <b className="required">公開時必須</b>}</span>
                <textarea value={summary} onChange={(event) => setSummary(event.target.value)} maxLength={500} rows={3} placeholder="検索結果に表示する短い説明を入力します。" />
                <small>{summary.length} / 500文字</small>
              </label>
            </div>
          </div>
        </section>

        <section className="panel form-section">
          <div className="section-number">2</div>
          <div className="section-content">
            <h2>回答</h2>
            <p className="section-help">見出し、箇条書き、表、画像、参考URLを使って分かりやすく整理できます。段落の間隔は読みやすい幅に抑えています。</p>
            <RichTextEditor
              value={bodyDoc}
              onChange={setBodyDoc}
              imageSources={imageSources}
              onRequestImage={requestImage}
              onDiscardImage={discardImage}
              disabled={saving}
            />
            <p className="image-help">
              「画像を追加」またはクリップボードからの貼り付けで挿入できます。画像はアプリ管理フォルダへコピーされ、外部へ送信されません。
            </p>
            <p className="url-help">
              URLを入力・貼り付けると、クリックされない文字として安全に保存されます。クリック可能にする場合は文字を選択して「参考URL」、文字へ戻す場合は「リンク解除」を選んでください。
            </p>
          </div>
        </section>

        <section className="panel form-section">
          <div className="section-number">3</div>
          <div className="section-content">
            <h2>表示設定</h2>
            <p className="section-help">新着・更新フラグは、選択した表示終了日当日まで表示されます。</p>
            <div className="display-settings-grid">
              <div className="display-setting-card">
                <label className="check-option">
                  <input
                    type="checkbox"
                    checked={newBadgeEnabled}
                    onChange={(event) => setNewBadgeEnabled(event.target.checked)}
                  />
                  <span><strong>「新着」を表示する</strong><small>検索結果とFAQ詳細に新着フラグを表示します。</small></span>
                </label>
                <label className="date-option">
                  <span>表示終了日</span>
                  <input
                    type="date"
                    value={newBadgeUntil}
                    disabled={!newBadgeEnabled}
                    aria-required={newBadgeEnabled}
                    onChange={(event) => setNewBadgeUntil(event.target.value)}
                  />
                </label>
              </div>
              <div className="display-setting-card">
                <label className="check-option">
                  <input
                    type="checkbox"
                    checked={updatedBadgeEnabled}
                    onChange={(event) => setUpdatedBadgeEnabled(event.target.checked)}
                  />
                  <span><strong>「更新」を表示する</strong><small>検索結果とFAQ詳細に更新フラグを表示します。</small></span>
                </label>
                <label className="date-option">
                  <span>表示終了日</span>
                  <input
                    type="date"
                    value={updatedBadgeUntil}
                    disabled={!updatedBadgeEnabled}
                    aria-required={updatedBadgeEnabled}
                    onChange={(event) => setUpdatedBadgeUntil(event.target.value)}
                  />
                </label>
              </div>
              <div className="display-setting-card hidden-setting">
                <label className="check-option">
                  <input type="checkbox" checked={isHidden} onChange={(event) => setIsHidden(event.target.checked)} />
                  <span><strong>このFAQを非表示にする</strong><small>公開状態でも通常検索には出さず、FAQ管理画面からのみ確認・編集できます。</small></span>
                </label>
              </div>
            </div>
          </div>
        </section>

        <section className="publish-bar">
          <label>
            <span>保存後の状態</span>
            <select value={status} onChange={(event) => setStatus(event.target.value as ArticleStatus)}>
              <option value="draft">下書き — 自分だけの管理一覧に表示</option>
              <option value="published">公開 — 通常の検索結果に表示</option>
              {isEditing && <option value="archived">廃止 — 通常の検索結果から除外</option>}
            </select>
          </label>
          <button type="submit" className="button primary large" disabled={!canSave || saving}>
            {saving ? "保存しています…" : status === "draft" ? "下書きを保存" : "FAQを保存"}
          </button>
        </section>
      </form>
    </div>
  );
}
