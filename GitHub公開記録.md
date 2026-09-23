# GitHub公開記録

## 1. 初回公開の決定

| 項目 | 記録 |
|---|---|
| 決定日 | 2026-08-16 |
| GitHubアカウント | `yknk1015` |
| リポジトリ | `yknk1015/local-knowledge-app` |
| URL | `https://github.com/yknk1015/local-knowledge-app` |
| 公開範囲 | Public |
| 既定ブランチ | `main` |
| 対象版 | KnowledgeApp 0.4.4 本格利用候補版 |
| ライセンス | 初回公開時点ではライセンスファイルなし |

Publicで公開するのは、プログラム、設計文書、秘密を含まない設定例、実データを含まない開発用フィクスチャー、配布に必要な静的素材だけとする。利用者が作成したFAQ、DB（WAL・SHMを含む）、検索・閲覧履歴、添付画像、バックアップ、CSV・JSONエクスポート、診断ログ、Codex提案・委譲、アプリ外で管理する外部資料は公開しない。

実際のFAQ・データベース・パスワードなどの利用者データや認証情報は、公開・非公開を問わずGitHubを含むGitリポジトリへ絶対にコミットしない。APIキー・トークン・秘密鍵・緊急認証情報も対象とし、文書・ソース・画像への転記も禁止する。

この公開は、利用者本人による対象PCでの一人利用試行に向けたソース取得を目的とする。正式リリース、複数人配布、機密情報を含む運用の承認を意味しない。該当段階へ進む場合は利用環境の規定に従い、公開範囲、配布経路、コード署名を再確認する。

## 2. コミット作成者情報の保護

初回push前に、公開対象履歴14件の作成者・コミッター情報を次へ統一した。

- 表示名：`yknk1015`
- メール：`204838945+yknk1015@users.noreply.github.com`

書き換え前の個人名と個人メールアドレスは本記録へ残さない。書き換えによりコミットIDは変更されたが、先頭コミットのツリーID`fc618871f791b6c8a02c573bdc5bed922a15477f`が一致することを確認し、ファイル内容、コミットメッセージ、日時、並び順を維持した。

| No. | 書き換え前 | 書き換え後 | コミットメッセージ |
|---:|---|---|---|
| 1 | `b4413f9717ab0115f3dad3ffd85d02c9f36314ab` | `8313199c8c7ec4680b9811baedb0d8a8c6a023d3` | chore: establish initial KnowledgeApp baseline |
| 2 | `e8d64b87da21246aeb3381af5940a3699b265b91` | `76606b1bacd07ee667a8214a9582ff850b230ed5` | feat: add verified full backup and restore |
| 3 | `3b7ef1ffa2c6e6675dba851555804858ad3ab2a0` | `5ae94734ab891dd6ff44e66f6688febbaa25e155` | feat: add FAQ trash and restore workflow |
| 4 | `e5b966ac35fe172e0080eaf5e218ff711e24b242` | `9be3e4d8af0aa3456775be4a792626fbb210174c` | feat: complete safe category management |
| 5 | `f34ef5b017b4166b854dfb2b4ce8d702f28acddd` | `b681b7b412ca509c3212ebc944c0f29c8ab68383` | feat: add secure inline FAQ images |
| 6 | `f664e6444fad10221cdbd1eb9b7de7e94cd6de5c` | `ae3c3d99c7aed08db7126313b141eacdef48db18` | feat: add safe links and FAQ duplication |
| 7 | `b335c4474594e25a49d6417ea25d1d8a4ec03963` | `636d4038970ff8361acb2114de35e3e2a07336de` | feat: add FAQ display flags and visibility controls |
| 8 | `5f99114ecdc281e62f2c68386a1556034a88c4b6` | `1ebe18e0235e7d438e31f23cd799e81237e269aa` | feat: add Codex FAQ delegation workflow |
| 9 | `82e4d7c32f02f66b2dc05ece437140578c4eff62` | `6107ee3bc127c469559b5f0518a48973a677c140` | 開発中マスコット反映 |
| 10 | `ca6459bf75db1cd1846d9863a449a19ceb2667e3` | `e6053c34abbf937bc6d03c44409060857ac1c6e2` | feat: リリース前機能と外部資料参照を統合 |
| 11 | `87e33fef2270ca06e3494bf8ee13f83274e8926d` | `fe9254d51ac5b10c9e1aef22bc3e787a3b68b28f` | feat: 本格利用候補版の機能とリリース検証を整備 |
| 12 | `db66c6495524203cc0c8351d55bdcd28e2f5bdbb` | `704307815d8be0419986b2eaf1169e224782bce4` | docs: クリーンリリース検証結果を記録 |
| 13 | `cb3cc4dc953f15de2f9477ad78a55ac8ccf3c0ed` | `5d7a89528a8df46c92f1badad49454e719807245` | test: Surface実機リハーサルを準備 |
| 14 | `0884695275efd9637311d07ab89c5f4a2ae2e04c` | `731e5a3b86d3fec17faa7811730a7438e7969b96` | プレリリース |

## 3. 初回公開前の確認結果

2026-08-16に次を確認した。

- React画面テスト：91件合格
- Rustテスト：84件合格、リリース性能試験1件合格
- TypeScript型検査、Viteビルド、Tauri 2リリースビルド：合格
- UTF-8（BOMなし）・LF検査：合格
- 利用者データ・配布禁止ファイル混入検査：合格
- Codexプラグイン境界検査：合格
- NSIS利用者単位インストール・配布設定検査：合格
- ローカル再生成インストーラー：8,439,716 bytes、SHA-256 `0F081C312174362FD72CE9EECB9ACC96C24E3ED24003E8E062C41847721A0BFA`、`NotSigned`

ローカル再生成インストーラーはGitへ含めない。GitHub Actionsで生成される成果物は、その実行で記録されたSHA-256と照合する。

### 2026-09-06 復旧キーの保護

C# 0.6.0の復旧キー・その検証ハッシュ・一時再設定トークンも認証情報として扱い、実際のFAQ・DB・パスワードとともにGitHubを含むGitへ絶対にコミットしない。合成専用の固定入力以外をテストへ転記しない。本改修でコミット・push・公開は行っていない。
