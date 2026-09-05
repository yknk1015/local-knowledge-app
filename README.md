# KnowledgeApp

Windows 11で利用する、一人用のローカルFAQ・ナレッジ管理アプリです。FAQの検索・編集・保存はPC内で完結し、利用者が作成したFAQデータをソースコードやGitHubへ含めない構成にしています。

## 現在の開発状況

2026-09-06の利用者承認により、バージョン`0.5.0`のC#版を正式な利用・開発の主系とします。WPF＋WebView2から現行React／Tiptap UIを使い、以後のバックエンド改修はC#で行います。旧Tauri／Rust版はバックアップ互換などの回帰確認用として保持します。

利用者から今回のPCでの復元・確認完了の報告を受領し、C#正式版への本番切替を完了しました。会社PCでの切替や実プラグイン往復など、未報告・未実施の確認を合格へ置き換えるものではありません。現在の確認範囲は[第17段階の切替記録](src-csharp/第17段階_CSharp正式切替記録.md)を参照してください。

旧Tauri版は[外部保管記録](旧Tauri版_外部保管実施記録.md)のとおり、ソースZIPと旧インストーラーをリポジトリ外へ保存しています。現在のGitHubリポジトリをC#主系として継続します。移行SQLはC#管理下へ移し、通常のビルド・試験・配布は旧ソースを必要としません。旧版との比較は明示的な別経路で実行します。

次を実装しています。

- Windows利用者別フォルダへのデータ保存
- SQLiteの初期作成とスキーマ管理
- 分類の作成と5階層制限
- FAQの新規登録、編集、一覧、詳細表示
- FAQの複製、論理削除、復元、管理一覧
- 下書き、公開、廃止の状態
- 新着・更新表示期間と非表示設定
- FAQ本文への安全な添付画像
- 見出し、太字、斜体、箇条書き、番号付きリスト、表、HTTP/HTTPS参考URLを含む回答
- ローカルパス・UNCパスなどを自動で開かず、文字列どおりコピーする外部資料参照
- HTTP/HTTPSの表示文字・接続先・完全なURLを確認してから既定ブラウザーで開く外部資料参照
- URLをクリックされない通常文字として保存し、必要な文字だけを参考URLへ設定・解除
- FAQ保存失敗時に原因と対処の表示位置へ自動移動
- 危険なURLや未許可の回答要素をC#側で拒否
- 任意名・任意保存先のフルバックアップと復元
- ローカル利用者認証、利用者管理、FAQ作成者・更新者の記録
- 初期管理者だけに限定した暫定的な緊急認証（認証情報は配布責任者が別管理）
- Excel編集用CSVの書き出し、全行プレビュー、取込前バックアップ付き一括取込
- タグマスターの追加・名称変更・使用件数確認・未使用タグ削除と、FAQでの複数タグ選択
- 旧版の検索情報・関連FAQを消去しない互換保持（新しいFAQの入力画面では使用しない）
- 日本語表記ゆれの正規化、同義語、分類範囲、重要度を含む検索順位と一致理由
- 50件ページング、更新日・重要度の並び替え、検索条件と一覧位置の復元
- 検索・閲覧履歴の絞り込み、ページング、削除
- 分類、FAQ、タグ、同義語、関連情報の形式版付きJSON入出力
- Codexによる新規FAQ下書き、既存FAQの推敲・修正、複数FAQの非破壊統合、分類提案、承認・却下履歴
- DB移行前の安全バックアップ、移行失敗時の起動中止、単一起動制御
- Windows利用者単位NSISインストーラーと配布物混入検査
- Gitコミット前とGitHub Actionsでの利用者データ混入検査
- ソース・設定・設計文書のUTF-8（BOMなし）・LF統一と自動検査

専用HTML手順書管理は実装せず、会社管理のHTML、Excel、PDF、Wordなどをコピー用パスまたはHTTP/HTTPS参考URLで案内します。

会社管理の外部資料本体はKnowledgeAppへ取り込まず、検索、存在確認、リンク切れ検査、フルバックアップの対象に含めません。外部資料の配置、アクセス権、更新、安全性、バックアップは会社側の運用に従ってください。FAQ本文に保存した参照先文字列と参考URLは、通常のFAQデータとしてバックアップ・復元されます。

## 利用を始める

