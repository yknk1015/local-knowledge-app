import { FormEvent, useCallback, useEffect, useMemo, useRef, useState } from "react";
import { open } from "@tauri-apps/plugin-dialog";
import {
  Link,
  unstable_usePrompt as usePrompt,
  useBeforeUnload,
  useNavigate,
  useParams,
} from "react-router-dom";
import { knowledgeApi, toAppError } from "../api/knowledgeApi";
import { ErrorState, LoadingState } from "../components/Feedback";
import { RichTextEditor, type ManagedImageSource } from "../components/RichTextEditor";
import type {
  AppError,
  ArticleStatus,
  Category,
  CodexMergePublicationContext,
  RelatedArticleSummary,
  TagMasterItem,
} from "../types/domain";
import "./ArticleEditorPage.css";

const EMPTY_DOCUMENT: Record<string, unknown> = {
  type: "doc",
  content: [{ type: "paragraph" }],
};

const VALIDATION_MESSAGES = {
  title: "タイトルを入力してください。",
  category: "所属分類を選択してください。",
  summary: "公開する場合は概要を入力してください。",
  body: "公開する場合は回答を入力してください。",
  newBadge: "新着フラグの表示終了日を選択してください。",
  mergeNewBadge: "統合FAQを公開する場合は、新着フラグと本日以降の表示終了日を設定してください。",
  updatedBadge: "更新フラグの表示終了日を選択してください。",
} as const;

const UNSAVED_CHANGES_MESSAGE = "保存していない変更があります。このページから移動すると変更内容は失われます。移動しますか？";

export function getDefaultBadgeUntil(today: Date = new Date()): string {
  const endDate = new Date(today.getFullYear(), today.getMonth(), today.getDate() + 7);
  return formatLocalDate(endDate);
}

function formatLocalDate(date: Date): string {
  const year = date.getFullYear();
  const month = String(date.getMonth() + 1).padStart(2, "0");
  const day = String(date.getDate()).padStart(2, "0");
  return `${year}-${month}-${day}`;
}

function documentHasContent(value: unknown): boolean {
  if (Array.isArray(value)) return value.some(documentHasContent);
  if (!value || typeof value !== "object") return false;
  const node = value as Record<string, unknown>;
  if (typeof node.text === "string" && node.text.trim() !== "") return true;
  if (node.type === "image" || node.type === "table" || node.type === "copyBlock") return true;
  return documentHasContent(node.content);
}

function statusCopy(status: ArticleStatus) {
  if (status === "published") return { label: "公開", description: "通常の検索結果に表示されます" };
  if (status === "archived") return { label: "廃止", description: "通常の検索結果から除外されます" };
  return { label: "下書き", description: "FAQ管理画面にだけ表示されます" };
}

