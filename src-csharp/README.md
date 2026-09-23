# KnowledgeApp C# 0.7.3

0.7.3では少数時のFAQタブ間隔を修正しました。0.7.2の起動改善を維持し、.NETを内蔵したインストール不要の単一exeを通常配布にしました。リポジトリルートで`npm run build:exe`を実行すると、最新UI・C#の準備から実exeの合成ログイン検査まで完了します。出力された`standalone/KnowledgeApp.CSharp.exe`だけを配布します。対象PCにはWebView2 Runtimeが必要です。既存のC#専用データ保存先と`--rehearsal`を維持します。現在の[起動・配布手順](../README.md)を参照してください。

0.7.0で管理者・FAQ編集者・閲覧のみの3役割、用途別保存先設定、Windows 11共有サーバーへの接続を追加しました。単独利用は従来のC#専用保存先を維持します。共有導入・既存データ引継ぎは[共有サーバー導入手順](../共有サーバー導入手順.md)を参照してください。DBをNASへ直接置かず、PC内のサービスからアクセスします。実環境への自動移行やサービス登録は行いません。

0.6.0は管理者の16文字復旧キー・新パスワード設定・固定緊急認証廃止に対応します。DBは第8版です。第7版の直接更新は事前フルバックアップ後に行い、旧バックアップはステージ移行します。以下の段階別記録のDB第7版などの記述は当時の履歴です。現行の仕様と検証は[復旧キー実装記録](../管理者パスワード復旧_実装計画.md)を参照してください。

このフォルダは2026-09-06以降の正式主系です。今後の改修はC#へ実装し、既存React/Tiptap/CSSを維持します。下記の第16段階以前は判断経緯・試験履歴であり、現在の起動手順ではありません。

設計基準は`FAQシステム要件定義書.md` v1.63、`FAQシステム基本設計書.md` v1.84、`ローカルFAQデータ・Git除外詳細設計書.md` v0.85です。正式切替の基準はそれぞれ1.17、4.6.13、2.0.8に保持し、現行の配布方式は各文書冒頭を優先します。利用者から今回のPCの復元・確認完了を受領し、正式切替を完了しました。対象環境を含む全体試験完了とは区別し、未実施項目は[今後の課題](../今後の課題.md)へ集約します。旧版文書の参照先と削除対象は[文書整理ガイド](../文書整理ガイド.md)を参照してください。

旧Tauriソースはリポジトリ外とGit履歴へ保管しました。移行SQLは`KnowledgeApp.Data/Migrations`が所有し、通常のC#ビルドに旧ソースは不要です。現在の試験・配布手順はルートREADMEと`旧Tauri版_外部保管実施記録.md`を参照してください。下記の歴史的Rust相互試験コマンドを現在実行する場合は`KNOWLEDGEAPP_LEGACY_SOURCE`へ旧ソースの絶対パスを明示し、FinalInteropCheckには`--with-legacy`を付けます。

## 現在：第17段階・C#正式主系

- 通常起動はC#本番専用`LocalApplicationData/jp.local.webknowledgesystem.csharp`。旧TauriとDBを共有しません。
- 開発・試験は`dotnet run --project src-csharp/KnowledgeApp.CSharp -- --rehearsal`。通常起動を試験代わりに使わないでください。
- FAQ引継ぎは旧版で作成したフルバックアップをC#の「設定・情報」で明示復元します。旧版のファイル探索・自動同期・DB直置きはしません。
- 通常配布は`npm run build:exe`。補助方式は`scripts/New-CSharpReleasePackage.ps1`と`scripts/New-CSharpInstaller.ps1`。`--production-candidate`は廃止・拒否します。
- 切替後の正本はC#です。旧版と検証版は変更せず残しますが、その後のC#更新は自動では届きません。
- Codex依頼はこのリポジトリの更新済み専用スキルを使用します。外部タスクの古いプラグインは再導入確認が必要です。

操作は[切替ガイド](CSharp正式切替ガイド.md)、検証結果は[第17段階記録](第17段階_CSharp正式切替記録.md)を参照してください。

## 第16段階の記録（最終移行準備・過去の判断）

第15段階D1～D4は利用者全合格です。採用された最終計画F1～F7に合わせ、本番向け固定ルート・排他・起動前退避、中断復旧、範囲外要求の即時案内、旧版との実バックアップ往復、PowerShell委譲の日時保持、NSISの隔離ライフサイクル試験を追加しました。通常起動の固定検証環境、React/Tiptap/CSS、FAQ・画像・DB第7版・既存の認証と委譲制限は維持します。

