import { open, save } from "@tauri-apps/plugin-dialog";
import { useEffect, useRef, useState } from "react";
import { knowledgeApi, toAppError } from "../api/knowledgeApi";
import { ErrorState, LoadingState } from "../components/Feedback";
import type {
  AppError,
  BackupOverview,
  BackupPreview,
  BackupResult,
  RestoreResult,
  SystemInfo,
} from "../types/domain";

type BackupOperation = "creating" | "inspecting" | "restoring" | null;

function defaultBackupName() {
  const now = new Date();
  const number = (value: number) => String(value).padStart(2, "0");
  return `KnowledgeApp_${now.getFullYear()}${number(now.getMonth() + 1)}${number(now.getDate())}_${number(now.getHours())}${number(now.getMinutes())}.faqbackup`;
}

function fileStem(path: string) {
  const name = path.split(/[\\/]/).at(-1) ?? "KnowledgeApp_Backup";
  return name.replace(/\.faqbackup$/i, "");
}

function formatDate(value: string) {
  return new Date(value).toLocaleString("ja-JP");
}

function formatBytes(value: number) {
  if (value < 1024) return `${value} B`;
  if (value < 1024 * 1024) return `${(value / 1024).toFixed(1)} KB`;
  return `${(value / (1024 * 1024)).toFixed(1)} MB`;
}