export function ArticleEditorPage() {
  const { articleId } = useParams();
  const navigate = useNavigate();
  const isEditing = Boolean(articleId);
  const [categories, setCategories] = useState<Category[]>([]);
  const [tagMaster, setTagMaster] = useState<TagMasterItem[]>([]);
  const [title, setTitle] = useState("");
  const [categoryId, setCategoryId] = useState("");
  const [summary, setSummary] = useState("");
  const [bodyDoc, setBodyDoc] = useState<Record<string, unknown>>(EMPTY_DOCUMENT);
  const [imageSources, setImageSources] = useState<ManagedImageSource[]>([]);
  const [status, setStatus] = useState<ArticleStatus>("draft");
  const [initialStatus, setInitialStatus] = useState<ArticleStatus>("draft");
  const [mergeContext, setMergeContext] = useState<CodexMergePublicationContext | null>(null);
  const [importance, setImportance] = useState(1);
  const [newBadgeEnabled, setNewBadgeEnabled] = useState(false);
  const [newBadgeUntil, setNewBadgeUntil] = useState("");
  const [updatedBadgeEnabled, setUpdatedBadgeEnabled] = useState(false);
  const [updatedBadgeUntil, setUpdatedBadgeUntil] = useState("");
  const [isHidden, setIsHidden] = useState(false);
  const [symptoms, setSymptoms] = useState<string[]>([]);
  const [causes, setCauses] = useState<string[]>([]);
  const [targets, setTargets] = useState<string[]>([]);
  const [errorCodes, setErrorCodes] = useState<string[]>([]);
  const [procedures, setProcedures] = useState<string[]>([]);
  const [cautions, setCautions] = useState<string[]>([]);
  const [tags, setTags] = useState<string[]>([]);
  const [tagToAdd, setTagToAdd] = useState("");
  const [searchTerms, setSearchTerms] = useState<string[]>([]);
  const [relatedArticles, setRelatedArticles] = useState<RelatedArticleSummary[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadedRouteKey, setLoadedRouteKey] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<AppError | null>(null);
  const [validation, setValidation] = useState<string[]>([]);
  const saveFeedbackRef = useRef<HTMLDivElement>(null);
  const formRef = useRef<HTMLFormElement>(null);
  const stagedImageIdsRef = useRef(new Set<string>());
  const initialEditorFingerprintRef = useRef<string | null>(null);
  const unsavedChangesRef = useRef(false);
  const allowNavigationRef = useRef(false);
  const routeKey = articleId ?? "__new__";

  useEffect(() => () => {
    for (const id of stagedImageIdsRef.current) {
      void knowledgeApi.discardStagedArticleImage(id);
    }
  }, []);

  useEffect(() => {
    let active = true;
    const load = async () => {
      setLoading(true);
      setLoadedRouteKey(null);
      initialEditorFingerprintRef.current = null;
      allowNavigationRef.current = false;
      try {
        const [categoryItems, tagItems] = await Promise.all([
          knowledgeApi.listCategories(),
          knowledgeApi.listTags(),
        ]);
        if (!active) return;
        setCategories(categoryItems);
        setTagMaster(tagItems);
        if (!articleId) {
          setCategoryId("");
          return;
        }
        const [article, publicationContext] = await Promise.all([
          knowledgeApi.getArticle(articleId),
          knowledgeApi.getCodexMergePublicationContext(articleId),
        ]);
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
        setInitialStatus(article.status);
        setMergeContext(publicationContext);
        setImportance(article.importance);
        setNewBadgeEnabled(Boolean(article.newBadgeUntil));
        setNewBadgeUntil(article.newBadgeUntil ?? "");
        setUpdatedBadgeEnabled(Boolean(article.updatedBadgeUntil));
        setUpdatedBadgeUntil(article.updatedBadgeUntil ?? "");
        setIsHidden(article.isHidden);
        setSymptoms(article.symptoms);
        setCauses(article.causes);
        setTargets(article.targets);
        setErrorCodes(article.errorCodes);
        setProcedures(article.procedures);
        setCautions(article.cautions);
        setTags(article.tags);
        setSearchTerms(article.searchTerms);
        setRelatedArticles(article.relatedArticles);
      } catch (caught) {
        if (active) setError(toAppError(caught));
      } finally {
        if (active) {
          setLoadedRouteKey(routeKey);
          setLoading(false);
        }
      }
    };
    void load();
    return () => { active = false; };
  }, [articleId, routeKey]);

  useEffect(() => {
    if (!error && validation.length === 0) return;
    const feedback = saveFeedbackRef.current;
    if (!feedback) return;

    feedback.focus({ preventScroll: true });
    feedback.scrollIntoView({ behavior: "smooth", block: "start" });
  }, [error, validation]);

  useEffect(() => {
    const saveWithKeyboard = (event: KeyboardEvent) => {
      if (!(event.ctrlKey || event.metaKey) || event.key.toLowerCase() !== "s") return;
      event.preventDefault();
      if (!saving) formRef.current?.requestSubmit();
    };
    window.addEventListener("keydown", saveWithKeyboard);
    return () => window.removeEventListener("keydown", saveWithKeyboard);
  }, [saving]);

  const canSave = useMemo(() => title.trim() !== "" && categoryId !== "", [categoryId, title]);
  const hasAnswer = useMemo(() => documentHasContent(bodyDoc), [bodyDoc]);
  const editorFingerprint = useMemo(() => JSON.stringify({
    title,
    categoryId,
    summary,
    bodyDoc,
    status,
    importance,
    newBadgeEnabled,
    newBadgeUntil,
    updatedBadgeEnabled,
    updatedBadgeUntil,
    isHidden,
    symptoms,
    causes,
    targets,
    errorCodes,
    procedures,
    cautions,
    tags,
    searchTerms,
    relatedArticleIds: relatedArticles.map((article) => article.id),
  }), [
    bodyDoc,
    categoryId,
    cautions,
    causes,
    errorCodes,
    importance,
    isHidden,
    newBadgeEnabled,
    newBadgeUntil,
    procedures,
    relatedArticles,
    searchTerms,
    status,
    summary,
    symptoms,
    tags,
    targets,
    title,
    updatedBadgeEnabled,
    updatedBadgeUntil,
  ]);
  const hasUnsavedChanges = loadedRouteKey === routeKey
    && initialEditorFingerprintRef.current !== null
    && initialEditorFingerprintRef.current !== editorFingerprint;
  unsavedChangesRef.current = hasUnsavedChanges;
  const shouldBlockNavigation = useCallback(
    () => unsavedChangesRef.current && !allowNavigationRef.current,
    [],
  );
  usePrompt({ when: shouldBlockNavigation, message: UNSAVED_CHANGES_MESSAGE });
  useBeforeUnload(useCallback((event) => {
    if (!unsavedChangesRef.current || allowNavigationRef.current) return;
    event.preventDefault();
    event.returnValue = "";
  }, []));

  useEffect(() => {
    if (loadedRouteKey !== routeKey || loading || initialEditorFingerprintRef.current !== null) return;
    initialEditorFingerprintRef.current = editorFingerprint;
    unsavedChangesRef.current = false;
  }, [editorFingerprint, loadedRouteKey, loading, routeKey]);
  const isInitialMergePublication = Boolean(
    mergeContext && initialStatus !== "published" && status === "published",
  );
  const selectedStatus = statusCopy(status);
  const missingBasicFields = [!title.trim() ? "タイトル" : "", !categoryId ? "所属分類" : ""].filter(Boolean);
  const basicSectionComplete = canSave && (status !== "published" || summary.trim() !== "");
  const displaySectionComplete = (!newBadgeEnabled || Boolean(newBadgeUntil))
    && (!updatedBadgeEnabled || Boolean(updatedBadgeUntil))
    && (!isInitialMergePublication || (newBadgeEnabled && Boolean(newBadgeUntil)));
  const currentStateReady = basicSectionComplete && (status !== "published" || hasAnswer) && displaySectionComplete;
  const availableTags = tagMaster.filter((tag) => !tags.includes(tag.name));
  const clearValidation = (...messages: string[]) => {
    setValidation((current) => current.filter((message) => !messages.includes(message)));
  };

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
    if (!title.trim()) problems.push(VALIDATION_MESSAGES.title);
    if (!categoryId) problems.push(VALIDATION_MESSAGES.category);
    if (status === "published" && !summary.trim()) problems.push(VALIDATION_MESSAGES.summary);
    if (status === "published" && !hasAnswer) problems.push(VALIDATION_MESSAGES.body);
    if (isInitialMergePublication && (!newBadgeEnabled || !newBadgeUntil || newBadgeUntil < formatLocalDate(new Date()))) {
      problems.push(VALIDATION_MESSAGES.mergeNewBadge);
    } else if (newBadgeEnabled && !newBadgeUntil) {
      problems.push(VALIDATION_MESSAGES.newBadge);
    }
    if (updatedBadgeEnabled && !updatedBadgeUntil) problems.push(VALIDATION_MESSAGES.updatedBadge);
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
        symptoms,
        causes,
        targets,
        errorCodes,
        procedures,
        cautions,
        tags,
        searchTerms,
        relatedArticleIds: relatedArticles.map((article) => article.id),
      });
      initialEditorFingerprintRef.current = editorFingerprint;
      unsavedChangesRef.current = false;
      stagedImageIdsRef.current.clear();
      setInitialStatus(saved.status);
      if (mergeContext && saved.status === "published" && !mergeContext.allSourcesMerged) {
        if (!mergeContext.canMarkMerged) {
          setError({
            code: "CDX-012",
            message: "統合FAQは公開しましたが、元FAQに変更または削除があるため統合済みにはしていません。",
            action: "FAQ管理画面で元FAQを確認し、必要であれば現在のFAQから統合をやり直してください。",
          });
          return;
        }
        const sourceTitles = mergeContext.sourceArticles
          .map((source) => `・${source.title}`)
          .join("\n");
        const savedMessage = isInitialMergePublication
          ? "統合FAQを新着として公開しました。"
          : "統合FAQを公開状態で保存しました。";
        if (window.confirm(
          `${savedMessage}\n\n次の元FAQを「統合済み」にしますか？\n${sourceTitles}\n\n元FAQの内容や公開状態は変えず、通常の検索結果から除外します。`,
        )) {
          try {
            const result = await knowledgeApi.markCodexMergeSources(saved.id);
            allowNavigationRef.current = true;
            navigate(`/articles/${saved.id}`, {
              state: { notice: `${result.markedCount}件の元FAQを「統合済み」にしました。` },
            });
            return;
          } catch (caught) {
            const mergeError = toAppError(caught);
            setError({
              ...mergeError,
              message: `統合FAQは公開しましたが、${mergeError.message}`,
            });
            return;
          }
        }
        allowNavigationRef.current = true;
        navigate(`/articles/${saved.id}`, {
          state: { notice: `${savedMessage} 元FAQは統合済みにしていません。` },
        });
        return;
      }
      allowNavigationRef.current = true;
      navigate(`/articles/${saved.id}`);
    } catch (caught) {
      setError(toAppError(caught));
    } finally {
      setSaving(false);
    }
  };

  if (loading) return <div className="page"><LoadingState label="編集画面を準備しています…" /></div>;
  if (error && !title && categories.length === 0) return <div className="page"><ErrorState error={error} /></div>;

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

  const cancelTo = articleId ? `/articles/${articleId}` : "/search";

  return (
    <div className="page editor-page faq-editor-page">
      <header className="editor-heading">
        <div>
          <Link to={cancelTo} className="editor-back-link" aria-label="編集をキャンセルして戻る">
            <span aria-hidden="true">←</span> 戻る
          </Link>
          <div className="editor-title-line">
            <span className="editor-mode-badge">{isEditing ? "編集中" : "新規作成"}</span>
            <h1>{isEditing ? "FAQを編集" : "新しいFAQを作成"}</h1>
          </div>
          <p>質問と回答を入力し、公開方法を選んで保存します。下書きなら途中の状態でも保存できます。</p>
          <small className="editor-security-note">安全のために、パスワードや秘密鍵、個人情報はFAQへ登録しないでください。</small>
        </div>
        <div className="editor-shortcut" aria-label="キーボードショートカット">
          <kbd>Ctrl</kbd><span>＋</span><kbd>S</kbd><small>で保存</small>
        </div>
      </header>

      <nav className="editor-steps" aria-label="FAQ作成の入力項目">
        <button type="button" className={basicSectionComplete ? "complete" : ""} onClick={() => document.getElementById("editor-basic")?.scrollIntoView({ behavior: "smooth", block: "start" })}>
          <span className="step-mark">{basicSectionComplete ? "✓" : "1"}</span>
          <span><strong>基本情報</strong><small>質問・分類・概要</small></span>
        </button>
        <button type="button" className={hasAnswer ? "complete" : ""} onClick={() => document.getElementById("editor-answer")?.scrollIntoView({ behavior: "smooth", block: "start" })}>
          <span className="step-mark">{hasAnswer ? "✓" : "2"}</span>
          <span><strong>回答</strong><small>手順・画像・外部資料</small></span>
        </button>
        <button type="button" className="complete" onClick={() => document.getElementById("editor-tags")?.scrollIntoView({ behavior: "smooth", block: "start" })}>
          <span className="step-mark">3</span>
          <span><strong>タグ</strong><small>タグマスターから選択</small></span>
        </button>
        <button type="button" className={displaySectionComplete ? "complete" : ""} onClick={() => document.getElementById("editor-display")?.scrollIntoView({ behavior: "smooth", block: "start" })}>
          <span className="step-mark">{displaySectionComplete ? "✓" : "4"}</span>
          <span><strong>公開・表示設定</strong><small>{selectedStatus.label}として保存</small></span>
        </button>
      </nav>

      {(error || validation.length > 0) && (
        <div ref={saveFeedbackRef} className="save-feedback editor-save-feedback" tabIndex={-1}>
          {error && <ErrorState error={error} />}
          {validation.length > 0 && (
            <div className="validation-summary" role="alert">
              <strong>保存する前に、次の項目を確認してください</strong>
              <ul>{validation.map((problem) => <li key={problem}>{problem}</li>)}</ul>
            </div>
          )}
        </div>
      )}

      {mergeContext && (
        <section className="panel merge-publication-guide" aria-label="統合FAQの公開案内">
          <strong>この下書きはCodexで統合したFAQです</strong>
          <p>
            {mergeContext.allSourcesMerged
              ? "元FAQは統合済みです。"
              : "初めて公開する際は新着の表示終了日が必要です。公開後に、元FAQを「統合済み」にするか確認します。"}
          </p>
          <ul>
            {mergeContext.sourceArticles.map((source) => (
              <li key={source.articleId}>
                {source.title}
                {!source.isCurrent && !source.isMerged && <span>（承認後に変更または削除されています）</span>}
              </li>
            ))}
          </ul>
        </section>
      )}

      <form ref={formRef} onSubmit={submit} className="editor-form" noValidate>
        <section id="editor-basic" className="editor-section-card" aria-labelledby="editor-basic-title">
          <div className="editor-section-heading">
            <span className="section-number">1</span>
            <div>
              <h2 id="editor-basic-title">基本情報</h2>
              <p>検索結果で最初に目に入る内容です。質問を短く具体的に書くと見つけやすくなります。</p>
            </div>
          </div>
          <div className="editor-section-body basic-information-grid">
            <div className={`editor-field basic-info-card required-basic-field full-width ${!title.trim() ? "unfilled" : ""} ${validation.includes(VALIDATION_MESSAGES.title) ? "invalid" : ""}`}>
              <div className="field-label-row">
                <label htmlFor="faq-title">タイトル（質問文） <b className="required">必須</b></label>
                <span className="character-count">{title.length} / 200</span>
              </div>
              <input
                id="faq-title"
                value={title}
                onChange={(event) => {
                  setTitle(event.target.value);
                  clearValidation(VALIDATION_MESSAGES.title);
                }}
                maxLength={200}
                aria-invalid={validation.includes(VALIDATION_MESSAGES.title)}
                aria-describedby={validation.includes(VALIDATION_MESSAGES.title) ? "faq-title-error" : "faq-title-help"}
                placeholder="例：Windowsの画面が真っ暗になったときは？"
              />
              {validation.includes(VALIDATION_MESSAGES.title)
                ? <small id="faq-title-error" className="field-error-message">{VALIDATION_MESSAGES.title}</small>
                : <small id="faq-title-help" className="field-help">利用者が実際に検索しそうな質問文を入力します。</small>}
            </div>

            <div className={`editor-field basic-info-card required-basic-field ${!categoryId ? "unfilled" : ""} ${validation.includes(VALIDATION_MESSAGES.category) ? "invalid" : ""}`}>
              <label htmlFor="faq-category">所属分類 <b className="required">必須</b></label>
              <select
                id="faq-category"
                value={categoryId}
                onChange={(event) => {
                  setCategoryId(event.target.value);
                  clearValidation(VALIDATION_MESSAGES.category);
                }}
                aria-invalid={validation.includes(VALIDATION_MESSAGES.category)}
                aria-describedby="faq-category-help"
              >
                <option value="">選択してください</option>
                {categories.map((category) => (
                  <option key={category.id} value={category.id}>
                    {"　".repeat(Math.max(0, category.depth - 1))}{category.name}
                  </option>
                ))}
              </select>
              <small id="faq-category-help" className={validation.includes(VALIDATION_MESSAGES.category) ? "field-error-message" : "field-help"}>
                {validation.includes(VALIDATION_MESSAGES.category) ? VALIDATION_MESSAGES.category : "FAQを最も探しやすい分類を1つ選びます。"}
              </small>
            </div>

            <div className="editor-field basic-info-card optional-basic-field">
              <label htmlFor="faq-importance">重要度 <span className="optional-field-badge">任意</span></label>
              <select id="faq-importance" value={importance} onChange={(event) => setImportance(Number(event.target.value))}>
                <option value={1}>1 — 通常</option>
                <option value={2}>2 — 重要</option>
                <option value={3}>3 — 最重要</option>
              </select>
              <small className="field-help">検索結果の並び順が同点のときに使用します。</small>
            </div>

            <div className={`editor-field basic-info-card conditional-basic-field full-width ${status === "published" ? "required-basic-field" : ""} ${status === "published" && !summary.trim() ? "unfilled" : ""} ${validation.includes(VALIDATION_MESSAGES.summary) ? "invalid" : ""}`}>
              <div className="field-label-row">
                <label htmlFor="faq-summary">
                  概要（検索結果に表示する短い答え）
                  <b className={status === "published" ? "required" : "required conditional-required"}>公開時必須</b>
                </label>
                <span className="character-count">{summary.length} / 500</span>
              </div>
              <textarea
                id="faq-summary"
                value={summary}
                onChange={(event) => {
                  setSummary(event.target.value);
                  clearValidation(VALIDATION_MESSAGES.summary);
                }}
                maxLength={500}
                rows={3}
                aria-invalid={validation.includes(VALIDATION_MESSAGES.summary)}
                aria-describedby="faq-summary-help"
                placeholder="例：ディスプレイの接続と表示先を順番に確認すると解決できます。"
              />
              <small id="faq-summary-help" className={validation.includes(VALIDATION_MESSAGES.summary) ? "field-error-message" : "field-help"}>
                {validation.includes(VALIDATION_MESSAGES.summary) ? VALIDATION_MESSAGES.summary : "結論を原則1文で入力します。詳しい操作は回答欄に記載します。"}
              </small>
            </div>
          </div>
        </section>

        <section id="editor-answer" className="editor-section-card" aria-labelledby="editor-answer-title">
          <div className="editor-section-heading">
            <span className="section-number">2</span>
            <div>
              <h2 id="editor-answer-title">回答</h2>
              <p>最初に結論を簡潔に示し、その後に手順や詳細を記載すると、読み手に伝わりやすくなります。</p>
            </div>
          </div>
          <div className="editor-section-body">
            <div className="answer-field-heading">
              <label>回答内容 {status === "published" && <b className="required">公開時必須</b>}</label>
              <span>{hasAnswer ? "入力済み" : "未入力"}</span>
            </div>
            <div className={validation.includes(VALIDATION_MESSAGES.body) ? "answer-editor-frame invalid" : "answer-editor-frame"}>
              <RichTextEditor
                value={bodyDoc}
                onChange={(document) => {
                  setBodyDoc(document);
                  clearValidation(VALIDATION_MESSAGES.body);
                }}
                imageSources={imageSources}
                onRequestImage={requestImage}
                onDiscardImage={discardImage}
                disabled={saving}
              />
            </div>
            {validation.includes(VALIDATION_MESSAGES.body) && <small className="field-error-message answer-error">{VALIDATION_MESSAGES.body}</small>}
          </div>
        </section>

        <section id="editor-tags" className="editor-section-card" aria-labelledby="editor-tags-title">
          <div className="editor-section-heading">
            <span className="section-number">3</span>
            <div>
              <h2 id="editor-tags-title">タグ</h2>
              <p>タグマスターから選択し、分類とは別の観点でFAQを関連付けます。複数選択できます。</p>
            </div>
          </div>
          <div className="editor-section-body tag-selector">
            <label htmlFor="faq-tag-select">タグマスターから選択</label>
            <div className="tag-selector-entry">
              <select id="faq-tag-select" value={tagToAdd} onChange={(event) => setTagToAdd(event.target.value)}>
                <option value="">タグを選択してください</option>
                {availableTags.map((tag) => <option key={tag.id} value={tag.name}>{tag.name}</option>)}
              </select>
              <button
                type="button"
                className="button secondary"
                disabled={!tagToAdd}
                onClick={() => {
                  if (!tagToAdd || tags.includes(tagToAdd)) return;
                  setTags((current) => [...current, tagToAdd]);
                  setTagToAdd("");
                }}
              >
                タグを追加
              </button>
            </div>
            {tags.length > 0 ? (
              <ul className="selected-tags" aria-label="選択中のタグ">
                {tags.map((tag) => (
                  <li key={tag}><span>{tag}</span><button type="button" aria-label={`${tag}を外す`} onClick={() => setTags((current) => current.filter((item) => item !== tag))}>外す</button></li>
                ))}
              </ul>
            ) : (
              <p className="tag-selector-empty">タグは未選択です。必要な場合だけ追加してください。</p>
            )}
            <div className="tag-master-link-row">
              <small>選択肢にないタグは、先にタグマスターへ登録します。</small>
              <Link to="/tags" className="button secondary compact">タグマスターを管理</Link>
            </div>
          </div>
        </section>

        <section id="editor-display" className="editor-section-card" aria-labelledby="editor-display-title">
          <div className="editor-section-heading">
            <span className="section-number">4</span>
            <div>
              <h2 id="editor-display-title">公開・表示設定</h2>
              <p>保存後に誰が見つけられるかと、検索結果に付ける目印を設定します。</p>
            </div>
          </div>
          <div className="editor-section-body display-section-body">
            <fieldset className="status-fieldset">
              <legend>保存後の状態</legend>
              <div className="status-options">
                <label className={status === "draft" ? "status-option selected" : "status-option"}>
                  <input type="radio" name="article-status" value="draft" checked={status === "draft"} onChange={() => { setStatus("draft"); clearValidation(VALIDATION_MESSAGES.summary, VALIDATION_MESSAGES.body); }} />
                  <span className="status-option-mark draft" aria-hidden="true">●</span>
                  <span><strong>下書き</strong><small>内容を整えてから公開する</small></span>
                </label>
                <label className={status === "published" ? "status-option selected" : "status-option"}>
                  <input
                    type="radio"
                    name="article-status"
                    value="published"
                    checked={status === "published"}
                    onChange={() => {
                      setStatus("published");
                      if (mergeContext && initialStatus !== "published") {
                        setNewBadgeEnabled(true);
                        setNewBadgeUntil((current) => current || getDefaultBadgeUntil());
                        clearValidation(VALIDATION_MESSAGES.mergeNewBadge, VALIDATION_MESSAGES.newBadge);
                      }
                    }}
                  />
                  <span className="status-option-mark published" aria-hidden="true">●</span>
                  <span><strong>公開</strong><small>通常の検索結果に表示する</small></span>
                </label>
                {isEditing && (
                  <label className={status === "archived" ? "status-option selected" : "status-option"}>
                    <input type="radio" name="article-status" value="archived" checked={status === "archived"} onChange={() => { setStatus("archived"); clearValidation(VALIDATION_MESSAGES.summary, VALIDATION_MESSAGES.body); }} />
                    <span className="status-option-mark archived" aria-hidden="true">●</span>
                    <span><strong>廃止</strong><small>検索結果から除外して保持する</small></span>
                  </label>
                )}
              </div>
            </fieldset>

            <div className="display-divider" />
            <div className="display-setting-heading">
              <h3>検索結果の表示</h3>
              <p>必要なものだけ設定できます。新着と更新は同時に表示できます。</p>
            </div>
            <div className="display-settings-grid">
              <div className={newBadgeEnabled ? "display-setting-card enabled" : "display-setting-card"}>
                <label className="check-option">
                  <input
                    type="checkbox"
                    aria-label="「新着」を表示する"
                    checked={newBadgeEnabled}
                    onChange={(event) => {
                      const enabled = event.target.checked;
                      setNewBadgeEnabled(enabled);
                      if (enabled) setNewBadgeUntil((current) => current || getDefaultBadgeUntil());
                      else clearValidation(VALIDATION_MESSAGES.newBadge, VALIDATION_MESSAGES.mergeNewBadge);
                    }}
                  />
                  <span><strong>「新着」を表示</strong><small>新しく追加したFAQであることを伝えます。</small></span>
                </label>
                <label className="date-option" htmlFor="new-badge-until">
                  <span>表示終了日 {newBadgeEnabled && <b className="required">必須</b>}</span>
                  <input
                    id="new-badge-until"
                    type="date"
                    value={newBadgeUntil}
                    min={isInitialMergePublication ? formatLocalDate(new Date()) : undefined}
                    disabled={!newBadgeEnabled}
                    aria-required={newBadgeEnabled}
                    aria-invalid={validation.includes(VALIDATION_MESSAGES.newBadge) || validation.includes(VALIDATION_MESSAGES.mergeNewBadge)}
                    onChange={(event) => {
                      setNewBadgeUntil(event.target.value);
                      clearValidation(VALIDATION_MESSAGES.newBadge, VALIDATION_MESSAGES.mergeNewBadge);
                    }}
                  />
                </label>
                {validation.includes(VALIDATION_MESSAGES.newBadge) && <small className="field-error-message">{VALIDATION_MESSAGES.newBadge}</small>}
                {validation.includes(VALIDATION_MESSAGES.mergeNewBadge) && <small className="field-error-message">{VALIDATION_MESSAGES.mergeNewBadge}</small>}
              </div>
              <div className={updatedBadgeEnabled ? "display-setting-card enabled" : "display-setting-card"}>
                <label className="check-option">
                  <input
                    type="checkbox"
                    aria-label="「更新」を表示する"
                    checked={updatedBadgeEnabled}
                    onChange={(event) => {
                      const enabled = event.target.checked;
                      setUpdatedBadgeEnabled(enabled);
                      if (enabled) setUpdatedBadgeUntil((current) => current || getDefaultBadgeUntil());
                      else clearValidation(VALIDATION_MESSAGES.updatedBadge);
                    }}
                  />
                  <span><strong>「更新」を表示</strong><small>内容を見直したFAQであることを伝えます。</small></span>
                </label>
                <label className="date-option" htmlFor="updated-badge-until">
                  <span>表示終了日 {updatedBadgeEnabled && <b className="required">必須</b>}</span>
                  <input
                    id="updated-badge-until"
                    type="date"
                    value={updatedBadgeUntil}
                    disabled={!updatedBadgeEnabled}
                    aria-required={updatedBadgeEnabled}
                    aria-invalid={validation.includes(VALIDATION_MESSAGES.updatedBadge)}
                    onChange={(event) => {
                      setUpdatedBadgeUntil(event.target.value);
                      clearValidation(VALIDATION_MESSAGES.updatedBadge);
                    }}
                  />
                </label>
                {validation.includes(VALIDATION_MESSAGES.updatedBadge) && <small className="field-error-message">{VALIDATION_MESSAGES.updatedBadge}</small>}
              </div>
              <div className={isHidden ? "display-setting-card hidden-setting enabled" : "display-setting-card hidden-setting"}>
                <label className="check-option">
                  <input type="checkbox" aria-label="通常検索では非表示にする" checked={isHidden} onChange={(event) => setIsHidden(event.target.checked)} />
                  <span><strong>通常検索では非表示にする</strong><small>公開状態でも検索結果には出さず、FAQ管理画面からだけ確認・編集できます。</small></span>
                </label>
              </div>
            </div>
          </div>
        </section>

        <section className="publish-bar editor-save-bar" aria-label="FAQの保存操作">
          <div className="editor-save-status">
            <span className={currentStateReady ? "save-readiness ready" : "save-readiness"} aria-hidden="true">{currentStateReady ? "✓" : "!"}</span>
            <span>
              <strong>{currentStateReady ? `${selectedStatus.label}として保存できます` : canSave ? "保存前に未入力の項目があります" : `${missingBasicFields.join("と")}を入力してください`}</strong>
              <small>{currentStateReady ? selectedStatus.description : canSave ? "保存すると不足している項目を確認できます" : "入力後に保存ボタンが有効になります"}</small>
            </span>
          </div>
          <div className="editor-save-actions">
            <Link to={cancelTo} className="button ghost">キャンセル</Link>
            <button type="submit" className="button primary large" disabled={!canSave || saving}>
              <span aria-hidden="true">{saving ? "" : "✓"}</span>
              {saving ? "保存しています…" : status === "draft" ? "下書きを保存" : "FAQを保存"}
            </button>
          </div>
        </section>
      </form>
    </div>
  );
}
