import { useCallback, useEffect, useState } from "react";
import { Link, useParams } from "react-router-dom";
import { knowledgeApi, toAppError } from "../api/knowledgeApi";
import { ErrorState, LoadingState } from "../components/Feedback";
import { RichTextViewer } from "../components/RichTextEditor";
import { StatusBadge } from "../components/StatusBadge";
import type { AppError, Article } from "../types/domain";

export function ArticleDetailPage() {
  const { articleId = "" } = useParams();
  const [article, setArticle] = useState<Article | null>(null);
  const [error, setError] = useState<AppError | null>(null);
  const [loading, setLoading] = useState(true);
  const [actionBusy, setActionBusy] = useState(false);

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

  if (article.deletedAt) {
    return (
      <article className="page article-detail">
        <div className="detail-actions">
          <Link to="/manage" className="text-link">← FAQの管理へ戻る</Link>
        </div>
        {error && <ErrorState error={error} />}
        <section className="panel deleted-article-panel">
          <span className="deleted-symbol" aria-hidden="true">↶</span>
          <span className="eyebrow">削除済みFAQ</span>
          <h1>{article.title}</h1>
          <p>このFAQは通常の検索には表示されません。内容と添付画像は保持されており、元の分類と状態へ復元できます。</p>
          <dl>
            <div><dt>分類</dt><dd>{article.categoryName}</dd></div>
            <div><dt>削除日時</dt><dd>{new Date(article.deletedAt).toLocaleString("ja-JP")}</dd></div>
          </dl>
          <button type="button" className="button primary large" disabled={actionBusy} onClick={() => void restoreArticle()}>
            {actionBusy ? "復元しています…" : "このFAQを復元"}
          </button>
        </section>
      </article>
    );
  }

  return (
    <article className="page article-detail">
      <div className="detail-actions">
        <Link to="/search" className="text-link">← 一覧へ戻る</Link>
        <div className="detail-action-buttons">
          <Link to={`/articles/${article.id}/edit`} className="button secondary">編集する</Link>
          <button type="button" className="button danger-outline" disabled={actionBusy} onClick={() => void deleteArticle()}>
            {actionBusy ? "処理中…" : "削除"}
          </button>
        </div>
      </div>
      {error && <ErrorState error={error} />}
      <header className="detail-header">
        <div className="card-meta">
          <span className="category-pill">{article.categoryName}</span>
          <StatusBadge status={article.status} />
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