export function SettingsPage() {
  const [info, setInfo] = useState<SystemInfo | null>(null);
  const [overview, setOverview] = useState<BackupOverview | null>(null);
  const [infoError, setInfoError] = useState<AppError | null>(null);
  const [operationError, setOperationError] = useState<AppError | null>(null);
  const [operation, setOperation] = useState<BackupOperation>(null);
  const [preview, setPreview] = useState<BackupPreview | null>(null);
  const [backupResult, setBackupResult] = useState<BackupResult | null>(null);
  const [restoreResult, setRestoreResult] = useState<RestoreResult | null>(null);
  const feedbackRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    knowledgeApi.getSystemInfo().then(setInfo).catch((caught) => setInfoError(toAppError(caught)));
    knowledgeApi.getBackupOverview().then(setOverview).catch(() => setOverview(null));
  }, []);

  useEffect(() => {
    if (operationError || backupResult || restoreResult) {
      feedbackRef.current?.scrollIntoView({ behavior: "smooth", block: "center" });
    }
  }, [backupResult, operationError, restoreResult]);

  const clearFeedback = () => {
    setOperationError(null);
    setBackupResult(null);
    setRestoreResult(null);
  };

  const createBackup = async () => {
    clearFeedback();
    setPreview(null);
    try {
      const name = defaultBackupName();
      const defaultPath = overview?.defaultDirectory
        ? `${overview.defaultDirectory.replace(/[\\/]$/, "")}\\${name}`
        : name;
      const destination = await save({
        title: "フルバックアップの保存先と名前を選択",
        defaultPath,
        filters: [{ name: "KnowledgeAppフルバックアップ", extensions: ["faqbackup"] }],
      });
      if (!destination) return;

      setOperation("creating");
      const displayName = fileStem(destination);
      try {
        setBackupResult(await knowledgeApi.createFullBackup(destination, displayName));
      } catch (caught) {
        const error = toAppError(caught);
        if (
          error.code === "BK-002" &&
          window.confirm("同じ名前のバックアップがあります。内容を確認のうえ上書きしますか？")
        ) {
          setBackupResult(await knowledgeApi.createFullBackup(destination, displayName, true));
        } else {
          throw error;
        }
      }
    } catch (caught) {
      setOperationError(toAppError(caught));
    } finally {
      setOperation(null);
    }
  };

  const chooseRestoreBackup = async () => {
    clearFeedback();
    setPreview(null);
    try {
      const selected = await open({
        title: "復元するフルバックアップを選択",
        multiple: false,
        directory: false,
        filters: [{ name: "KnowledgeAppフルバックアップ", extensions: ["faqbackup"] }],
      });
      if (!selected) return;
      setOperation("inspecting");
      setPreview(await knowledgeApi.inspectBackup(selected));
    } catch (caught) {
      setOperationError(toAppError(caught));
    } finally {
      setOperation(null);
    }
  };

  const restoreBackup = async () => {
    if (!preview) return;
    const confirmed = window.confirm(
      `「${preview.displayName}」を復元します。\n現在の状態は自動で安全バックアップされます。続けますか？`,
    );
    if (!confirmed) return;

    clearFeedback();
    try {
      setOperation("restoring");
      setRestoreResult(await knowledgeApi.restoreBackup(preview.sourcePath));
      setPreview(null);
    } catch (caught) {
      setOperationError(toAppError(caught));
    } finally {
      setOperation(null);
    }
  };

  const busy = operation !== null;

  return (
    <div className="page settings-page">
      <div className="page-heading">
        <span className="eyebrow">バックアップと端末情報</span>
        <h1>設定・情報</h1>
        <p>FAQ一式のバックアップ・復元と、データの保存状態を確認できます。</p>
      </div>

      <section className="panel backup-panel" aria-labelledby="backup-heading">
        <div className="backup-intro">
          <div>
            <span className="eyebrow">大切なFAQを守る</span>
            <h2 id="backup-heading">フルバックアップと復元</h2>
            <p>FAQ、分類、履歴、設定、添付画像、手順書を1つのファイルにまとめます。</p>
          </div>
          <div className="backup-actions">
            <button type="button" className="button primary large" onClick={createBackup} disabled={busy}>
              フルバックアップを作成
            </button>
            <button type="button" className="button secondary large" onClick={chooseRestoreBackup} disabled={busy}>
              バックアップから復元
            </button>
          </div>
        </div>

        {overview && (
          <dl className="backup-overview" aria-label="今回のバックアップ対象">
            <div><dt>FAQ</dt><dd>{overview.counts.articles}件</dd></div>
            <div><dt>分類</dt><dd>{overview.counts.categories}件</dd></div>
            <div><dt>添付画像</dt><dd>{overview.counts.attachments}件</dd></div>
            <div><dt>手順書</dt><dd>{overview.counts.manuals}件</dd></div>
            <div><dt>推定サイズ</dt><dd>約 {formatBytes(overview.estimatedBytes)}</dd></div>
          </dl>
        )}

        {operation && (
          <div className="backup-progress" role="status">
            <span className="spinner" aria-hidden="true" />
            <strong>
              {operation === "creating" && "バックアップを検査して保存しています…"}
              {operation === "inspecting" && "バックアップの安全性と内容を確認しています…"}
              {operation === "restoring" && "現在の状態を退避して復元しています…"}
            </strong>
            <span>完了するまでアプリを閉じないでください。</span>
          </div>
        )}

        <div ref={feedbackRef} className="backup-feedback" tabIndex={-1}>
          {operationError && <ErrorState error={operationError} />}
          {backupResult && (
            <div className="backup-success" role="status">
              <strong>フルバックアップを作成しました</strong>
              <span>{backupResult.destinationPath}</span>
              <small>FAQ {backupResult.counts.articles}件・{formatBytes(backupResult.totalBytes)}</small>
            </div>
          )}
          {restoreResult && (
            <div className="backup-success" role="status">
              <strong>バックアップから復元しました</strong>
              <span>FAQ {restoreResult.counts.articles}件を復元しました。</span>
              <small>復元前の安全バックアップ：{restoreResult.safetyBackupPath}</small>
            </div>
          )}
        </div>

        {preview && (
          <div className="restore-preview">
            <div className="restore-preview-heading">
              <div>
                <span className="eyebrow">復元前の確認</span>
                <h3>{preview.displayName}</h3>
              </div>
              <button type="button" className="button primary" onClick={restoreBackup} disabled={busy}>
                この内容を復元
              </button>
            </div>
            <dl className="backup-summary">
              <div><dt>作成日時</dt><dd>{formatDate(preview.createdAt)}</dd></div>
              <div><dt>FAQ</dt><dd>{preview.counts.articles}件</dd></div>
              <div><dt>分類</dt><dd>{preview.counts.categories}件</dd></div>
              <div><dt>添付画像</dt><dd>{preview.counts.attachments}件</dd></div>
              <div><dt>手順書</dt><dd>{preview.counts.manuals}件</dd></div>
              <div><dt>データサイズ</dt><dd>{formatBytes(preview.totalBytes)}</dd></div>
            </dl>
            <p className="restore-note">復元を開始する前に、現在の状態をPC内へ自動退避します。破損や形式不一致があるファイルは復元しません。</p>
          </div>
        )}
      </section>

      {!info && !infoError && <LoadingState label="端末情報を読み込んでいます…" />}
      {infoError && <ErrorState error={infoError} />}
      {info && (
        <div className="settings-grid">
          <section className="panel setting-card safe">
            <span className="setting-icon" aria-hidden="true">✓</span>
            <div>
              <h2>FAQデータはソースコードと分離されています</h2>
              <p>このPCの利用者専用フォルダに保存され、開発用フォルダやインストール先には保存されません。</p>
            </div>
          </section>
          <section className="panel setting-card">
            <div>
              <h2>データ保存先</h2>
              <code className="path-display">{info.dataRoot}</code>
              <p className="subtle">データベース：{info.databasePath}</p>
            </div>
          </section>
          <section className="panel setting-card">
            <div>
              <h2>アプリ情報</h2>
              <dl className="info-list">
                <div><dt>アプリ版</dt><dd>{info.appVersion}</dd></div>
                <div><dt>保存方式</dt><dd>SQLite（このPC内）</dd></div>
              </dl>
            </div>
          </section>
          <section className="panel setting-card warning">
            <span className="setting-icon" aria-hidden="true">!</span>
            <div>
              <h2>登録しない情報</h2>
              <p>パスワード、秘密鍵、個人情報、会社規定で保存が禁止されている情報はFAQへ登録しないでください。</p>
            </div>
          </section>
        </div>
      )}
    </div>
  );
}
