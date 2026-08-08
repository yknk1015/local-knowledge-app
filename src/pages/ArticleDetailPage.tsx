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

  return (
    <article className="page article-detail">
      <div className="detail-actions">
        <Link to="/search" className="text-link">← 一覧へ戻る</Link>
        <Link to={`/articles/${article.id}/edit`} className="button secondary">編集する</Link>
      </div>
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
        <RichTextViewer value={article.bodyDoc} />
      </section>
      <aside className="detail-safety-note">
        <strong>作業前にご確認ください</strong>
        <p>会社のPCやネットワークを変更する場合は、所属先の運用ルールを優先してください。</p>
      </aside>
    </article>
  );
}