**第16段階では本番候補の実使用を無効にしています。** `--production-candidate`の実装は準備しましたが、旧TauriがC#復元中断記録を無視する経路をレビューで見つけたため、`CandidateUseEnabled=false`としてDBを開く前に止めます。Rustソースにも記録を検出して停止する保護を追加しますが、既に導入された未更新の旧exeには届きません。復旧を考慮した旧版切戻し入口を確認するまで有効化しません。試作表示の除去やゲート値の書換えだけで正式化しないでください。

自動試験は新規の合成ルートのみです。既存の本番/検証DB・FAQ・メール原本を読取・変更していません。実プラグインの登録/実会話、利用者の実データ代表確認、対象PCの最終確認は未実施として分けます。現在は追加の手動再試験を依頼せず、最終実行物が揃ってから必要な操作だけを案内します。

主な追加確認は`ProductionCheck`、`RestoreCrashCheck`、`LegacyInstanceCheck`、`InstallationGuardCheck`、`FinalInteropCheck`、`Test-CSharpInstallerRegression.ps1`です。新しい試作NSISは旧Tauriを置き換えない専用導入先・登録を使い、実行中なら更新/削除を拒否します。現段階ではインストールや本番切替を自動実行しません。

PowerShell修正は検証後の元JSON文字列を保持するものであり、未知/重複項目の消去や版比較の緩和はしません。`plugin-creator`の手順で登録元を確認できなかったため、ソース修正と実コマンドの合成試験までとし、登録・設定・キャッシュを変更していません。

## 第15段階の記録（受入完了）

第14段階C1～C4（C2は再試験）は利用者から全合格を受領しました。第15段階は、Codexの委譲・提案・保存済み履歴・同一受付番号の意味比較に残るC#のJSON深さ64制限を補完します。Rust実測と同じ全体127コンテナまでとし、提案1MiB・委譲5MiB、安全構造、画像保持、承認と競合確認は維持します。タイトルは「KnowledgeApp C# 移行試作 — 第15段階 — Codex本文互換確認」です。画面デザイン・自然文検索・DB第7版は変更しません。

第15段階の手順は`Documents/KnowledgeApp_Test/outputs/csharp-codex-depth-20260905/CSharp_Codex本文互換_試験記録.md`、パッケージと合成JSONは同じTestルートの`runtime/csharp-codex-depth-20260905`へ分離します。手動では深い合成FAQの取込とローカル委譲準備だけを確認し、実プラグインへは依頼文を送りません。

2026-09-05に利用者からD1～D4すべて良好との報告を受領し、第15段階の画面受入を完了しました。起動・現在状態の退避、深い合成FAQの取込・表示、明示選択した1件のローカル委譲準備と元FAQ保持、試験前状態への復元を合格とします。これは利用者による確認であり、Codex自身の実画面操作や本番プラグインとの往復試験ではありません。実連携・本番・配布等の残条件と本番切替保留は維持します。今回は結果記録だけを更新し、アプリの変更・再配布や次段階の実装は行いません。

第15段階はCodex72シナリオ、本文・JSON互換271件、C#/Rust実サービス相互往復26チェック、Rust全体86件が合格しました。相互往復のRust側1試験は専用合成ルートを持つC#試験から個別実行済みです。DataCheck全体、復元確認112件、旧版716件、継続保存74件、Security147件、単一起動37件、UI同梱13件、MailCheck、React210件も回帰合格し、型検査・UIビルド・WPF publish、Rust整形・Clippyは成功しました。個別の手動用JSONは生成時20件に加え、実Tiptap/ProseMirrorスキーマでも確認しています。

新しい比較試験は`dotnet run --project src-csharp/KnowledgeApp.CodexInteropCheck --configuration Release`で実行します。Rust依存物の取得・通常試験後に実行し、固定のRust試験だけを起動して新規合成ファイルを往復させます。`node scripts/test-csharp-codex-fixture.mjs`はRichCompatibilityCheckのReleaseビルド後に実行し、固定の生成モードが新規作成した手動用JSONだけをSHAと実エディター構造へ照合します。いずれも入力パスを受け付けず、本番データを探索しません。CIにも同じ順序で追加しました。

保存先は`%LOCALAPPDATA%/jp.local.webknowledgesystem.csharp-rehearsal`だけです。本番Tauri版の保存先は開かずコピーもしません。初回はFAQ・分類・タグ・提案が空で、初期管理者`0000`／空パスワードだけを用意します。FAQ・画像・設定・利用者・履歴・委譲等は終了後も保持しますが、未保存編集・ログイン・タブ・スクロールは再起動で破棄します。既存領域の初期化途中・DB欠落・破損・現行以外の版は停止し、勝手に初期化しません。旧版はDBを直置きせず、設定画面から第1～7版の正規バックアップを明示復元します。

