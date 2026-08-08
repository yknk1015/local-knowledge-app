import type { ArticleStatus } from "../types/domain";

const labels: Record<ArticleStatus, string> = {
  draft: "下書き",
  published: "公開",
  archived: "廃止",
};

export function StatusBadge({ status }: { status: ArticleStatus }) {
  return <span className={`status-badge ${status}`}>{labels[status]}</span>;
}