1. 配布された`KnowledgeApp-CSharp-0.5.0-setup.exe`を実行し、利用者単位でインストールします。Windows 11 x64、.NET 10 Desktop Runtime x64、WebView2 Runtimeが必要です。不足する実行環境は自動導入しません。会社PCで一人利用する場合も取得元とSHA-256を確認し、会社規定がコード署名・配布経路・アプリ実行制限の手続きを要求する場合はその規定を優先してください。
2. スタートメニューの「KnowledgeApp (C#)」から起動します。初回の専用データ領域は空です。ログインID`0000`、パスワード空欄でログインします。旧版のFAQを引き継ぐ場合は、次の「旧Tauri版からの切り替え」を先に行ってください。
3. 「利用者の管理」で初期管理者の「パスワード再設定」を選び、会社規定に沿うパスワードへ変更します。
4. 「設定・情報」で「空欄のパスワードを許可する」をOFFにし、「フルバックアップを作成」で初回バックアップを別の安全な保存先へ作成します。
5. 分類と「タグマスター」を登録してからFAQで必要なタグを選び、検索語、分類範囲、公開状態、並び順を指定して検索します。表記違いは「同義語の管理」から同じグループへ登録できます。
6. 外部資料のパスは回答内のコピー用テキスト枠へ、Web資料はHTTP/HTTPS参考URLへ設定します。ローカル・UNCパスは自動で開かず、利用者がコピーして会社の許可された方法で参照します。

バックアップから戻す場合は管理者で「設定・情報」→「バックアップから復元」を選び、内容の事前確認後に復元します。現在の状態は復元前にPC内へ自動退避されます。

Excelで一括編集する場合は「FAQの管理」→「CSVエクスポート」で最新CSVを書き出し、列を削除・並べ替えずに編集します。「CSVインポート」で全行のプレビューと警告を確認して反映してください。反映直前にフルバックアップが自動作成されます。CSVは完全移行形式ではないため、端末移行にはフルバックアップを使います。

詳しい初回設定、定期バックアップ、更新前確認、外部資料の責任範囲は`KnowledgeApp運用手順.md`を参照してください。`リリースノート_0.4.4.md`など旧版の文書は履歴として保持します。

### 旧Tauri版からの切り替え

1. 旧Tauri版の「設定・情報」からフルバックアップ（`.faqbackup`）を作成し、旧版を終了します。
2. C#版へログインし、「設定・情報」→「バックアップから復元」で、そのバックアップを明示的に選びます。プレビューと確認を経て復元します。
3. 復元後は旧版で使用していた利用者・パスワードで再ログインし、代表的なFAQ、画像、配色を確認します。以後の編集はC#版だけで行います。

C#版は旧Tauri版や試作版の保存先を自動で探索・読取・コピー・同期しません。旧版のデータと元バックアップは残り、C#での編集が旧版へ戻ることもありません。DBを直接コピーせず、利用者・設定・画像を含むフルバックアップを使用してください。移行後の再復元は現在のC#データを置き換える操作なので、最新のC#バックアップを先に作成してください。

## CodexでFAQ下書きを作る

1. KnowledgeAppを起動し、分類を登録します。分類の説明も入れると候補精度が上がります。
2. このソースフォルダをCodexで開き、「この内容のKnowledgeApp用FAQを作ってください」と依頼します。
3. Codexは`AGENTS.md`から専用スキルを読み、FAQ本文と分類候補を確認待ち提案箱へ送ります。
4. KnowledgeAppの「Codexからの提案」で本文と分類を確認し、「確認して下書きに取り込む」を選びます。
5. 通常のFAQ編集画面で必要な修正や画像追加を行い、利用者が公開します。

既存FAQを推敲・修正する場合はFAQ詳細の「Codexに推敲・修正を依頼」、複数FAQを統合する場合はFAQ管理画面で2～10件を選び「Codexへ委譲」を使用します。画面に表示された委譲番号付きの依頼文をCodexへ送ると、元FAQだけを対象に提案を作成します。修正案は元FAQの版が変わっていない場合だけ承認後に反映し、統合案は元FAQを残して新しい下書きにします。

却下した提案は「承認・却下履歴」から確認できます。同じ依頼系列では最新の却下案だけを再検討へ戻せます。

旧版向けプラグインはC#の保存先へ自動で切り替わりません。C#対応版を使用し、保存先の不一致を旧版領域の直接読取やファイルコピーで回避しないでください。プラグインのソース試験と、会社PC／Codexへ導入された実プラグインの確認は別に扱います。

アプリへ生成AI APIを組み込まない主な理由は、従量課金による費用増大を避けるためです。定額利用のCodexへは、利用者が必要な対象を明示して新規作成・修正・統合を委譲できます。CodexはSQLiteを直接読み書きせず、FAQを自動公開・削除しません。

## 開発環境

- Windows 11 64ビット
- Node.js 24以降
- .NET SDK 10.0.204（正式C#バックエンドとWPFホスト）
- WebView2 Runtime
- PowerShell 7（日本語を含むUTF-8検査スクリプトの直接実行）
- Rust stable（MSVC）とMicrosoft C++ Build Tools（旧Tauri互換回帰を実行する場合）

## 初回準備

```powershell
npm ci
./scripts/setup-git-hooks.ps1
```

## 開発実行

```powershell
npm run build
dotnet run --project ./src-csharp/KnowledgeApp.CSharp --configuration Release -- --rehearsal
```

開発・画面試験では必ず`--rehearsal`を付け、C#専用検証領域を使います。引数なしのC#起動は正式な本番領域を開きます。UI変更後は`npm run build`で更新してからC#を再ビルドしてください。バックエンドは`src-csharp/KnowledgeApp.Data`、ネイティブ画面は`src-csharp/KnowledgeApp.CSharp`、共通UIは`src`で開発します。

この作業ツリーには旧`src-tauri`とTauri CLI起動コマンドを置きません。フロントエンドの互換APIパッケージは既存の分岐・試験を維持するため残していますが、C#の通常開発でRust/Tauriのビルドは不要です。

旧版との比較が必要な場合は、外部保管した`src-tauri`の絶対パスを開発者が`KNOWLEDGEAPP_LEGACY_SOURCE`へ設定し、`KnowledgeApp.CodexInteropCheck`または`KnowledgeApp.FinalInteropCheck -- --with-legacy`を明示実行します。いずれも合成データだけを使用し、未指定時は旧ソースや利用者データを自動探索しません。GitHub Actionsでは手動起動の`legacy-compatibility.yml`を使用します。

NSIS配布物の作成には独立したNSIS 3の`makensis.exe`をPATHへ追加するか、`KNOWLEDGEAPP_NSIS_COMPILER`へ絶対パスを指定します。旧Tauriビルドによる準備は不要です。

## テスト

```powershell
npm test
npm run build
pwsh -NoProfile -File ./scripts/check-text-encoding.ps1
pwsh -NoProfile -File ./scripts/check-no-runtime-data.ps1
pwsh -NoProfile -File ./scripts/test-codex-plugin.ps1
dotnet build ./src-csharp/KnowledgeApp.CSharp --configuration Release
dotnet run --project ./src-csharp/KnowledgeApp.DataCheck --configuration Release
dotnet run --project ./src-csharp/KnowledgeApp.ProductionCheck --configuration Release
dotnet run --project ./src-csharp/KnowledgeApp.RestoreCrashCheck --configuration Release
dotnet run --project ./src-csharp/KnowledgeApp.MailCheck --configuration Release
```

上記は主な試験です。CIは認証、復元、Codex、UI、安全境界、配布などの専用試験をすべて実行します。`ProductionCheck`を含む自動試験は新規の合成一時領域を使い、本番FAQを読みません。会社PC・実データ・実プラグインの未実施確認を自動試験の合格と混同しません。

旧Tauriとの互換回帰を実行する場合：

```powershell
# KNOWLEDGEAPP_LEGACY_SOURCEは、保管したsrc-tauriの絶対パスを事前に設定する。
# 移動した古いtargetは再利用せず、新しいCARGO_TARGET_DIRを使う。
if (-not $env:KNOWLEDGEAPP_LEGACY_SOURCE) { throw '旧ソースの保存先を明示してください。' }
cargo test --manifest-path (Join-Path $env:KNOWLEDGEAPP_LEGACY_SOURCE 'Cargo.toml') --locked
dotnet run --project ./src-csharp/KnowledgeApp.CodexInteropCheck --configuration Release
dotnet run --project ./src-csharp/KnowledgeApp.FinalInteropCheck --configuration Release -- --with-legacy
```

正式な利用者単位NSISインストーラーを生成する場合：

```powershell
npm run build
dotnet build ./src-csharp/KnowledgeApp.CSharp --configuration Release
$releasePackage = & ./scripts/New-CSharpReleasePackage.ps1 | Select-Object -Last 1
./scripts/Test-CSharpReleasePackage.ps1 -PackageDirectory $releasePackage.packageDirectory -SourceUiDirectory ./dist
./scripts/check-no-runtime-data.ps1 -ReleaseDirectory $releasePackage.packageDirectory
$releaseInstaller = & ./scripts/New-CSharpInstaller.ps1 -PackageDirectory $releasePackage.packageDirectory | Select-Object -Last 1
$releaseInstaller
```

生成先は各スクリプトの結果に返されます。正式成果物は`KnowledgeApp-CSharp-0.5.0-win-x64.zip`と`KnowledgeApp-CSharp-0.5.0-setup.exe`です。ZIPはフォルダー全体で使用し、exeだけを移動しないでください。NSISコンパイラーはPATHまたは`KNOWLEDGEAPP_NSIS_COMPILER`で明示し、生成スクリプトは自動取得しません。CIではNSISを独立して準備します。

GitHub Actionsの通常CIはC#正式パッケージとNSISを主成果物として保存します。旧Rustの検査・相互試験は手動起動の別ワークフローへ分離しています。パッケージのSHA-256は整合性確認用であり、コード署名や会社の配布承認の代替ではありません。

0.4.3までのSurface全体試験結果を保持し、0.4.4以降はSurfaceで再試験しません。最初は会社FAQへ接続しない検証用Windows利用者と合成FAQを使用します。2026-08-16付で、一人利用試行では対象コミット、起動、利用者データ分離、通常・緊急認証、ローカルバックアップ・復元、Git混入防止を最低確認とし、正式配布向けの追加確認は延期しました。0.3.3～0.4.3の実機・診断文書は過去候補の履歴として保持します。

`会社環境検証手順_0.4.4.md`と`会社環境検証結果_0.4.4.md`は旧版の記録です。C#版で未実施の会社PC起動や実データ復元は別に確認します。コード署名・正式配布経路・社内承認、広範な実機・表示・ネットワーク確認は、会社規定が求める時点や複数人配布へ進む前に再開します。

## C#正式版と互換性

2026-08-29以降の段階移植・利用者受入を経て、2026-09-06にC#版への本番切り替え開始が承認されました。「UIなどにデグレを起こさない移行」の方針を維持し、React/Tiptap、検索、編集、認証、画像、バックアップ、Codexの安全境界を継承します。DB第7版とフルバックアップ形式第1版は変更しません。既存版と本番領域を共有しないことで、未更新の旧版がC#復元中断状態を無視して開く経路をなくします。

C#版も最大10件のFAQタブを備えます。1～10件を横スクロールなしで利用可能幅へ均等に縮小し、FAQ検索・一覧と各FAQの縦スクロール位置を起動中メモリで個別に復元します。11件目では既存タブを自動で閉じず、利用者へ既存タブを閉じるよう案内します。

「メール履歴を確認」は、利用者が明示的に「② .msgを確認」を押した場合だけ、選択フォルダ直下の`.msg`を1回ローカル読取します。選択・確認・マスク済み内容だけをCodex向け委譲へ保存し、未選択メールや原本パス・ファイル名は委譲しません。`.pst`と添付は読みません。実行方法と現状の制限は`src-csharp/README.md`を参照してください。

16MiB超の要求の成功対応や通常JSONの極深構造をRustへ逆方向取込する対応などは後続課題です。上限超過は安全に拒否し、未保存入力と既存データを保持します。以後の追加機能・不具合修正はC#主系で行います。

## FAQデータの保存先

通常版は次の利用者専用フォルダを使用します。

```text
%LOCALAPPDATA%\jp.local.webknowledgesystem.csharp\
```

`--rehearsal`指定の開発・試験時だけ`%LOCALAPPDATA%/jp.local.webknowledgesystem.csharp-rehearsal`を使用します。旧Tauri版の`%LOCALAPPDATA%/jp.local.webknowledgesystem`とは常に別領域です。C#は通常起動時と復元直前に検証済みの安全バックアップを保持し、復元中断時は専用領域内の記録と安全バックアップを検証して復旧します。

FAQ、履歴、画像、バックアップ、エクスポート、ログ、会社管理の外部資料はGitへ登録しません。アプリは保存先の解決に失敗しても、作業フォルダの`./data`へ代替保存しません。旧版互換用の`manuals`領域は非破壊で保持しますが、現行画面から外部資料を取り込む用途には使用しません。

詳細は以下を参照してください。

- `FAQシステム要件定義書.md`
- `FAQシステム基本設計書.md`
- `ローカルFAQデータ・Git除外詳細設計書.md`
- `GitHub公開記録.md`