検査と復元では元アーカイブを変更せず、生成した作業コピーだけに同梱の歴史的移行SQLを適用します。現在状態のフル安全バックアップと失敗時のロールバックを維持します。既存の利用者・パスワードは保持し、第1～5版と第6版の正規の利用者未作成状態だけ初期管理者を作成します。旧版の検査表示・確認ダイアログにその注意を出します。第7版の利用者欠落を初期化で隠しません。

メール委譲も専用検証ルートへ保存します。実Codexプラグインは未接続のため、メール画面では依頼文コピーを行いません。FAQ・メールとも試作の依頼文を本番向けプラグインへ送らないでください。

第11段階以降は、UIをexe隣の`ui`から読み、`ui-manifest.json`で構成とSHA-256を照合します。exeだけを移動せず、パッケージのフォルダー全体を使ってください。以下に残る第10段階以前のビルド先・`dist`祖先探索の説明は、その旧ビルドに対する履歴です。新しいソースではUI変更後にC#版の再ビルドが必要です。

第2起動は既存画面の表示・復元だけを要求し、新たなDB・WebViewを作りません。旧段階が起動中ならその画面が表示されるため、旧C#試作を手動終了してから第15段階のタイトルを確認してください。Tauri版は別系統で、終了・変更は不要です。ブラウザーのプロファイルだけを起動ごとの別のOS一時ルートへ作成し、終了時も検証FAQの保存先は削除しません。本番データ・実Codexプラグインは未接続、通常配布のNSIS・更新・アンインストールは未検証です。

復元の確認情報は10分間・同じログイン中だけ有効で、試行後は再利用できません。バックアップ差替え・別パス・再ログイン・期限切れ時はBK-011の案内に従い「バックアップから復元」で選び直してください。通常のバックアップ作成・安全退避・復元・再ログインは従来どおりです。

本文は10万字・1万ノード等のC#独自制限を外し、安全構造とJSON深さ128で扱います。受信・転送DTOは外側込み144です。Codex専用JSONはルート込み127で、通常の保存本文とは外側構造の分だけ受理範囲が異なります。C#要求16MB超の巨大FAQと通常JSONの極深構造をRustへ逆方向再取込する境界差、本番コピー比較・実連携・配布と全体UIは残課題とし、本番切替を保留します。JSON取込のSHA照合・検証・反映は、同じ読込済み文書を使用します。

### 第14段階の受入履歴

第14段階は本文・JSON互換213件、復元確認・認証競合112件、旧版バックアップ716件、継続保存74件、DataCheck全体、Codex45件、Security147件、MailCheck、単一起動37件、UI同梱13件、React210件が合格しました。型検査・UIビルド・WPF publishも成功しています。手動C1～C4は`Documents/KnowledgeApp_Test/outputs/csharp-content-restore-20260905/CSharp_本文互換・復元確認_試験記録.md`へ分離し、合成長文JSONと第14段階パッケージを同じTestルートの`runtime/csharp-content-restore-20260905`へ新規配置します。

パッケージ26試験、リポジトリ外の最終ファイル照合、利用者データ混入検査、UTF-8/LF検査も合格しました。第14段階のログイン画面まで実画面で確認しました。C2初回は手動用JSONの管理ID衝突で中断しましたが、修正版による再試験を経て、2026-09-05に利用者からC1～C4全合格を受領しました。初回不合格の記録は保持し、本番切替許可は引き続きfalseです。

### C2用テストJSONの修正（アプリの更新不要）

元の手動用JSONは新規DBで採番した`CAT-00001`・`FAQ-00001`が前段階の別の内部IDのデータと衝突していました。衝突拒否は維持し、テスト専用の空DBだけで採番開始値を2へ設定した、分類1件・公開FAQ1件・本文100001文字のJSONへ差し替えます。既存の管理ID1を持つFAQ1件・分類1件・PNG1件・ブルー設定を合成DBで再現して検証し、既存管理ID2がある環境では今後も拒否します。実DBの番号や本文は探索しません。

再試験は同じ第14段階アプリで、リポジトリ外の`Documents/KnowledgeApp_Test/runtime/csharp-content-restore-20260905/fixtures/Synthetic_long_document_retest.knowledge-export.json`を使用しました。手順と結果欄は`Documents/KnowledgeApp_Test/outputs/csharp-content-restore-20260905/CSharp_本文互換・復元確認_C2再試験.md`へ別名保存し、C1のバックアップと元の記録を保持しています。旧手順の「下書き」は実態に合わせて「公開」へ訂正しました。C2再試験・C3・C4も利用者合格を受領し、この試験データ修正では製品コード・設計を変更していません。

