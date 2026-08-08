import { FormEvent, useEffect, useMemo, useRef, useState } from "react";
import { Link, useNavigate, useParams } from "react-router-dom";
import { knowledgeApi, toAppError } from "../api/knowledgeApi";
import { ErrorState, LoadingState } from "../components/Feedback";
import { RichTextEditor } from "../components/RichTextEditor";
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
  const [status, setStatus] = useState<ArticleStatus>("draft");
  const [importance, setImportance] = useState(1);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<AppError | null>(null);
  const [validation, setValidation] = useState<string[]>([]);
  const saveFeedbackRef = useRef<HTMLDivElement>(null);

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
        setStatus(article.status);
        setImportance(article.importance);
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

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    const problems: string[] = [];
    if (!title.trim()) problems.push("タイトルを入力してください。");
    if (!categoryId) problems.push("所属分類を選択してください。");
    if (status === "published" && !summary.trim()) problems.push("公開する場合は概要を入力してください。");
    if (status === "published" && JSON.stringify(bodyDoc) === JSON.stringify(EMPTY_DOCUMENT)) {
      problems.push("公開する場合は回答を入力してください。");
    }
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
            <p className="section-help">見出し、箇条書き、表、参考URLを使って分かりやすく整理できます。</p>
            <RichTextEditor value={bodyDoc} onChange={setBodyDoc} disabled={saving} />
            <p className="url-help">
              URLを入力・貼り付けると、クリックされない文字として安全に保存されます。クリック可能にする場合は文字を選択して「参考URL」、文字へ戻す場合は「リンク解除」を選んでください。
            </p>
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
