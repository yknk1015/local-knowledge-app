import type { AppError } from "../types/domain";

export function LoadingState({ label = "読み込んでいます…" }: { label?: string }) {
  return (
    <div className="state-panel" role="status">
      <span className="spinner" aria-hidden="true" />
      <p>{label}</p>
    </div>
  );
}

export function ErrorState({ error, onRetry }: { error: AppError; onRetry?: () => void }) {
  return (
    <div className="error-panel" role="alert">
      <div className="error-code">{error.code}</div>
      <div>
        <strong>{error.message}</strong>
        <p>{error.action}</p>
        {onRetry && (
          <button type="button" className="button secondary" onClick={onRetry}>
            もう一度試す
          </button>
        )}
      </div>
    </div>
  );
}

export function EmptyState({
  title,
  description,
  action,
}: {
  title: string;
  description: string;
  action?: React.ReactNode;
}) {
  return (
    <div className="empty-state">
      <div className="empty-illustration" aria-hidden="true">◇</div>
      <h2>{title}</h2>
      <p>{description}</p>
      {action}
    </div>
  );
}
