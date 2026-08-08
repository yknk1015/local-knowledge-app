# KnowledgeApp

Windows 11で利用する、一人用のローカルFAQ・ナレッジ管理アプリです。FAQの検索・編集・保存はPC内で完結し、利用者が作成したFAQデータをソースコードやGitHubへ含めない構成にしています。

## 現在の開発状況

バージョン`0.1.0`の最初の開発単位として、次を実装しています。

- Windows利用者別フォルダへのデータ保存
- SQLiteの初期作成とスキーマ管理
- 分類の作成と5階層制限
- FAQの新規登録、編集、一覧、詳細表示
- 下書き、公開、廃止の状態
- 見出し、太字、斜体、箇条書き、番号付きリスト、表、HTTP/HTTPS参考URLを含む回答
- URLをクリックされない通常文字として保存し、必要な文字だけを参考URLへ設定・解除
- FAQ保存失敗時に原因と対処の表示位置へ自動移動
- 危険なURLや未許可の回答要素をRust側で拒否
- Gitコミット前とGitHub Actionsでの利用者データ混入検査

画像、タグ、同義語、高度な日本語検索、参考URLの確認付き外部オープン、HTML手順書、バックアップなどは、今後の開発単位で追加します。現版の参考URLは安全のため表示・編集だけを行い、クリックによる移動を停止します。

## 開発環境

- Windows 11 64ビット
- Node.js 24以降
- Rust stable（MSVC）
- Microsoft C++ Build Tools「C++によるデスクトップ開発」

## 初回準備

```powershell
npm install
./scripts/setup-git-hooks.ps1
```

## 開発実行

```powershell
npm run tauri dev
```

## テスト

```powershell
npm test
cargo test --manifest-path src-tauri/Cargo.toml
./scripts/check-no-runtime-data.ps1
```

Windows実行ファイルだけを確認する場合：

```powershell
npm run tauri build -- --no-bundle
```

## FAQデータの保存先

通常版は次の利用者専用フォルダを使用します。

```text
%LOCALAPPDATA%\jp.local.webknowledgesystem\
```

FAQ、履歴、画像、手順書、バックアップ、エクスポート、ログはGitへ登録しません。アプリは保存先の解決に失敗しても、作業フォルダの`./data`へ代替保存しません。

詳細は以下を参照してください。

- `FAQシステム要件定義書.md`
- `FAQシステム基本設計書.md`
- `ローカルFAQデータ・Git除外詳細設計書.md`
