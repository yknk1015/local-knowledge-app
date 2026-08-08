import { useEffect, useState } from "react";
import { knowledgeApi, toAppError } from "../api/knowledgeApi";
import { ErrorState, LoadingState } from "../components/Feedback";
import type { AppError, SystemInfo } from "../types/domain";

export function SettingsPage() {
  const [info, setInfo] = useState<SystemInfo | null>(null);
  const [error, setError] = useState<AppError | null>(null);

  useEffect(() => {
    knowledgeApi.getSystemInfo().then(setInfo).catch((caught) => setError(toAppError(caught)));
  }, []);

  return (
    <div className="page settings-page">
      <div className="page-heading">
        <span className="eyebrow">端末情報</span>
        <h1>設定・情報</h1>
        <p>データの保存状態とアプリの情報を確認できます。</p>
      </div>

      {!info && !error && <LoadingState />}
      {error && <ErrorState error={error} />}
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