追加後のRichCompatibilityCheckは251件（既存213＋今回38）、最終JSONの生成・検証モードは38件が合格しました。React210件、型検査・UIビルド、UTF-8/LF・データ混入・diff検査も合格です。`dotnet run --project src-csharp/KnowledgeApp.RichCompatibilityCheck --configuration Release -- --write-manual-fixture`で新規合成JSONを生成し、その実出力を検証後にパスとSHA-256を返します。実DBや入力パスを引数に渡すモードはありません。生成物はOS一時フォルダーに置き、個別の配布・結果はリポジトリ外へ保存してください。

```powershell
npm run build
dotnet run --project src-csharp/KnowledgeApp.LegacyBackupCheck --configuration Release
dotnet run --project src-csharp/KnowledgeApp.RichCompatibilityCheck --configuration Release
dotnet run --project src-csharp/KnowledgeApp.BackupConfirmationCheck --configuration Release
dotnet run --project src-csharp/KnowledgeApp.RehearsalCheck --configuration Release
dotnet run --project src-csharp/KnowledgeApp.SingleInstanceCheck --configuration Release
dotnet run --project src-csharp/KnowledgeApp.UiBundleCheck --configuration Release
pwsh -NoProfile -File scripts/New-CSharpTrialPackage.ps1
```

パッケージ生成は毎回新しい`bin/pkg-{ランダム12桁}/app`へ発行し、成功時に検証済みフォルダーとZIPの場所を返します。既存パッケージを上書きしません。実行にNode.js・npm・ソースリポジトリは不要ですが、Windows 11 x64、.NET 10 Desktop Runtime x64、WebView2 Runtimeが必要です。依存環境を自動ダウンロード・導入しません。ローカルにある依存物の宣言・ライセンス情報を同梱しますが、利用環境のOSS・配布承認や署名の完了を意味しません。

同梱UIは静的ファイル一式のSHA-256、サイズ、完全なファイル集合を検査します。`PACKAGE-MANIFEST.json`はパッケージの整合性検査用であり、コード署名や出所の保証ではありません。単一起動37件・同梱UI13件の合成自動試験に合格しました。利用者確認ではログイン後の画面、未保存入力とタブ、モーダル中の二重起動、正常終了後の再起動を確認します。最小化解除・前面表示要求は行いますが、Windowsの前面化制限を強制的に解除しません。

第11段階の最終パッケージは160ペイロードファイルとマニフェストからなり、現行React UI 30ファイルの同一性、必要依存物、禁止ファイル・空フォルダーの拒否を確認しました。パッケージ回帰9件も合格しました。リポジトリ外へ新規配置後、Codexの実画面操作でログイン前の正常表示、別配置からの二重起動による最小化復元と同じウィンドウ・1プロセス維持を確認済みです。2026-09-05に利用者P1～P4すべての合格を受領し、この段階の画面受入を完了しました。後続ゲートが残るため本番切替は許可しません。

第12段階の専用自動試験74件、既存DataCheck全体、Codex 45件、Security 142件、MailCheck、単一起動37件、UI同梱13件、パッケージ19件が合格しました。React 188件は試験の初期分類設定待ちだけを補強し3回連続合格、型検査とUIビルドも合格しました。リポジトリ外での第12段階の正常な初期ログイン画面・継続保存／本番未接続表示を確認済みです。2026-09-05に利用者R1～R4の全合格を受領しました。

## 現在できること

第13段階はLegacyBackupCheck 716チェック、RehearsalCheck 74件、React 205件、DataCheck全体、Codex 45件、Security 145件、MailCheck、単一起動37件、UI同梱13件、パッケージ22件が合格しました。型検査・UIビルド・最終WPF publishも成功し、2026-09-05にB1～B4の利用者全合格を受領しました。旧版の量的制限を復元へ持ち込まず、本文の深さ128・応答144で閲覧まで検査しています。この時点で残った通常編集・複製・入出力の長文／深い構造と復元プレビュー対象固定を、第14段階で補完します。

