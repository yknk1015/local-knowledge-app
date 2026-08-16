# KnowledgeApp 0.4.3 Surface検証パッケージ

## 最初に行うこと

1. ZIP外側の`.sha256`とZIP本体のSHA-256を照合します。
2. ZIPをSurfaceのローカルフォルダへ展開します。
3. 展開先で`verify-surface-release-package.ps1`を実行します。
4. 検証がPASSしたら、`Surface実機テスト計画書_0.4.3.md`を確認します。
5. `Surface実機リハーサル手順_0.4.3.md`の順に操作し、`Surface実機試験結果_0.4.3.md`へ記録します。

## 重要な安全条件

- このパッケージは会社FAQや個人FAQを含まない試験専用品です。
- インストーラー3件は未署名です。会社または端末管理ルールで許可されたSurfaceだけで使用してください。
- セキュリティ警告、アプリ実行制限、ウイルス対策を回避して実行しないでください。
- 0.2.0と0.3.4は段階更新・互換性試験専用です。正式配布しないでください。
- パスワード、会社FAQ、個人情報、会社の機密パスを結果票へ記載しないでください。
- Surfaceで生成したDB、バックアップ、CSV、JSON、検索・閲覧履歴を開発PCへ戻さないでください。

## パッケージ内の主なファイル

| ファイル | 用途 |
|---|---|
| `KnowledgeApp_0.2.0_x64-setup.exe` | 旧DB・画像を作る更新試験専用旧版 |
| `KnowledgeApp_0.3.4_x64-setup.exe` | 旧検索情報・旧関連FAQ・変更済みパスワードを作る中間版 |
| `KnowledgeApp_0.4.3_x64-setup.exe` | 本格利用候補版 |
| `SHA256SUMS.txt` | 展開後ファイルのハッシュ一覧 |
| `verify-surface-release-package.ps1` | ハッシュ・署名・端末環境の検査 |
| `knowledgeapp-test-image.png` | 画像保存用の合成画像 |
| `external-reference-test.html` | 外部HTML参照用の合成ファイル |
| `six-digit-management-id.fixture.json` | 6桁管理ID確認用の合成JSON |
| `New-KnowledgeAppLargeCsvFixture.ps1` | 10,000件CSV生成用 |

不一致やP0不具合を検出した場合は操作を続けず、結果票へ記録して開発側へ返却してください。



