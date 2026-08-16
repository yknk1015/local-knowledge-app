import { open, save } from "@tauri-apps/plugin-dialog";
import { useState } from "react";
import { Link } from "react-router-dom";
import { knowledgeApi, toAppError } from "../api/knowledgeApi";
import { ErrorState, LoadingState } from "../components/Feedback";
import type {
  AppError,
  JsonEntityCounts,
  JsonExportResult,
  JsonImportPreview,
  JsonImportResult,
} from "../types/domain";
import "./JsonTransferPage.css";

const COUNT_LABELS: Array<[keyof JsonEntityCounts, string]> = [
  ["categories", "分類"],
  ["articles", "FAQ"],
  ["tags", "タグ"],
  ["synonymGroups", "同義語グループ"],
  ["relations", "関連FAQ"],
  ["mergeRelations", "統合関係"],
];

function defaultJsonName() {
  const now = new Date();
  const pad = (value: number) => String(value).padStart(2, "0");
  return `KnowledgeApp_${now.getFullYear()}${pad(now.getMonth() + 1)}${pad(now.getDate())}.knowledge-export.json`;
}

function EntityCounts({ counts }: { counts: JsonEntityCounts }) {
  return <dl className="json-entity-counts">{COUNT_LABELS.map(([key, label]) => (
    <div key={key}><dt>{label}</dt><dd>{counts[key]}件</dd></div>
  ))}</dl>;
}

export function JsonTransferPage() {
  const [preview, setPreview] = useState<JsonImportPreview | null>(null);
  const [exportResult, setExportResult] = useState<JsonExportResult | null>(null);
  const [importResult, setImportResult] = useState<JsonImportResult | null>(null);
  const [busy, setBusy] = useState<"export" | "inspect" | "import" | null>(null);
  const [error, setError] = useState<AppError | null>(null);

  const exportData = async () => {
    setError(null);
    setExportResult(null);
    try {
      const destination = await save({
        title: "KnowledgeApp JSONの保存先を選択",
        defaultPath: defaultJsonName(),
        filters: [{ name: "KnowledgeApp JSON", extensions: ["json"] }],
      });
      if (!destination) return;
      const normalized = destination.toLowerCase().endsWith(".knowledge-export.json")
        ? destination
        : destination.replace(/\.json$/i, "") + ".knowledge-export.json";
      setBusy("export");
      setExportResult(await knowledgeApi.exportJson(normalized));
    } catch (caught) {
      setError(toAppError(caught));
    } finally {
      setBusy(null);
    }
  };

  const chooseImport = async () => {
    setError(null);
    setPreview(null);
    setImportResult(null);
    try {
      const selected = await open({
        title: "取り込むKnowledgeApp JSONを選択",
        multiple: false,
        directory: false,
        filters: [{ name: "KnowledgeApp JSON", extensions: ["json"] }],
      });
      if (!selected) return;
      setBusy("inspect");
      setPreview(await knowledgeApi.inspectJson(selected));
    } catch (caught) {
      setError(toAppError(caught));
    } finally {
      setBusy(null);
    }
  };

  const applyImport = async () => {
    if (!preview || preview.errorCount > 0) return;
    if (!window.confirm(
      `JSONを取り込みます。\n新規 ${preview.createCount}件、更新 ${preview.updateCount}件、変更なし ${preview.unchangedCount}件\n\n取込直前のフルバックアップを作成し、全件をまとめて反映します。続けますか？`,
    )) return;
    setBusy("import");
    setError(null);
    try {
      setImportResult(await knowledgeApi.importJson(preview.sourcePath, preview.fileSha256));
    } catch (caught) {
      setError(toAppError(caught));
    } finally {
      setBusy(null);
    }
  };

  return (
    <div className="page json-transfer-page">
      <div className="page-heading split">
        <div>
          <span className="eyebrow">構造を保った移行・整理</span>
          <h1>JSONエクスポート・インポート</h1>
          <p>分類、FAQ、タグ、同義語、関連情報を形式版付きJSONで受け渡します。</p>
        </div>
        <Link to="/manage" className="button secondary">FAQ管理へ戻る</Link>
      </div>

      <section className="panel json-guidance">
        <h2>このJSONに含まれるもの</h2>
        <p>FAQ本文内のコピー用参照先とHTTP/HTTPS参考URLは維持します。添付画像本体、画像参照、会社管理の外部資料本体、利用者、検索・閲覧履歴は含めません。端末全体の移行にはフルバックアップを使用してください。</p>
        <p>Git管理フォルダ内では入出力できません。インポート時は全データを事前検査し、確認後にフルバックアップを作成してから1回の処理で反映します。</p>
      </section>

      <div className="json-transfer-actions">
        <section className="panel json-action-card">
          <span className="eyebrow">書き出す</span>
          <h2>JSONエクスポート</h2>
          <p>現在のKnowledgeAppデータを「.knowledge-export.json」へ書き出します。</p>
          <button type="button" className="button primary" disabled={busy !== null} onClick={() => void exportData()}>保存先を選んで書き出す</button>
        </section>
        <section className="panel json-action-card">
          <span className="eyebrow">検査して取り込む</span>
          <h2>JSONインポート</h2>
          <p>ファイルを選択すると、変更内容とエラーを反映前に表示します。</p>
          <button type="button" className="button primary" disabled={busy !== null} onClick={() => void chooseImport()}>JSONファイルを選択</button>
        </section>
      </div>

      {busy && <LoadingState label={busy === "export" ? "JSONを書き出しています…" : busy === "import" ? "バックアップ後にJSONを取り込んでいます…" : "JSONを検査しています…"} />}
      {error && <ErrorState error={error} />}
      {exportResult && (
        <section className="success-notice json-result" role="status">
          <strong>JSONを書き出しました。</strong>
          <EntityCounts counts={exportResult.counts} />
          <small>{exportResult.destinationPath}</small>
        </section>
      )}
      {importResult && (
        <section className="success-notice json-result" role="status">
          <strong>JSONを取り込みました。</strong>
          <p>新規 {importResult.createdCount}件、更新 {importResult.updatedCount}件、変更なし {importResult.unchangedCount}件</p>
          <small>取込前バックアップ: {importResult.safetyBackupPath}</small>
        </section>
      )}
      {preview && !importResult && (
        <section className={`panel json-preview${preview.errorCount > 0 ? " has-errors" : ""}`}>
          <h2>取込プレビュー</h2>
          <EntityCounts counts={preview.counts} />
          <dl className="json-action-counts">
            <div><dt>新規</dt><dd>{preview.createCount}件</dd></div>
            <div><dt>更新</dt><dd>{preview.updateCount}件</dd></div>
            <div><dt>変更なし</dt><dd>{preview.unchangedCount}件</dd></div>
            <div><dt>エラー</dt><dd>{preview.errorCount}件</dd></div>
          </dl>
          {preview.errors.length > 0 ? (
            <div className="json-preview-errors" role="alert">
              <strong>修正が必要です。</strong>
              <ul>{preview.errors.map((message) => <li key={message}>{message}</li>)}</ul>
            </div>
          ) : (
            <button type="button" className="button primary large" disabled={busy !== null} onClick={() => void applyImport()}>この内容で取り込む</button>
          )}
        </section>
      )}
    </div>
  );
}