- .NET 10 LTS、WPF、WebView2で現行のReact/CSSビルドを表示する
- 必須の同等性ゲートをJSONで列挙する
- 未移植項目が1件でもある間、`cutoverAllowed=false`かつ`deferred_until_parity`にする
- 既存の本番DBを開かず、互換先データルートの文字列だけを報告する
- WebViewからは`migration_probe`と、認証・利用者管理・分類・検索・同義語・FAQ詳細・FAQ編集・タグマスター・管理一覧・添付画像・外部URL・コピー・検索／閲覧履歴、固定CSV／JSON入出力、フルバックアップ・検査・復元、Codex提案・明示委譲・履歴・統合関係に限定した型付きメッセージだけを受け付ける
- 第12段階の実アプリはC#専用固定検証ルートへ保存・再起動後も保持する。自動試験は独立したOS一時合成ルートだけを使用する。本番DBと現行Tauri版の利用者データは開かない
- Rust版と同じ第1～7版マイグレーションSQLを埋め込み参照し、外部キー、WAL、パラメーター化SQL、トランザクション、移行前バックアップを使用する
- 初期管理者、Argon2id、通常認証・管理者復旧キー、利用停止、管理者・一般利用者境界、最後の管理者保護、パスワード再設定、空パスワード方針を扱う
- 1～5階層の分類CRUD、循環・6階層・同一親同名・使用中削除の拒否、同一親内の上下並べ替えを扱う
- 同義語CRUD、NFKC、日本語3文字分解、短語補助、FTS5 trigram候補抽出、項目別重み付け、一致理由、3検索範囲、50件ページング、下書き条件と非表示・削除済み・統合済み除外を扱う
- FAQ詳細の基本情報、Tiptap JSON、監査情報、旧検索情報、タグ、関連FAQ、削除・統合情報を読み取り、詳細取得と分離した明示要求で閲覧履歴を記録する
- 現行ReactのFAQ編集4区分を再利用し、合成DB内で新規作成、更新、複製、論理削除、復元、管理一覧、作成者・更新者、管理ID、タグ選択、FTS5再構築を扱う
- タグマスターの追加・名称変更・未使用削除を既存画面から扱う。NFKC・大小文字・空白正規化で同名を拒否し、1～100文字・1行を検証する。使用中タグの削除は件数と対処案内を返し、名称変更時はタグID、FAQ本文・監査・旧検索情報を維持して検索索引だけを同一トランザクションで更新する（C#合成24シナリオと利用者T1～T8の画面受入が合格）
- 通常編集では、現行画面から変更しない旧検索情報と既存関連FAQを削除・上書きしない。複製では旧検索情報、既存関連FAQ、タグを独立FAQへ引き継ぐ
- Tiptap本文は見出し、太字・斜体、箇条書き、番号付きリスト、表、管理画像、HTTP/HTTPS参考URL、コピー用テキストを許可構造として検証し、危険URL・未知ノード・外部／Base64画像を固定エラーで拒否する
- 画像選択はWPFのファイルダイアログ、画像貼り付けはBase64の型付き要求で受け、C#側で10MB上限、PNG・JPEG・WebP・GIFのマジックバイト、サイズ、SHA-256を検証する
- 保存前画像は合成データルートの一時領域に置き、FAQ保存成功時だけ確定する。DB失敗時は新規ファイルを戻し、本文から外した画像は保存成功後だけ削除する
- 添付付き複製は新しい画像UUIDと別ファイルを使用し、論理削除・復元では画像を保持する。第10段階ではWebView2のフォルダ公開をやめ、C#の資源応答で固定UUID配置の確定画像・一時画像だけを提供する
- 参考URLは確認画面の後にC#側でもHTTP/HTTPS、接続先、認証情報なし、2,048文字以下を再検証して既定ブラウザーへ渡す。コピー用テキストはプレーンテキストを書き込むだけで、クリップボード読取やパス実行を行わない
- 検索履歴は検索文・日付・0件のみ、閲覧履歴はFAQタイトル・元の検索文・日付で絞り込み、50件ずつ返す。履歴削除は検索／閲覧の固定対象と、検証済み期間または全件指定だけをトランザクションで扱う
- CSVは形式第2版の固定17列、UTF-8 BOM、CRLF、RFC 4180、内部UUID非出力、Excel数式無害化、回答本文ハッシュ、構造化本文保持、変更時の安全な段落置換を扱う
- JSONは形式第1版、UTF-8 BOMなし、未知項目拒否、分類・FAQ・タグ・同義語・関連・統合関係の厳格検査を扱い、画像、利用者・認証、履歴、設定を含めない
- C#ではCSV／JSON専用のWPF保存・選択ダイアログを使用し、絶対パス、固定複合拡張子、Git管理外を再検証する。取込前にmanifest・サイズ・SHA-256を検証した`.faqbackup`を作成し、単一トランザクションで反映する
- 設定画面の端末情報、緑・青の配色、タイトル分類表示、マスコット表示をC#の合成データルートと`app_settings`へ接続し、未保存時の現行既定値とバックアップ復元を維持する
- フルバックアップは管理者だけが操作でき、C#専用WPFダイアログで選んだ`.faqbackup`へDBスナップショット、管理添付画像、旧版互換`manuals`、設定を保存する。manifestの版・サイズ・SHA-256・DB整合性を検証し、Git配下、破損、未知形式、同名の無確認上書きを拒否する
- 復元前に現在状態を`safety-backups`へ退避し、DBと管理フォルダを検証済みステージから入れ替える。失敗時は復元前へ戻し、成功時は認証セッションを破棄する。バックアップ・CSV／JSONの9処理、Codex状態更新の7処理、タグ保存`save_tag`の計17処理は実際の応答まで待機し、試作ウィンドウの終了を保護する。`delete_tag`は通常の30秒期限を維持する
- Codex向け分類カタログ、修正1件・統合2～10件の明示委譲、提案一覧、承認・却下履歴、最新版再検討、統合公開後の確認付き関係作成・解除を合成ルートで扱う
- 提案1MB・委譲5MB、固定フォルダ、通常ファイル、UTF-8厳格JSON、受付IDとファイル名、未知項目、受付・承認時の委譲照合を検証する。修正画像のノード属性・位置・順序を保持し、新規・統合案は画像なしとする
- 承認はFAQ・必要な分類・索引・受付記録・履歴を単一トランザクションで確定する。修正は元FAQ版を照合し、分類・状態・フラグ・画像などを保持する。タイトル・概要の前後空白を除いて保存し、Rust履歴JSONと意味互換の比較を行う
- 同受付IDの異内容は保存済み履歴を置換せず個別に警告する。新規・統合は下書きのみ、修正は承認後のみ反映し、統合元の内容・状態・削除状態を自動変更しない
- 「① フォルダを選択」の後に「② .msgを確認」を利用者が押した場合だけ、選択フォルダ直下の`.msg`を1回走査する
- `.msg`は添付領域へ入らず標準MAPI項目を優先して読み、本文がない場合だけ互換解析を試し、部分欠落または除外理由を安全な固定コードで表示する
- 選択・確認・マスク済みメールだけを固定データルートの専用委譲JSONへ保存し、Codex用依頼文を発行する
- `.pst`、添付、未選択メール、原本パスは委譲しない。作成直後の委譲は利用者が明示的に破棄できる

