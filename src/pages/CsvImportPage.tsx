import { useState } from "react";
import { Link } from "react-router-dom";
import { knowledgeApi, toAppError } from "../api/knowledgeApi";
import { selectFaqCsvImportPath } from "../api/transferDialogs";
import { ErrorState, LoadingState } from "../components/Feedback";
import type { AppError, CsvImportPreview, CsvImportResult } from "../types/domain";

const ACTION_LABELS = { create: "新規", update: "更新", unchanged: "変更なし", error: "エラー" } as const;

export function CsvImportPage() {
  const [preview, setPreview] = useState<CsvImportPreview | null>(null);
  const [result, setResult] = useState<CsvImportResult | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<AppError | null>(null);

  const choose = async () => {
    const selected = await selectFaqCsvImportPath();
    if (!selected) return;
    setLoading(true); setError(null); setPreview(null); setResult(null);
    try { setPreview(await knowledgeApi.inspectFaqCsv(selected)); }
    catch (caught) { setError(toAppError(caught)); }
    finally { setLoading(false); }
  };

  const apply = async () => {
    if (!preview || preview.errorCount > 0) return;
    const details = [
      `新規 ${preview.createCount}件、更新 ${preview.updateCount}件、変更なし ${preview.unchangedCount}件`,
      preview.bodyReplacementCount > 0 ? `回答本文のプレーンテキスト置換 ${preview.bodyReplacementCount}件` : null,
      preview.staleOverwriteCount > 0 ? `書き出し後に更新されたFAQの上書き ${preview.staleOverwriteCount}件` : null,
    ].filter(Boolean).join("\n");
    if (!window.confirm(`次の内容でCSVを取り込みますか？\n${details}\n\n取込直前の自動バックアップを作成してから、一括で反映します。`)) return;
    setLoading(true); setError(null);
    try { setResult(await knowledgeApi.importFaqCsv(preview.sourcePath, preview.fileSha256)); }
    catch (caught) { setError(toAppError(caught)); }
    finally { setLoading(false); }
  };

  return (
    <div className="page csv-import-page">
      <div className="page-heading split">
        <div>
          <span className="eyebrow">Excelで一括編集</span>
          <h1>CSVインポート</h1>
          <p>KnowledgeAppからエクスポートした「.knowledge-faq.csv」を検査し、確認後に一括反映します。</p>
        </div>
        <Link to="/manage" className="button secondary">FAQ管理へ戻る</Link>
      </div>
      <section className="panel csv-guidance">
        <h2>CSV編集のポイント</h2>
        <p>FAQ管理IDは既存行では変更せず、新規FAQを追加する行だけ空欄にしてください。分類は分類パスを編集して変更できます。</p>
        <p>回答本文を変更していない行は、現在の表・画像・リンク・書式をそのまま保持します。回答本文を変更した行は、Excelで編集した文字列を段落として置き換えるため、表・画像・書式は本文から外れます。</p>
        <p>更新日時が古い行もCSVの内容で上書きします。取込前に必ずプレビューを確認してください。</p>
        <button type="button" className="button primary" disabled={loading} onClick={() => void choose()}>CSVファイルを選択</button>
      </section>
      {loading && <LoadingState label="CSVを処理しています…" />}
      {error && <ErrorState error={error} onRetry={() => void choose()} />}
      {result && (
        <section className="success-notice csv-result" role="status">
          <strong>CSVを取り込みました。</strong>
          <p>新規 {result.createdCount}件、更新 {result.updatedCount}件、変更なし {result.unchangedCount}件</p>
          <small>取込前バックアップ: {result.safetyBackupPath}</small>
        </section>
      )}
      {preview && !result && (
        <>
          <section className={`panel csv-preview-summary${preview.errorCount > 0 ? " has-errors" : ""}`}>
            <h2>取込プレビュー</h2>
            <dl>
              <div><dt>全行</dt><dd>{preview.totalRows}件</dd></div>
              <div><dt>新規</dt><dd>{preview.createCount}件</dd></div>
              <div><dt>更新</dt><dd>{preview.updateCount}件</dd></div>
              <div><dt>変更なし</dt><dd>{preview.unchangedCount}件</dd></div>
              <div><dt>本文置換</dt><dd>{preview.bodyReplacementCount}件</dd></div>
              <div><dt>旧版上書き</dt><dd>{preview.staleOverwriteCount}件</dd></div>
              <div><dt>エラー</dt><dd>{preview.errorCount}件</dd></div>
            </dl>
            {preview.errorCount > 0
              ? <p className="field-error">エラー行を修正してから、CSVを選択し直してください。</p>
              : <button type="button" className="button primary" disabled={loading} onClick={() => void apply()}>この内容で取り込む</button>}
          </section>
          <div className="panel management-table-wrap">
            <table className="management-table csv-preview-table">
              <thead><tr><th>行</th><th>処理</th><th>FAQ</th><th>確認事項</th></tr></thead>
              <tbody>{preview.rows.map((row) => (
                <tr key={`${row.line}-${row.faqManagementId ?? "new"}`} className={row.action === "error" ? "csv-error-row" : ""}>
                  <td>{row.line}</td>
                  <td><span className={`status-badge csv-${row.action}`}>{ACTION_LABELS[row.action]}</span></td>
                  <td>{row.title || "（読込不可）"}<small className="table-subline">{row.faqManagementId ?? "新規FAQ"}</small></td>
                  <td>{row.messages.length > 0 ? <ul>{row.messages.map((message) => <li key={message}>{message}</li>)}</ul> : "－"}</td>
                </tr>
              ))}</tbody>
            </table>
          </div>
        </>
      )}
    </div>
  );
}
