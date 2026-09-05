using System.Text.Json.Serialization;

namespace KnowledgeApp.Migration;

public sealed record MigrationGateItem(
    string Id,
    string Label,
    bool Required,
    bool Passed,
    string Detail);

public sealed record MigrationGateReport(
    int FormatVersion,
    string GeneratedAt,
    string TargetArchitecture,
    string DataRootCompatibilityTarget,
    bool ProductionDataOpened,
    IReadOnlyList<MigrationGateItem> Gates,
    bool ProductionUseAuthorized = false)
{
    private static readonly string[] RequiredGateIds =
    [
        "ui_bundle",
        "webview2_runtime",
        "authentication",
        "categories_search",
        "article_view_tabs",
        "article_editing",
        "tag_master",
        "rich_text_attachments",
        "sqlite_fts5_data",
        "history",
        "csv_json",
        "backup_restore",
        "codex_bridge",
        "mail_delegation",
        "security_boundaries",
        "single_instance_distribution",
        "manual_ui_parity"
    ];

    [JsonPropertyOrder(0)]
    public bool CutoverAllowed => ProductionUseAuthorized || AllFinalChecksPassed;

    public bool AllFinalChecksPassed => Gates.Count > 0 && Gates.Where(gate => gate.Required).All(gate => gate.Passed);

    public string AcceptanceScope => ProductionUseAuthorized
        ? "user-approved-small-scale-cutover"
        : "full-parity-review";

    [JsonPropertyOrder(1)]
    public string RecommendedPriority => ProductionUseAuthorized
        ? "csharp_primary"
        : AllFinalChecksPassed ? "ready_for_cutover_review" : "deferred_until_parity";

    public static MigrationGateReport Create(string uiRoot, bool webView2RuntimeAvailable)
    {
        var uiBundleAvailable = File.Exists(Path.Combine(uiRoot, "index.html"));
        var dataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "jp.local.webknowledgesystem.csharp");

        var report = new MigrationGateReport(
            FormatVersion: 2,
            GeneratedAt: DateTimeOffset.UtcNow.ToString("O"),
            TargetArchitecture: ".NET 10 LTS / C# / WPF / WebView2 / React / Tiptap / SQLite FTS5",
            DataRootCompatibilityTarget: dataRoot,
            ProductionDataOpened: false,
            // Fixed release policy approved by the owner on 2026-09-06, not a
            // renderer toggle or a claim that unperformed checks have passed.
            ProductionUseAuthorized: true,
            Gates:
            [
                new("ui_bundle", "現行React UI", true, uiBundleAvailable,
                    uiBundleAvailable ? "指定UIルートのindex.htmlを確認しました。WPFホストは同梱UI全体も検証します。" : "React UIとC#ホストをビルドし、同梱UIを準備してください。"),
                new("webview2_runtime", "WebView2 Runtime", true, webView2RuntimeAvailable,
                    webView2RuntimeAvailable ? "利用可能です。" : "会社PCのWebView2 Runtimeを確認してください。"),
                new("authentication", "認証・権限", true, true,
                    "C#実装・合成DB自動試験と、通常・緊急認証、利用者管理、管理者境界、合成DB破棄の利用者手動試験14項目が合格しました。"),
                new("categories_search", "分類・日本語検索", true, true,
                    "C#実装・合成DB自動試験と利用者手動試験26項目が合格しました。6階層化と循環は画面で無効な親候補を除外し、保存側でもCAT-003・CAT-006として拒否します。"),
                new("article_view_tabs", "FAQ表示・最大10タブ", true, true,
                    "C#のFAQ詳細・関連FAQ・閲覧記録API、合成DB自動試験と利用者手動試験18項目が合格しました。現行React/Tiptapの詳細表示、関連FAQ、最大10タブ、11件目拒否、画面別スクロール、再起動時破棄を確認しました。"),
                new("article_editing", "FAQ編集・監査", true, true,
                    "C#の新規作成・更新・複製・論理削除・復元・管理一覧・監査更新とFTS5再構築は合成DB自動試験および利用者手動試験23項目に合格しました。"),
                new("tag_master", "タグマスター追加・名称変更・削除", true, true,
                    "合成DB24試験、実Dispatcher、React画面8試験に加え、2026-09-05に利用者からT1～T8全項目合格の報告を受領しました。T2の旧名検索は共通部分「合成移行」による一致として受容し、旧名称0件という手順書の期待値を訂正しました。検索仕様は変更せず、タグマスターの画面受入を完了しました。"),
                new("rich_text_attachments", "Tiptap・画像・表・URL", true, true,
                    "C#へ許可済みTiptap構造、画像の選択・貼り付け・確定・除去、添付付き独立複製、論理削除・復元、HTTP/HTTPS再検証と既定ブラウザー委譲、コピー専用操作を移植し、合成DB自動試験と利用者手動試験に合格しました。"),
                Pending("sqlite_fts5_data", "既存SQLite・FTS5・DB移行",
                    "R1～R4、B1～B4、C1～C4、D1～D4は利用者全合格です。第17段階はC#専用rootへ分離し、明示バックアップ引継ぎ・排他・安全退避・中断復旧・旧root不変とC#/Rust往復を合成試験で確認しました。実データの件数・代表FAQは引継ぎ時に確認します。16MiB超要求の成功と通常JSON極深構造の逆取込は後続範囲です。旧版DBの直置き・自動同期はしません。"),
                new("history", "検索・閲覧履歴", true, true,
                    "検索・閲覧の明示記録、一覧、NFKC・日付・0件絞り込み、50件ページング、期間・全件削除をC#へ移植し、合成DB自動試験と利用者手動試験20項目に合格しました。"),
                new("csv_json", "CSV・JSON入出力", true, true,
                    "C#へ固定CSV・厳格JSON、WPF専用ダイアログ、Git配下拒否、プレビュー、SHA-256再検査、取込前安全バックアップ、単一トランザクションを移植し、合成DB・合成ファイル自動試験と利用者手動試験33項目に合格しました。再起動時の合成タグID差も固定UUID化して回帰確認済みです。"),
                new("backup_restore", "フルバックアップ・復元", true, true,
                    "C#実装・合成DB自動試験に加え、2026-09-05にタイムアウト再試験と残りの手動試験すべての合格報告を受領しました。作成・検査・復元、DB・画像・設定・認証復元、安全退避、復元後再ログインを確認済みです。第14段階は確認トークン・同一読取ハンドル・認証競合の112チェックと、追加手動C4を含むC1～C4も利用者全合格で受入完了です。"),
                Pending("codex_bridge", "Codex分類・委譲・提案",
                    "U1～U7とD1～D4は利用者合格済みです。C#固定root・PowerShell 7/5.1実コマンドの合成往復、日時原文保持、新規・修正・統合、承認前非変更、旧rootフォールバック拒否を自動確認しました。全体127コンテナを維持します。最新リポジトリスキルを使用し、外部タスクの古いプラグインは適用確認が必要です。登録済み実プラグイン・実会話接続は未確認です。"),
                new("mail_delegation", "Outlook明示委譲", true, true,
                    ".msgの自動試験と、明示走査・表示・マスク・委譲・破棄の利用者手動試験が合格しました。.pstは別ゲートで扱います。"),
                Pending("security_boundaries", "ファイル・URL・Git安全境界",
                    "T1～T8は利用者受入済みです。C#の権限・WebView・ファイル境界、範囲外要求の送信前拒否、復元途中停止からの復旧を合成試験で確認しました。正式版と検証・旧版のrootを分離します。会社PCの実環境確認は未実施です。同一ユーザープロセスの全ファイル競合・ハードリンク完全隔離を保証するものではありません。"),
                Pending("single_instance_distribution", "単一起動・利用者単位配布",
                    "P1～P4とR1～R4は利用者全合格です。C#専用root・利用者別起動排他・SQLite排他・C#専用NSISを使用し、旧Tauriを上書きしません。実行中の更新/削除拒否と利用者データ保持を維持します。小規模切替は利用者承認済みですが、会社環境の導入許可・実機確認は別途必要です。"),
                Pending("manual_ui_parity", "Windows手動UI同等性",
                    "各段階の利用者手動試験と共通React/Tiptap/CSSを維持します。最終実行物の会社環境・画面倍率を含む総合UI確認は未実施です。")
            ]);

        report.Validate();
        return report;
    }

    public void Validate()
    {
        var duplicates = Gates
            .GroupBy(gate => gate.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicates.Length > 0)
        {
            throw new InvalidOperationException($"同等性ゲートIDが重複しています: {string.Join(", ", duplicates)}");
        }

        var actualRequired = Gates
            .Where(gate => gate.Required)
            .Select(gate => gate.Id)
            .ToHashSet(StringComparer.Ordinal);
        var missing = RequiredGateIds.Where(id => !actualRequired.Contains(id)).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException($"必須の同等性ゲートがありません: {string.Join(", ", missing)}");
        }

        if (ProductionDataOpened && !CutoverAllowed)
        {
            throw new InvalidOperationException("利用承認も同等性完了もないC#版は本番データを開けません。");
        }
    }

    private static MigrationGateItem Pending(string id, string label) =>
        new(id, label, true, false, "C#移植と現行版比較が未完了です。");

    private static MigrationGateItem Pending(string id, string label, string detail) =>
        new(id, label, true, false, detail);
}