CSV・JSON入出力はC#自動試験と利用者手動試験に合格しました。No.29で検出した再起動時の合成タグID差は固定ID化し、回帰試験と利用者再試験に合格しています。2026-09-05に、タイムアウト不合格項目の再試験と残りのフルバックアップ未実施項目の全合格報告を受領し、`backup_restore`ゲートも合格しました。Codex連携は合成自動試験45シナリオに加え、利用者U1～U7の全合格報告により画面受入を完了しています。ただし実プラグインとC#本番固定ルートの往復は未確認で、`codex_bridge`全体は未合格です。第10段階も利用者T1～T8の全合格報告を受領し、タグCRUD・画面回帰・終了確認の受入を完了、`tag_master`ゲートを合格としました。安全境界の本番環境を含む後続条件は未合格です。現行Tauri版主系、本番DB未接続、`cutoverAllowed=false`を維持します。

## 検証

```powershell
npm run build
dotnet build src-csharp/KnowledgeApp.CSharp/KnowledgeApp.CSharp.csproj
dotnet run --project src-csharp/KnowledgeApp.DataCheck/KnowledgeApp.DataCheck.csproj
dotnet run --project src-csharp/KnowledgeApp.CodexCheck/KnowledgeApp.CodexCheck.csproj
dotnet run --project src-csharp/KnowledgeApp.SecurityCheck/KnowledgeApp.SecurityCheck.csproj
dotnet run --project src-csharp/KnowledgeApp.MailCheck/KnowledgeApp.MailCheck.csproj
dotnet run --project src-csharp/KnowledgeApp.MigrationGate/KnowledgeApp.MigrationGate.csproj -- dist
dotnet run --project src-csharp/KnowledgeApp.CSharp/KnowledgeApp.CSharp.csproj
```

最後のコマンドはWindows上で試作ウィンドウを開きます。第12段階は専用検証データを保持するため、実FAQや本番DBを使わず、利用者が明示的に作成する合成内容だけで確認します。以下の件数は過去段階の受入履歴です。2026-08-30に認証・権限14項目、分類・検索・FTS5の26項目、FAQ詳細・最大10タブの18項目、FAQ編集・監査の23項目、リッチテキスト・添付画像・外部URL・コピー境界、検索・閲覧履歴20項目、CSV・JSON入出力33項目が利用者手動試験に合格しました。2026-09-05にフルバックアップ・復元とタイムアウト再試験、続いてCodex合成提案のU1～U7も全合格しました。実Codex往復や本番環境の安全境界の合格を意味するものではありません。

以後は利用者指示によりMarkdownの短い試験記録を基本とし、Excelを必須としません。自動試験、Codexの実画面操作、利用者確認を区別し、未実施と残る確認を明記します。個人記入用手順・結果はリポジトリ外の`Documents/KnowledgeApp_Test/outputs/{フェーズ名}`へ保存し、以前のExcel結果を上書きしません。

### 2026-09-05 ファイル選択・入出力のタイムアウト修正

