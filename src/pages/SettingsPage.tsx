import { open, save } from "@tauri-apps/plugin-dialog";
import { useEffect, useRef, useState } from "react";
import { knowledgeApi, toAppError } from "../api/knowledgeApi";
import { useAuth } from "../app/AuthContext";
import { useDisplaySettings } from "../app/ColorTheme";
import { ErrorState, LoadingState } from "../components/Feedback";
import type {
  AppError,
  BackupOverview,
  BackupPreview,
  BackupResult,
  PasswordPolicySettings,
  RestoreResult,
  SystemInfo,
  ColorTheme,
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
  const { user } = useAuth();
  const isAdmin = user?.role === "admin";
  const {
    colorTheme,
    showTopCategoryInTitle,
    showMascot,
    updateColorTheme,
    updateShowTopCategoryInTitle,
    updateShowMascot,
  } = useDisplaySettings();
  const [info, setInfo] = useState<SystemInfo | null>(null);
  const [overview, setOverview] = useState<BackupOverview | null>(null);
  const [infoError, setInfoError] = useState<AppError | null>(null);
  const [operationError, setOperationError] = useState<AppError | null>(null);
  const [operation, setOperation] = useState<BackupOperation>(null);
  const [preview, setPreview] = useState<BackupPreview | null>(null);
  const [backupResult, setBackupResult] = useState<BackupResult | null>(null);
  const [restoreResult, setRestoreResult] = useState<RestoreResult | null>(null);
  const [themeError, setThemeError] = useState<AppError | null>(null);
  const [themeSaving, setThemeSaving] = useState(false);
  const [titleDisplayError, setTitleDisplayError] = useState<AppError | null>(null);
  const [titleDisplaySaving, setTitleDisplaySaving] = useState(false);
  const [mascotDisplayError, setMascotDisplayError] = useState<AppError | null>(null);
  const [mascotDisplaySaving, setMascotDisplaySaving] = useState(false);
  const [passwordPolicy, setPasswordPolicy] = useState<PasswordPolicySettings | null>(null);
  const [passwordPolicyError, setPasswordPolicyError] = useState<AppError | null>(null);
  const [passwordPolicySaving, setPasswordPolicySaving] = useState(false);
  const feedbackRef = useRef<HTMLDivElement>(null);
  const displaySettingsSaving = themeSaving || titleDisplaySaving || mascotDisplaySaving;

  useEffect(() => {
    knowledgeApi.getSystemInfo().then(setInfo).catch((caught) => setInfoError(toAppError(caught)));
    if (isAdmin) {
      knowledgeApi.getBackupOverview().then(setOverview).catch(() => setOverview(null));
      knowledgeApi.getPasswordPolicy()
        .then(setPasswordPolicy)
        .catch((caught) => setPasswordPolicyError(toAppError(caught)));
    }
  }, [isAdmin]);

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

  const changeColorTheme = async (nextTheme: ColorTheme) => {
    if (nextTheme === colorTheme || displaySettingsSaving) return;
    setThemeError(null);
    setThemeSaving(true);
    try {
      await updateColorTheme(nextTheme);
    } catch (caught) {
      setThemeError(toAppError(caught));
    } finally {
      setThemeSaving(false);
    }
  };

  const changeTitleDisplay = async (enabled: boolean) => {
    if (enabled === showTopCategoryInTitle || displaySettingsSaving) return;
    setTitleDisplayError(null);
    setTitleDisplaySaving(true);
    try {
      await updateShowTopCategoryInTitle(enabled);
    } catch (caught) {
      setTitleDisplayError(toAppError(caught));
    } finally {
      setTitleDisplaySaving(false);
    }
  };

  const changeMascotDisplay = async (enabled: boolean) => {
    if (enabled === showMascot || displaySettingsSaving) return;
    setMascotDisplayError(null);
    setMascotDisplaySaving(true);
    try {
      await updateShowMascot(enabled);
    } catch (caught) {
      setMascotDisplayError(toAppError(caught));
    } finally {
      setMascotDisplaySaving(false);
    }
  };

  const changeAllowEmptyPasswords = async (enabled: boolean) => {
    if (!passwordPolicy || enabled === passwordPolicy.allowEmptyPasswords || passwordPolicySaving) return;
    const previous = passwordPolicy;
    setPasswordPolicyError(null);
    setPasswordPolicySaving(true);
    setPasswordPolicy({ allowEmptyPasswords: enabled });
    try {
      setPasswordPolicy(await knowledgeApi.savePasswordPolicy({ allowEmptyPasswords: enabled }));
    } catch (caught) {
      setPasswordPolicy(previous);
      setPasswordPolicyError(toAppError(caught));
    } finally {
      setPasswordPolicySaving(false);
    }
  };

  return (
    <div className="page settings-page">
      <div className="page-heading">
        <span className="eyebrow">表示・バックアップ・端末情報</span>
        <h1>設定・情報</h1>
        <p>画面表示を選び、データとCodex連携用の保存先を確認できます。バックアップと復元は管理者が操作できます。</p>
      </div>

      <section className="panel appearance-panel" aria-labelledby="appearance-heading">
        <div className="appearance-heading">
          <div>
            <span className="eyebrow">見やすい配色を選ぶ</span>
            <h2 id="appearance-heading">画面の配色</h2>
            <p>選んだ配色はすぐに全画面へ反映され、次回起動時も維持されます。</p>
          </div>
          <span className="theme-current" aria-live="polite">
            {themeSaving ? "保存しています…" : `${colorTheme === "blue" ? "ブルー" : "グリーン"}を使用中`}
          </span>
        </div>

        <fieldset className="theme-options" disabled={displaySettingsSaving}>
          <legend className="sr-only">画面の配色を選択</legend>
          <label className={`theme-option${colorTheme === "blue" ? " selected" : ""}`}>
            <input
              type="radio"
              name="color-theme"
              value="blue"
              aria-label="ブルー"
              checked={colorTheme === "blue"}
              onChange={() => void changeColorTheme("blue")}
            />
            <span className="theme-preview blue-preview" aria-hidden="true">
              <span className="theme-preview-sidebar"><i /><i /><i /></span>
              <span className="theme-preview-content">
                <i className="theme-preview-search" />
                <span><i /><i /></span>
              </span>
            </span>
            <span className="theme-option-copy">
              <span className="theme-option-title">
                <strong>ブルー</strong>
                <small>おすすめ</small>
              </span>
              <span>鮮明な青で、選択中の項目や主要操作を見分けやすくします。</span>
            </span>
            <span className="theme-selection" aria-hidden="true">{colorTheme === "blue" ? "✓" : ""}</span>
          </label>

          <label className={`theme-option${colorTheme === "green" ? " selected" : ""}`}>
            <input
              type="radio"
              name="color-theme"
              value="green"
              aria-label="グリーン"
              checked={colorTheme === "green"}
              onChange={() => void changeColorTheme("green")}
            />
            <span className="theme-preview green-preview" aria-hidden="true">
              <span className="theme-preview-sidebar"><i /><i /><i /></span>
              <span className="theme-preview-content">
                <i className="theme-preview-search" />
                <span><i /><i /></span>
              </span>
            </span>
            <span className="theme-option-copy">
              <span className="theme-option-title"><strong>グリーン</strong></span>
              <span>従来の落ち着いた緑を使い、やわらかな印象で表示します。</span>
            </span>
            <span className="theme-selection" aria-hidden="true">{colorTheme === "green" ? "✓" : ""}</span>
          </label>
        </fieldset>

        <p className="theme-note">公開・成功、注意、エラーなど意味を持つ色は、識別しやすさを保つため配色を変えても維持します。</p>
        {themeError && <ErrorState error={themeError} />}

        <div className="title-display-setting">
          <div>
            <span className="eyebrow">検索結果を見分けやすくする</span>
            <h3>FAQタイトルの分類表示</h3>
            <p>ONの場合、検索結果のタイトルを「【トップ分類名】質問文」の形式で表示します。保存済みタイトルは変更しないため、分類名の変更やFAQの移動にも自動で追従します。</p>
          </div>
          <label className="title-display-toggle">
            <input
              type="checkbox"
              checked={showTopCategoryInTitle}
              disabled={displaySettingsSaving}
              onChange={(event) => void changeTitleDisplay(event.target.checked)}
            />
            <span>
              <strong>トップ分類名を表示する</strong>
              <small aria-live="polite">
                {titleDisplaySaving
                  ? "保存しています…"
                  : showTopCategoryInTitle
                    ? "現在はONです"
                    : "現在はOFFです"}
              </small>
            </span>
          </label>
        </div>
        {titleDisplayError && <ErrorState error={titleDisplayError} />}

        <div className="title-display-setting mascot-display-setting">
          <div>
            <span className="eyebrow">左下のマスコット</span>
            <h3>FAQ Owlの表示</h3>
            <p>OFFにすると全画面で非表示になります。マスコットを右クリックして非表示にした場合も、ここから再表示できます。</p>
          </div>
          <label className="title-display-toggle">
            <input
              type="checkbox"
              checked={showMascot}
              disabled={displaySettingsSaving}
              onChange={(event) => void changeMascotDisplay(event.target.checked)}
            />
            <span>
              <strong>マスコットを表示する</strong>
              <small aria-live="polite">
                {mascotDisplaySaving
                  ? "保存しています…"
                  : showMascot
                    ? "現在はONです"
                    : "現在はOFFです"}
              </small>
            </span>
          </label>
        </div>
        {mascotDisplayError && <ErrorState error={mascotDisplayError} />}
      </section>

      {isAdmin && (
        <section className="panel appearance-panel password-policy-panel" aria-labelledby="password-policy-heading">
          <div className="title-display-setting">
            <div>
              <span className="eyebrow">管理者向け</span>
              <h2 id="password-policy-heading">利用者パスワードの設定</h2>
              <p>OFFにすると、以後の利用者追加とパスワード再設定で1文字以上の入力が必須になります。すでに空欄で登録済みの利用者は変更されません。</p>
            </div>
            {passwordPolicy && (
              <label className="title-display-toggle">
                <input
                  type="checkbox"
                  checked={passwordPolicy.allowEmptyPasswords}
                  disabled={passwordPolicySaving}
                  onChange={(event) => void changeAllowEmptyPasswords(event.target.checked)}
                />
                <span>
                  <strong>空欄のパスワードを許可する</strong>
                  <small aria-live="polite">
                    {passwordPolicySaving
                      ? "保存しています…"
                      : passwordPolicy.allowEmptyPasswords
                        ? "現在は許可しています"
                        : "現在は許可していません"}
                  </small>
                </span>
              </label>
            )}
          </div>
          {!passwordPolicy && !passwordPolicyError && <LoadingState label="パスワード設定を読み込んでいます…" />}
          {passwordPolicyError && <ErrorState error={passwordPolicyError} />}
        </section>
      )}

      {isAdmin ? (
      <section className="panel backup-panel" aria-labelledby="backup-heading">
        <div className="backup-intro">
          <div>
            <span className="eyebrow">大切なFAQを守る</span>
            <h2 id="backup-heading">フルバックアップと復元</h2>
            <p>FAQ、分類、履歴、設定、添付画像を1つのファイルにまとめます。会社管理の外部資料本体は含みません。</p>
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
              <div><dt>データサイズ</dt><dd>{formatBytes(preview.totalBytes)}</dd></div>
            </dl>
            <p className="restore-note">復元を開始する前に、現在の状態をPC内へ自動退避します。破損や形式不一致があるファイルは復元しません。会社管理の外部資料本体は復元対象外です。</p>
          </div>
        )}
      </section>
      ) : (
        <section className="panel backup-panel" aria-labelledby="backup-heading">
          <div className="backup-intro">
            <div>
              <span className="eyebrow">管理者向け機能</span>
              <h2 id="backup-heading">フルバックアップと復元</h2>
              <p>利用者情報を含むため、バックアップと復元は管理者だけが操作できます。</p>
            </div>
          </div>
        </section>
      )}

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
          <section className="panel setting-card codex-storage-card" aria-labelledby="codex-storage-heading">
            <div>
              <div className="setting-card-heading">
                <h2 id="codex-storage-heading">Codex連携用の保存先</h2>
                <span className="readonly-badge">参照のみ</span>
              </div>
              <p>安全性と連携の整合性を保つため、アプリが管理する固定場所を使用します。設定画面からは変更できません。</p>
              <dl className="codex-storage-paths">
                <div>
                  <dt>分類一覧</dt>
                  <dd><code className="path-display">{info.codexCategoryCatalogPath}</code></dd>
                </div>
                <div>
                  <dt>提案箱</dt>
                  <dd><code className="path-display">{info.codexInboxPath}</code></dd>
                </div>
              </dl>
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
