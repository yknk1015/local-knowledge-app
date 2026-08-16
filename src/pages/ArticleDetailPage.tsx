import { useCallback, useEffect, useState } from "react";
import { Link, useLocation, useNavigate, useParams } from "react-router-dom";
import { knowledgeApi, toAppError } from "../api/knowledgeApi";
import { ErrorState, LoadingState } from "../components/Feedback";
import { RichTextViewer } from "../components/RichTextEditor";
import { StatusBadge } from "../components/StatusBadge";
import { ArticleDisplayBadges } from "../components/ArticleDisplayBadges";
import type { AppError, Article, CodexDelegationResult } from "../types/domain";

export function ArticleDetailPage() {
  const { articleId = "" } = useParams();
  const location = useLocation();
  const navigate = useNavigate();
  const locationState = location.state as { returnTo?: unknown; notice?: unknown } | null;
  const returnTo = typeof locationState?.returnTo === "string"
    && /^\/search(?:\?|$)/.test(locationState.returnTo)
    ? locationState.returnTo
    : "/search";
  const initialNotice = typeof locationState?.notice === "string" ? locationState.notice : null;
  const [article, setArticle] = useState<Article | null>(null);
  const [error, setError] = useState<AppError | null>(null);
  const [loading, setLoading] = useState(true);
  const [actionBusy, setActionBusy] = useState(false);
  const [delegation, setDelegation] = useState<CodexDelegationResult | null>(null);
  const [notice, setNotice] = useState<string | null>(initialNotice);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      setArticle(await knowledgeApi.getArticle(articleId));
    } catch (caught) {
      setError(toAppError(caught));
    } finally {
      setLoading(false);
    }
  }, [articleId]);

  useEffect(() => { void load(); }, [load]);

  if (loading) return <div className="page"><LoadingState label="FAQを開いています…" /></div>;
  if (error) return <div className="page"><ErrorState error={error} onRetry={() => void load()} /></div>;
  if (!article) return null;

  const deleteArticle = async () => {
    if (!window.confirm(`「${article.title}」を削除済みに移動しますか？\n通常の検索には表示されなくなりますが、あとから復元できます。`)) return;
    setActionBusy(true);
    setError(null);
    try {
      setArticle(await knowledgeApi.deleteArticle(article.id));
    } catch (caught) {
      setError(toAppError(caught));
    } finally {
      setActionBusy(false);
    }
  };

  const restoreArticle = async () => {
    if (!window.confirm(`「${article.title}」を元の分類と状態で復元しますか？`)) return;
    setActionBusy(true);
    setError(null);
    try {
      setArticle(await knowledgeApi.restoreArticle(article.id));
    } catch (caught) {
      setError(toAppError(caught));
    } finally {
      setActionBusy(false);
    }
  };

  const duplicateArticle = async () => {
    setActionBusy(true);
    setError(null);
    try {
      const copy = await knowledgeApi.duplicateArticle(article.id);
      navigate(`/articles/${copy.id}/edit`);
    } catch (caught) {
      setError(toAppError(caught));
      setActionBusy(false);
    }
  };

  const delegateRevision = async () => {
    if (!window.confirm(`「${article.title}」の本文をCodexへ渡す委譲ファイルを作成しますか？\nCodexは提案だけを作成し、承認するまでFAQを変更しません。`)) return;
    setActionBusy(true);
    setError(null);
    setDelegation(null);
    try {
      setDelegation(await knowledgeApi.createCodexDelegation("revise", [article.id]));
    } catch (caught) {
      setError(toAppError(caught));
    } finally {
      setActionBusy(false);
    }
  };

  const clearMerge = async () => {
    if (!article.mergeInfo) return;
    if (!window.confirm(`「${article.title}」の統合済み設定を解除しますか？\n統合による検索除外だけを解除します。表示されるかどうかは元の公開状態と非表示設定に従います。`)) return;
    setActionBusy(true);
    setError(null);
    try {
      setArticle(await knowledgeApi.clearArticleMerge(article.id));
      setNotice("統合済み設定を解除しました。");
    } catch (caught) {
      setError(toAppError(caught));
    } finally {
      setActionBusy(false);
    }
  };

  if (article.deletedAt) {
    return (
      <article className="page article-detail">
        <div className="detail-actions">
          <Link to="/manage" className="text-link">← FAQの管理へ戻る</Link>
        </div>
        {error && <ErrorState error={error} />}
        {notice && <div className="success-notice detail-notice" role="status">{notice}</div>}
        <section className="panel deleted-article-panel">
          <span className="deleted-symbol" aria-hidden="true">↶</span>
          <span className="eyebrow">削除済みFAQ</span>
          <h1>{article.title}</h1>
          <p>このFAQは通常の検索には表示されません。内容と添付画像は保持されており、元の分類と状態へ復元できます。</p>
          {article.mergeInfo && (
            <p>
              統合先：<Link to={`/articles/${article.mergeInfo.targetArticleId}`}>{article.mergeInfo.targetArticleTitle}</Link>
            </p>
          )}
          <dl>
            <div><dt>分類</dt><dd>{article.categoryName}</dd></div>
            <div><dt>削除日時</dt><dd>{new Date(article.deletedAt).toLocaleString("ja-JP")}</dd></div>
          </dl>
          <div className="deleted-article-actions">
            <button type="button" className="button primary large" disabled={actionBusy} onClick={() => void restoreArticle()}>
              {actionBusy ? "復元しています…" : "このFAQを復元"}
            </button>
            {article.mergeInfo && (
              <button type="button" className="button secondary large" disabled={actionBusy} onClick={() => void clearMerge()}>
                統合を解除
              </button>
            )}
          </div>
        </section>
      </article>
    );
  }

  return (
    <article className="page article-detail">
      <div className="detail-actions">
        <Link to={returnTo} className="text-link">← 一覧へ戻る</Link>
        <div className="detail-action-buttons">
          {!article.mergeInfo && (
            <button type="button" className="button secondary" disabled={actionBusy} onClick={() => void delegateRevision()}>
              Codexに推敲・修正を依頼
            </button>
          )}
          {article.mergeInfo && (
            <button type="button" className="button secondary" disabled={actionBusy} onClick={() => void clearMerge()}>
              統合を解除
            </button>
          )}
          <button type="button" className="button secondary" disabled={actionBusy} onClick={() => void duplicateArticle()}>
            {actionBusy ? "処理中…" : "複製して下書きを作る"}
          </button>
          <Link to={`/articles/${article.id}/edit`} className="button secondary">編集する</Link>
          <button type="button" className="button danger-outline" disabled={actionBusy} onClick={() => void deleteArticle()}>
            {actionBusy ? "処理中…" : "削除"}
          </button>
        </div>
      </div>
      {error && <ErrorState error={error} />}
      {notice && <div className="success-notice detail-notice" role="status">{notice}</div>}
      {delegation && (
        <section className="success-notice codex-delegation-notice" role="status">
          <strong>Codexへの委譲準備ができました。</strong>
          <p>Codexの新しいタスクへ、次の文章をそのまま送ってください。</p>
          <code>{delegation.prompt}</code>
          <small>委譲番号：{delegation.delegationId}</small>
        </section>
      )}
      {article.mergeInfo && (
        <section className="panel merged-article-notice" aria-label="統合済みFAQ">
          <span className="status-badge merged">統合済み</span>
          <p>
            このFAQは「<Link to={`/articles/${article.mergeInfo.targetArticleId}`}>{article.mergeInfo.targetArticleTitle}</Link>」へ統合されています。
            通常の検索結果には表示されません。
          </p>
        </section>
      )}
      <header className="detail-header">
        <div className="card-meta">
          <span className="category-pill">{article.categoryName}</span>
          {article.mergeInfo
            ? <span className="status-badge merged">統合済み</span>
            : <StatusBadge status={article.status} />}
          <ArticleDisplayBadges
            newBadgeUntil={article.newBadgeUntil}
            updatedBadgeUntil={article.updatedBadgeUntil}
            isHidden={article.isHidden}
            showHidden
          />
          <span className="importance">重要度 {article.importance}</span>
        </div>
        <h1>{article.title}</h1>
        {article.summary && <p className="detail-summary">{article.summary}</p>}
        <div className="detail-dates">
          <span>作成：{new Date(article.createdAt).toLocaleString("ja-JP")}</span>
          <span>更新：{new Date(article.updatedAt).toLocaleString("ja-JP")}</span>
        </div>
      </header>
      <section className="answer-section">
        <h2>回答</h2>
        <RichTextViewer
          value={article.bodyDoc}
          imageSources={article.attachments.map((attachment) => ({
            id: attachment.id,
            assetPath: attachment.assetPath,
            altText: attachment.altText,
          }))}
        />
      </section>
      <aside className="detail-safety-note">
        <strong>作業前にご確認ください</strong>
        <p>会社のPCやネットワークを変更する場合は、所属先の運用ルールを優先してください。</p>
      </aside>
    </article>
  );
}