画像、CSV、JSON、バックアップの専用ダイアログは、選択中の一律30秒期限を廃止し、選択またはキャンセルまで待機します。バックアップ作成・検査・復元とCSV／JSON書出し・検査・取込みも、実際の処理結果まで待機します。通常要求の30秒期限、確認画面、権限、パス検査、上書き・安全バックアップは変更しません。自動再試行や強制取消も追加しません。

起動中の旧試作版を停止・上書きしない修正版ビルド手順は次のとおりです。

```powershell
npm run build
dotnet build src-csharp/KnowledgeApp.CSharp/KnowledgeApp.CSharp.csproj --output src-csharp/KnowledgeApp.CSharp/bin/timeout-fix-20260905
```

再テストには`src-csharp/KnowledgeApp.CSharp/bin/timeout-fix-20260905/KnowledgeApp.CSharp.exe`を手動で起動します。フロントエンドは従来どおり同じリポジトリの`dist`を参照するため、exeだけを別フォルダへ移動しないでください。起動中の旧版へ修正は自動反映されません。旧版を閉じるとその起動で作成した合成DBが破棄され、修正版は新しい合成DBで開始します。

専用再テストExcelで45秒待機・取消・検証エラーの維持を確認する手順を提供し、2026-09-05に利用者から再試験と未実施項目を含めた全合格報告を受領しました。記入済み結果は保持します。`backup_restore`は合格ですが、後続のCodex連携などが残るため、本番切替は許可しません。

### 2026-09-05 Codex提案・委譲の移行試験

起動中の旧試作版を上書きしないビルド先は次のとおりです。

```powershell
npm run build
dotnet build src-csharp/KnowledgeApp.CSharp/KnowledgeApp.CSharp.csproj --output src-csharp/KnowledgeApp.CSharp/bin/codex-migration-20260905
dotnet run --project src-csharp/KnowledgeApp.CodexCheck/KnowledgeApp.CodexCheck.csproj
```

画面確認では`src-csharp/KnowledgeApp.CSharp/bin/codex-migration-20260905/KnowledgeApp.CSharp.exe`を起動します。exeは同じリポジトリの`dist`を参照するため、単体で別フォルダへ移動しないでください。旧版を閉じるとその合成DBは破棄され、新版は別の新規合成DBを使います。未保存の旧版を勝手に終了させません。

新規試作ルートには`CodexSyntheticUiFixture.Seed(database)`が「合成Codex新規案」「合成Codex修正案」「合成Codex統合案」と、固定合成FAQの委譲ファイルを一度だけ用意します。ログインや提案の履歴受付、元FAQの変更は行いません。ログイン後に「Codexからの提案」を開き、承認前確認、却下・再検討、新規下書き、修正前後比較、非破壊統合と公開後確認を実画面で検証します。既存・復元後のDBへ合成案を再投入しません。

現行のKnowledgeApp専用Codexプラグインは本番固定データルートを使用しており、この試作ルートには未接続です。試作画面が発行した依頼文を本番向けプラグインへ送らないでください。合成案だけでUIを確認し、メール原本や本番FAQを読みません。2026-09-05に利用者U1～U7の全合格を受領し、この段階の画面受入は完了しました。実プラグインとC#本番固定ルートの往復は未確認のため、`codex_bridge`全体は未合格のままです。

### 2026-09-05 タグマスター・ホスト／ファイル安全境界（第10段階）

現行タグマスターの`save_tag`・`delete_tag`をC#へ接続し、タグ選択とは別の必須`tag_master`ゲートで追跡します。関連FAQを手動編集する将来機能は復活させず、React/CSSのレイアウトも変更しません。タグC#合成24シナリオで認証、文字数・同名制約、使用件数、索引反映、本文・監査保持、失敗時の巻戻しに合格しました。既存タグ関連React試験8件、FileBoundaryの合成junction、実Dispatcher配線と`KnowledgeApp.DataCheck`全体、SecurityCheckの既存98シナリオ、CodexCheck再試験45シナリオも合格しています。終了制御32件とプロファイル照合12件を加えたSecurityCheck計142件もすべて合格しました。2026-09-05に利用者からT1～T8全項目合格の報告を受領し、`tag_master`ゲートを合格としました。T2は旧名と新名に共通する「合成移行」の一致であり、手順書の旧名称0件という期待値を訂正しました。検索処理は変更せず、完全一致化は後日相談とします。安全境界の本番環境確認は未実施のままです。

多数のFAQの索引更新で30秒の誤失敗・再試行を招かないよう、`save_tag`は実応答まで待機し終了を保護します。長時間入出力9件・Codex状態更新7件と合わせて固定17件だけが対象です。`delete_tag`は通常の30秒期限を維持します。

ホストは正規の`https://app.knowledge.local/index.html`とHashRouterだけを信頼し、送信元・要求形式・重複項目・ドキュメント世代を検証します。外部遷移・通信、子フレーム、新しいウィンドウ、ダウンロード、不要権限を拒否し、正規の静的資産・管理画像だけをC#の資源応答で提供します。CSPを適用し、遅延画面読込、マスコット、管理画像・貼り付け、確認後の参考URL表示は維持します。

WebView2のプロファイルとキャッシュはexe隣の既定フォルダを使わず、合成ルートの`temp/webview2-profile`へ明示保存します。`CreateAsync`後の`environment.UserDataFolder`を期待パスとI/Oなしで正規化比較し、不一致なら`EnsureCoreWebView2Async`前に固定`SYS-001`で停止します。未知のプロファイル、環境変数、レジストリは読取・変更しません。以前の`bin`配下に残っている既存プロファイルも自動削除しません。

`HostShutdownPolicy`に従い、`OnClosing`では固定17処理のガード後に終了を一時保留し、二重終了を抑止します。WebView2初期化と`BrowserProcessExited`を合計最大5秒まで待ってからDBを破棄し、安全を確認できた合成ルートだけを削除して最終終了します。`Browser.Dispose`直後のプロファイル削除は行いません。待機期限超過や再解析点・ロックなどによる削除拒否時は、合成一時データを残した旨を固定文言で通知し、kill・強制削除・別インスタンス操作をしません。

`FileSystemBoundary`は合成ルートのUUID・一時フォルダ直下条件、既存祖先と対象の再解析点、Gitへの迂回、デバイスパス・代替データストリームを検証します。通常のUNCバックアップは維持し、不正な保存先を作業フォルダへ代替しません。一時画像の整理は既知の生成名だけを対象とし、終了時の合成ルートを安全に検証できない場合は削除せず残します。同一OS利用者の悪意ある同時差替えを完全隔離する仕組みとは位置付けません。

保存済みバックアップ先は`ValidatePathSyntax`で通常UNCを含む構文だけを検証し、表示や設定取得から`Directory.Exists`などによる共有先確認を自動実行しません。実際のバックアップ操作時だけ、必要な実体・アクセス検査を行います。

起動中の旧試作を上書きしないビルド先は次のとおりです。

```powershell
npm run build
dotnet build src-csharp/KnowledgeApp.CSharp/KnowledgeApp.CSharp.csproj --output src-csharp/KnowledgeApp.CSharp/bin/security-migration-20260905-verified
dotnet run --project src-csharp/KnowledgeApp.DataCheck/KnowledgeApp.DataCheck.csproj
dotnet run --project src-csharp/KnowledgeApp.CodexCheck/KnowledgeApp.CodexCheck.csproj
dotnet run --project src-csharp/KnowledgeApp.SecurityCheck/KnowledgeApp.SecurityCheck.csproj
```

第10段階の最終確認用試作は`src-csharp/KnowledgeApp.CSharp/bin/security-migration-20260905-verified/KnowledgeApp.CSharp.exe`を起動します。ネイティブタイトル「KnowledgeApp C# 移行試作 — 第10段階」で中間版・既存2版と見分けてください。exeは同じリポジトリの`dist`を参照するため、単体で別フォルダへ移動しないでください。旧版を勝手に閉じたり未保存内容を破棄したりせず、新版は独立した合成DBで確認します。

試験記録はリポジトリ外の`Documents/KnowledgeApp_Test/outputs/csharp-tags-security-20260905/CSharp_タグ安全境界_試験記録.md`へ用意したT1～T8を使います。T1～T8は利用者報告により全項目合格として記録済みです。タグの画面受入、安全境界変更後のUI回帰、本番固定ルート・データコピー比較、実Codex往復、単一起動・配布、全体UI同等性は区別して記録します。未確認のゲートは未合格のままとし、本番DBへ接続しません。

## 固定した外部ライブラリ

- `Microsoft.Web.WebView2` 1.0.4129.50
- `Microsoft.Data.Sqlite` 10.0.11（MIT License）
- `Isopoh.Cryptography.Argon2` 2.0.0（CC0-1.0）
- `MsgReader` 6.1.0（MIT License）
- `OpenMcdf` 3.1.4（MPL-2.0 License）

`OpenMcdf`は添付ストレージを開かず標準MAPI項目だけを優先して読み取るために使用します。`MsgReader`は標準MAPI項目に本文がない場合の互換解析にだけ使用します。どちらもOutlookを起動せず、アプリから添付削除や原本保存の機能は呼び出しません。配布前にNuGet依存物を含むライセンス表示と利用環境のOSS利用規定を確認してください。
