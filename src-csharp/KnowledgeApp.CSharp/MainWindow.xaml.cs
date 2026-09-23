using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using KnowledgeApp.Data;
using KnowledgeApp.Migration;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace KnowledgeApp.CSharp;

public partial class MainWindow : Window
{
    private static readonly TimeSpan ShutdownWaitLimit = TimeSpan.FromSeconds(5);
    private KnowledgeDatabase? _database;
    private KnowledgeCommandDispatcher? _commandDispatcher;
    private AuthenticationService? _authentication;
    private StorageSettingsService? _storageSettings;
    private SharedHttpClient? _remote;
    private string DeviceDataRoot => _syntheticRoot ?? (_production ? ProductionDataRoot.FixedPath : RehearsalDataRoot.FixedPath);
    private readonly string? _syntheticRoot;
    private readonly Func<KnowledgeDatabase>? _openSyntheticDatabase;
    private readonly Action<string>? _startupStage;
    private readonly Action<string>? _startupNotice;
    private TemporaryBrowserStorage? _temporaryBrowserStorage;
    private int _activeCriticalOperations;
    private long _documentGeneration;
    private bool _bridgeDocumentReady;
    private bool _closed;
    private readonly HostShutdownPolicy _shutdown = new();
    private readonly TaskCompletionSource _initializationFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HashSet<uint> _exitedBrowserProcessIds = [];
    private TaskCompletionSource _browserExitSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CoreWebView2Environment? _webViewEnvironment;
    private uint? _webViewProcessId;
    private bool _initializationStarted;
    private bool _browserCreationStarted;
    private bool _browserDisposed;
    private readonly bool _production;

    public MainWindow(bool production = true)
    {
        _production = production;
        InitializeComponent();
        if (production)
        {
            Title = "KnowledgeApp C# 0.7.3";
            ModeLabel.Text = "KnowledgeApp C#";
            ModeDescription.Text = "C#専用の保存先を使用しています。旧版からの引継ぎは「設定・情報」のフルバックアップ復元で行えます。";
            ModeBanner.Background = System.Windows.Media.Brushes.WhiteSmoke;
            ModeBanner.BorderBrush = System.Windows.Media.Brushes.LightGray;
            ModeLabel.Foreground = System.Windows.Media.Brushes.DarkSlateGray;
            ModeDescription.Foreground = System.Windows.Media.Brushes.DarkSlateGray;
        }
        Loaded += OnLoaded;
    }

    public bool CanReceiveActivation => !_shutdown.IsClosing && !_closed;

    // Available only to the named startup test assembly. Neither launch arguments
    // nor the WebView bridge can choose a database path or enable measurements.
    internal MainWindow(string syntheticRoot, Func<KnowledgeDatabase> openDatabase, Action<string> startupStage, Action<string> startupNotice)
        : this(production: true)
    {
        _syntheticRoot = FileSystemBoundary.ValidateSyntheticRoot(syntheticRoot);
        _openSyntheticDatabase = openDatabase;
        _startupStage = startupStage;
        _startupNotice = startupNotice;
    }

    private async void OpenMailImport_Click(object sender, RoutedEventArgs e)
    {
        if (_remote is not null && !_shutdown.IsClosing && _bridgeDocumentReady)
        {
            try
            {
                var exchange = await _remote.PrepareCodexExchange();
                var remoteWindow = new MailImportWindow(new KnowledgeApp.Mail.MailDelegationWriter(() => exchange), _production) { Owner = this };
                remoteWindow.ShowDialog();
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, exception is AppProblemException problem ? problem.Problem.Message : "共有先の権限と接続を確認してください。", "メール履歴", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            return;
        }
        if (_database is null || _commandDispatcher is null || _shutdown.IsClosing || !_bridgeDocumentReady)
        {
            MessageBox.Show(this, "アプリの起動完了後に開いてください。", "メール履歴", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_authentication?.GetCurrentUser()?.Role is not (UserRoles.Admin or UserRoles.Editor))
        {
            MessageBox.Show(this, "管理者またはFAQ編集者でログインしてからメール履歴を開いてください。", "メール履歴", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var writer = _production
            ? RehearsalMailDelegation.CreateWriter(() => ProductionDataRoot.FixedPath)
            : RehearsalMailDelegation.CreateWriter();
        var window = new MailImportWindow(writer, _production) { Owner = this };
        window.ShowDialog();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        if (_shutdown.IsClosing || _closed)
        {
            _initializationFinished.TrySetResult();
            return;
        }
        _initializationStarted = true;
        try
        {
            var uiRoot = ResolveUiRoot();
            _startupStage?.Invoke("ui-verified");
            var runtimeAvailable = IsWebView2RuntimeAvailable();
            if (!runtimeAvailable)
            {
                throw new AppProblemException(new AppProblem(
                    "SYS-001",
                    "画面表示に必要なWebView2 Runtimeが見つかりません。",
                    "会社の導入ルールに従い、WebView2 Runtimeを確認してから起動してください。"));
            }
            var environment = await CreateBrowserEnvironmentAsync();
            if (_shutdown.IsClosing || _closed) return;
            // Start the native browser before the synchronous database work. The
            // browser stays on about:blank, with no bridge or navigation yet.
            // Keep DB ownership on this UI thread so closing cannot race a late
            // background database assignment after the shutdown deadline.
            var report = MigrationGateReport.Create(uiRoot, runtimeAvailable);
            var dataRoot = DeviceDataRoot;
            var browserInitialization = InitializeBrowserControlAsync(environment);
            try
            {
                var connection = SharedConnectionSettings.Read(dataRoot);
                if (connection is not null)
                {
                    _remote = new SharedHttpClient(connection, dataRoot);
                    // Remote entry points check the live administrator session before every use.
                    _storageSettings = new StorageSettingsService(dataRoot, () => { });
                    ModeLabel.Text = "KnowledgeApp 共有接続";
                    ModeDescription.Text = $"共有先: {connection.Url}（DBと画像は共有サーバー内で管理します）";
                }
                else
                {
                    _database = _openSyntheticDatabase?.Invoke() ?? (_production ? KnowledgeDatabase.OpenProduction() : KnowledgeDatabase.OpenRehearsal());
                    if (_production)
                    {
                        report = report with { ProductionDataOpened = true };
                        report.Validate();
                    }
                    if (_database.RecoveredInterruptedRestore)
                        ModeDescription.Text = "前回の復元が中断されたため、復元前の状態へ自動で戻しました。必要ならバックアップを選び直してください。" + ModeDescription.Text;
                    var authentication = new AuthenticationService(_database);
                    _authentication = authentication;
                    _storageSettings = new StorageSettingsService(dataRoot, authentication);
                    var attachments = new ArticleAttachmentService(dataRoot);
                    CodexLocationService.ActivateLocal(dataRoot);
                    var codex = new CodexProposalService(_database, authentication, attachments);
                    codex.RefreshCategoryCatalogBestEffort();
                    _commandDispatcher = new KnowledgeCommandDispatcher(
                        authentication,
                        new ClassificationSearchService(_database, authentication, codex.RefreshCategoryCatalogBestEffort),
                        new ArticleViewService(_database, authentication, attachments),
                        new ArticleEditingService(_database, authentication, attachments),
                        new HistoryService(_database, authentication),
                        new TransferService(_database, authentication, dataRoot),
                        new BackupService(_database, authentication, dataRoot, codex.RefreshCategoryCatalogBestEffort),
                        new SettingsService(_database, authentication, dataRoot),
                        codex,
                        SelectArticleImage,
                        SelectTransferSaveFile,
                        SelectTransferOpenFile,
                        Clipboard.SetText,
                        OpenExternalUrl,
                        SelectStorageFolder);
                }

                _startupStage?.Invoke("data-ready");
            }
            catch
            {
                // Observe native startup even when DB/catalog preparation fails,
                // so normal shutdown owns the browser PID and can release it.
                // Preserve the original data error if both preparations failed.
                try { await browserInitialization; } catch { }
                throw;
            }
            await browserInitialization;
            if (_shutdown.IsClosing || _closed) return;
            Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Browser.CoreWebView2.Settings.AreHostObjectsAllowed = false;
            Browser.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
            Browser.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
            ConfigureHostSecurity(new HostResourceFiles(uiRoot, Path.Combine(dataRoot, "attachments", "articles"), Path.Combine(dataRoot, "temp", "staged-article-images", "files")));
            Browser.CoreWebView2.WebMessageReceived += (_, args) => HandleWebMessage(args, report);

            var reportJson = HostResponseJson.Serialize(report);
            await Browser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                HostSecurityPolicy.BridgeInitializationScript(reportJson) +
                (_remote is null ? "" : "if (window.__KNOWLEDGE_CSHARP_BRIDGE__) window.__KNOWLEDGE_SHARED__ = true;"));
            if (_shutdown.IsClosing || _closed) return;

            Browser.CoreWebView2.Navigate("https://app.knowledge.local/index.html#/search");
            _startupStage?.Invoke("navigate");
            var pendingCount = report.Gates.Count(gate => gate.Required && !gate.Passed);
            StatusText.Text = _remote is not null ? "共有接続／サーバーの認証と権限を使用／通信断時のローカル切替なし" : _production
                ? "C# 0.7.3／本番用データ／起動前安全バックアップ作成済み"
                : $"C#検証用／継続確認 {pendingCount}項目／専用検証DB（終了後も保持）／本番データ未接続";
        }
        catch (Exception exception)
        {
            if (_shutdown.IsClosing || _closed) return;
            StatusText.Text = "C#互換ホストを起動できませんでした。";
            var safeMessage = exception is AppProblemException problem
                ? $"{problem.Problem.Code}：{problem.Problem.Message}\n{problem.Problem.Action}"
                : "SYS-001：必要なUIまたはC#専用DBを準備できませんでした。\nパッケージ全体とローカル保存先へのアクセスを確認してください。既存データは削除しないでください。";
            if (_startupNotice is not null) _startupNotice(safeMessage);
            else MessageBox.Show(
                $"C#互換ホストを起動できませんでした。\n\n{safeMessage}",
                "KnowledgeApp C#",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            // Release the DB/legacy lease through the normal bounded shutdown,
            // rather than leave an unusable window retaining production access.
            Close();
        }
        finally
        {
            _initializationFinished.TrySetResult();
        }
    }

    private async Task<CoreWebView2Environment> CreateBrowserEnvironmentAsync()
    {
        // Every process owns a new disposable browser profile, never the FAQ root.
        _temporaryBrowserStorage = TemporaryBrowserStorage.Create();
        var profile = _temporaryBrowserStorage.ProfileDirectory;
        _browserCreationStarted = true;
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
        if (!HostShutdownPolicy.IsExpectedProfileDirectory(environment.UserDataFolder, profile))
            throw new AppProblemException(AppProblem.System(
                "ブラウザーの保存先が今回の起動用の一時フォルダーと一致しません。"));
        _webViewEnvironment = environment;
        environment.BrowserProcessExited += OnBrowserProcessExited;
        _startupStage?.Invoke("environment-ready");
        return environment;
    }

    private async Task InitializeBrowserControlAsync(CoreWebView2Environment environment)
    {
        await Browser.EnsureCoreWebView2Async(environment);
        _webViewProcessId = Browser.CoreWebView2.BrowserProcessId;
        _startupStage?.Invoke("webview-ready");
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _bridgeDocumentReady = false;
        _documentGeneration++;
        if (_webViewEnvironment is not null)
        {
            _webViewEnvironment.BrowserProcessExited -= OnBrowserProcessExited;
            _webViewEnvironment = null;
        }
        // The asynchronous OnClosing path already performed the normal cleanup. These
        // disposals also cover a host shutdown that cannot be deferred by WPF.
        TryDisposeBrowser();
        TryDisposeDatabase();
        _remote?.Dispose();
        _database = null;
        _temporaryBrowserStorage = null;
        base.OnClosed(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        var decision = _shutdown.RequestClose(Volatile.Read(ref _activeCriticalOperations));
        if (decision == HostCloseDecision.AllowClose)
        {
            base.OnClosing(e);
            return;
        }
        e.Cancel = true;
        if (decision == HostCloseDecision.RejectCriticalOperation)
        {
            MessageBox.Show(
                "ファイルの入出力または復元を処理しています。完了後にアプリを閉じてください。",
                "KnowledgeApp C#",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }
        if (decision == HostCloseDecision.IgnoreDuplicate) return;
        _bridgeDocumentReady = false;
        _documentGeneration++;
        IsEnabled = false;
        StatusText.Text = "FAQデータを保持し、ブラウザーの終了と今回の一時データの整理を待っています。";
        _ = CompleteShutdownAsync();
    }

    private async Task CompleteShutdownAsync()
    {
        // Return from the first Closing event before issuing the final Close, and keep
        // WPF's dispatcher alive so BrowserProcessExited can be delivered.
        await Task.Yield();
        var cleanupSucceeded = false;
        try
        {
            var deadline = Task.Delay(ShutdownWaitLimit);
            var initializationCompleted = !_initializationStarted ||
                await CompletesBeforeDeadline(_initializationFinished.Task, deadline);
            var browserDisposed = TryDisposeBrowser();
            var browserReleased = browserDisposed && initializationCompleted &&
                await WaitForBrowserReleaseAsync(deadline);
            var databaseDisposed = TryDisposeDatabase();
            if (HostShutdownPolicy.MayDeleteTemporaryData(initializationCompleted, browserReleased, databaseDisposed))
            {
                cleanupSucceeded = _temporaryBrowserStorage?.TryCleanup() ?? true;
            }
        }
        catch
        {
            // No forced process termination and no fallback to another data directory.
            TryDisposeBrowser();
            TryDisposeDatabase();
        }
        finally
        {
            if (HostShutdownPolicy.NeedsResidualDataNotice(
                    _temporaryBrowserStorage is not null, cleanupSucceeded))
            {
                if (_startupNotice is not null) _startupNotice("Browser temporary storage was retained.");
                else MessageBox.Show(
                    "ブラウザーの終了または安全な削除を確認できなかったため、今回のブラウザー用一時データを残しました。\n" +
                    (_production
                        ? "本番用FAQの保存済みデータは保持されます。残るのは今回のブラウザー用一時領域です。アプリは終了します。"
                        : "C#専用の検証FAQは保持されます。本番データへの変更はありません。アプリは終了します。"),
                    "KnowledgeApp C#",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            _shutdown.CompleteShutdown();
            Close();
        }
    }

    private async Task<bool> WaitForBrowserReleaseAsync(Task deadline)
    {
        while (!HostShutdownPolicy.BrowserResourcesReleased(
                   true, _browserCreationStarted, _webViewProcessId, _exitedBrowserProcessIds))
        {
            if (_browserExitSignal.Task.IsCompleted)
            {
                _browserExitSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            if (!await CompletesBeforeDeadline(_browserExitSignal.Task, deadline)) return false;
        }
        return true;
    }

    private static async Task<bool> CompletesBeforeDeadline(Task operation, Task deadline)
    {
        var decision = HostShutdownPolicy.DecideWait(operation.IsCompleted, deadline.IsCompleted);
        if (decision == HostWaitDecision.Completed) return true;
        if (decision == HostWaitDecision.TimedOut) return false;
        return await Task.WhenAny(operation, deadline) == operation;
    }

    private void OnBrowserProcessExited(object? sender, CoreWebView2BrowserProcessExitedEventArgs e)
    {
        _exitedBrowserProcessIds.Add(e.BrowserProcessId);
        _browserExitSignal.TrySetResult();
    }

    private bool TryDisposeBrowser()
    {
        if (_browserDisposed) return true;
        try
        {
            Browser.Dispose();
            _browserDisposed = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryDisposeDatabase()
    {
        try
        {
            _database?.Dispose();
            _database = null;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async void HandleWebMessage(CoreWebView2WebMessageReceivedEventArgs args, MigrationGateReport report)
    {
        HostRequestEnvelope? request;
        string source;
        var generation = _documentGeneration;
        try
        {
            source = args.Source;
            if (_closed || _shutdown.IsClosing || !HostSecurityPolicy.MayReceiveRequest(
                    source, Browser.CoreWebView2.Source, _bridgeDocumentReady) ||
                !HostRequestEnvelope.TryParse(args.WebMessageAsJson, out request) || request is null)
            {
                return;
            }
        }
        catch
        {
            // A malformed message or closed WebView must never cross the command boundary.
            return;
        }

        if (request.Command == "migration_probe")
        {
            PostResponse(new HostResponse(request.Id, true, report, null), source, generation);
            return;
        }

        HostResponse response;
        try
        {
            var dispatcher = _commandDispatcher;
            var runsOnUiThread = request.Command is
                "select_storage_folder" or "select_article_image" or "write_clipboard_text" or
                "select_faq_csv_export_path" or "select_faq_csv_import_path" or
                "select_json_export_path" or "select_json_import_path" or
                "select_full_backup_destination" or "select_restore_backup_source";
            var critical = (HostOperationPolicy.PreventsWindowClose(request.Command) || _remote is not null);
            if (critical)
            {
                Interlocked.Increment(ref _activeCriticalOperations);
            }
            object? result;
            try
            {
                if (request.Command == "get_connection_settings")
                    result = new { settings = SharedConnectionSettings.Read(DeviceDataRoot), activeShared = _remote is not null };
                else if (request.Command == "save_connection_settings")
                {
                    if (_remote is null && _authentication?.GetCurrentUser() is not null) _authentication.RequireAdmin();
                    if (_remote is not null && await _remote.Execute("get_current_user", JsonSerializer.SerializeToElement(new { })) is not null) await _remote.RequireRole(admin: true);
                    var value = request.Args.GetProperty("input");
                    var settings = value.ValueKind == JsonValueKind.Null ? null : value.Deserialize<SharedConnectionSettings>(new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                    if (settings is not null) { using var candidate = new SharedHttpClient(settings); await candidate.VerifyServer(); }
                    SharedConnectionSettings.Save(DeviceDataRoot, settings);
                    result = new { restartRequired = true };
                }
                else if (_remote is not null) result = await ExecuteRemote(request.Command, request.Args);
                else if (dispatcher is null) throw new AppProblemException(AppProblem.System("C#サービスを開始できませんでした。"));
                else result = runsOnUiThread
                    ? dispatcher.Execute(request.Command, request.Args)
                    : await Task.Run(() => dispatcher.Execute(request.Command, request.Args));
            }
            finally
            {
                if (critical)
                {
                    Interlocked.Decrement(ref _activeCriticalOperations);
                }
            }
            response = new HostResponse(request.Id, true, result, null);
        }
        catch (AppProblemException exception)
        {
            response = new HostResponse(request.Id, false, null, exception.Problem);
        }
        catch
        {
            response = new HostResponse(
                request.Id,
                false,
                null,
                AppProblem.System("C#処理を完了できませんでした。"));
        }
        PostResponse(response, source, generation);
    }

    private void PostResponse(HostResponse response, string source, long generation)
    {
        try
        {
            if (_closed || _shutdown.IsClosing || !HostSecurityPolicy.MaySendResponse(
                    source, Browser.CoreWebView2.Source, _bridgeDocumentReady, generation, _documentGeneration))
            {
                return;
            }
            Browser.CoreWebView2.PostWebMessageAsJson(HostResponseJson.Serialize(response));
        }
        catch (InvalidOperationException)
        {
            // The WebView can close after the asynchronous operation has completed.
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // A stopped renderer must not receive a response or crash the native host.
        }
    }

    private void ConfigureHostSecurity(HostResourceFiles resources)
    {
        var core = Browser.CoreWebView2;
        core.NavigationStarting += (_, args) =>
        {
            if (!HostSecurityPolicy.IsTrustedDocument(args.Uri))
            {
                args.Cancel = true;
                return;
            }
            _bridgeDocumentReady = false;
            _documentGeneration++;
        };
        core.SourceChanged += (_, args) =>
        {
            if (args.IsNewDocument) _documentGeneration++;
            _bridgeDocumentReady = !_shutdown.IsClosing && !_closed && HostSecurityPolicy.IsTrustedDocument(core.Source);
        };
        core.FrameNavigationStarting += (_, args) => args.Cancel = true;
        core.NewWindowRequested += (_, args) => args.Handled = true;
        core.DownloadStarting += (_, args) => args.Cancel = true;
        core.PermissionRequested += (_, args) =>
        {
            args.State = CoreWebView2PermissionState.Deny;
            args.Handled = true;
            args.SavesInProfile = false;
        };

        // Virtual-host folder mappings bypass WebResourceRequested. Serve only these fixed
        // resources ourselves so the allowlist and CSP also cover local application content.
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All,
            CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += async (_, args) =>
        {
            var context = args.ResourceContext switch
            {
                CoreWebView2WebResourceContext.Document => HostResourceContext.Document,
                CoreWebView2WebResourceContext.Script => HostResourceContext.Script,
                CoreWebView2WebResourceContext.Stylesheet => HostResourceContext.Stylesheet,
                CoreWebView2WebResourceContext.Image => HostResourceContext.Image,
                CoreWebView2WebResourceContext.Font => HostResourceContext.Font,
                CoreWebView2WebResourceContext.Other => HostResourceContext.Other,
                _ => HostResourceContext.Blocked
            };
            var resource = args.RequestedSourceKind == CoreWebView2WebResourceRequestSourceKinds.Document
                ? HostSecurityPolicy.ResolveResource(args.Request.Uri, args.Request.Method, context)
                : null;
            // Dispose completes the WebView2 deferral. Calling Complete as well
            // completes it twice and terminates the WPF host with 0x8000000E.
            using var deferral = args.GetDeferral();
            try
            {
                if (resource is null) throw new IOException("Resource is not permitted.");
                if (_remote is null && resource.Root == HostResourceRoot.Attachments)
                    new ArticleViewService(_database!, _authentication!).RequireImageAccess(resource.RelativePath);
                else if (_remote is null && resource.Root == HostResourceRoot.StagedImages) _authentication!.RequireEditor();
                var bytes = _remote is not null && resource.Root != HostResourceRoot.Ui ? await _remote.ReadImage(resource) : resources.Read(resource);
                var headers = $"Content-Type: {resource.ContentType}\r\n" +
                    "X-Content-Type-Options: nosniff\r\nCache-Control: no-store\r\n" +
                    "Referrer-Policy: no-referrer\r\n" +
                    $"Content-Security-Policy: {HostSecurityPolicy.ContentSecurityPolicy}\r\n";
                // A memory stream has no OS file handle; WebView2 reads it asynchronously.
                args.Response = core.Environment.CreateWebResourceResponse(
                    new MemoryStream(args.Request.Method == "HEAD" ? [] : bytes, writable: false),
                    200, "OK", headers);
            }
            catch
            {
                // Never fall through to the network or expose a filesystem error to the page.
                args.Response = core.Environment.CreateWebResourceResponse(
                    new MemoryStream([], writable: false), 403, "Forbidden",
                    "Content-Type: text/plain; charset=utf-8\r\nX-Content-Type-Options: nosniff\r\nCache-Control: no-store\r\n");
            }
        };
    }

    private static bool IsWebView2RuntimeAvailable()
    {
        try
        {
            return !string.IsNullOrWhiteSpace(CoreWebView2Environment.GetAvailableBrowserVersionString());
        }
        catch (WebView2RuntimeNotFoundException)
        {
            return false;
        }
    }

    private async Task<object?> ExecuteRemote(string command, JsonElement args)
    {
        var remote = _remote!;
        if (command == "write_clipboard_text")
        {
            await remote.RequireRole();
            Clipboard.SetText(ExternalInteractionValidator.ValidateClipboardText(args.GetProperty("text").GetString()!));
            return null;
        }
        if (command == "open_external_url")
        {
            await remote.RequireRole();
            OpenExternalUrl(ExternalInteractionValidator.ValidateExternalUrl(args.GetProperty("url").GetString()!));
            return null;
        }
        if (command is "get_storage_folders" or "save_storage_folder" or "check_storage_folder")
        {
            await remote.RequireRole(admin: true);
            return await Task.Run<object?>(() => command switch
            {
                "get_storage_folders" => _storageSettings!.GetFolders(),
                "save_storage_folder" => _storageSettings!.Save(args.GetProperty("input").Deserialize<SaveStorageFolderInput>(new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!),
                _ => _storageSettings!.Check(args.GetProperty("input").Deserialize<SaveStorageFolderInput>(new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!)
            });
        }
        if (command.Contains("_server_storage_", StringComparison.Ordinal))
            return await remote.Execute(command.Replace("_server_storage_", "_storage_", StringComparison.Ordinal), args);
        if (command == "select_storage_folder") { await remote.RequireRole(admin: true); return SelectStorageFolder(); }
        if (command == "select_server_backup_folder")
        {
            await remote.RequireRole(admin: true);
            var text = Microsoft.VisualBasic.Interaction.InputBox("共有サーバーのWindowsから見たフォルダーパスを入力してください。ネットワーク共有はUNCパスを使用します。", "共有サーバーの保存先");
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        if (command == "select_article_image")
        {
            await remote.RequireRole(editor: true);
            var path = SelectArticleImage();
            if (path is null) return null;
            FileSystemBoundary.ValidatePath(path);
            var info = new FileInfo(path);
            if (info.Length > 10 * 1024 * 1024) throw new AppProblemException(AppProblem.System("画像は10MB以内にしてください。"));
            var bytes = await File.ReadAllBytesAsync(path);
            return await remote.Execute("stage_article_image_bytes", JsonSerializer.SerializeToElement(new { input = new { originalName = info.Name, bytesBase64 = Convert.ToBase64String(bytes) } }));
        }
        if (command.StartsWith("select_", StringComparison.Ordinal))
        {
            await remote.RequireRole(admin: true);
            return command switch
            {
                "select_faq_csv_export_path" => SelectTransferSaveFile("csv|" + args.GetProperty("defaultName").GetString()),
                "select_json_export_path" => SelectTransferSaveFile("json|" + args.GetProperty("defaultName").GetString()),
                "select_full_backup_destination" => SelectTransferSaveFile("backup|" + Path.GetFileName(args.GetProperty("defaultPath").GetString())),
                "select_faq_csv_import_path" => SelectTransferOpenFile("csv"),
                "select_json_import_path" => SelectTransferOpenFile("json"),
                "select_restore_backup_source" => SelectTransferOpenFile("backup"),
                _ => null
            };
        }
        if (command.Contains("codex", StringComparison.Ordinal) || command == "clear_article_merge") return await remote.ExecuteCodex(command, args);
        if (command is "export_faq_csv" or "inspect_faq_csv" or "import_faq_csv" or "export_json" or "inspect_json" or "import_json" or
            "create_full_backup" or "inspect_backup" or "restore_backup") return await remote.ExecuteFile(command, args);
        return await remote.Execute(command, args);
    }

    private static string? SelectStorageFolder()
    {
        var dialog = new OpenFolderDialog { Title = "リポジトリ外の保存先フォルダーを選択", Multiselect = false };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    private static string? SelectArticleImage()
    {
        var dialog = new OpenFileDialog
        {
            Title = "FAQへ追加する画像を選択",
            Filter = "画像（10MB以下）|*.png;*.jpg;*.jpeg;*.webp;*.gif",
            Multiselect = false,
            CheckFileExists = true
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private string? SelectTransferSaveFile(string request)
    {
        var separator = request.IndexOf('|', StringComparison.Ordinal);
        if (separator <= 0 || separator == request.Length - 1)
        {
            return null;
        }
        var kind = request[..separator];
        var defaultName = request[(separator + 1)..];
        var isBackup = kind == "backup";
        var requestedPath = isBackup && Path.IsPathFullyQualified(defaultName)
            ? Path.GetFullPath(defaultName)
            : null;
        var dialog = new SaveFileDialog
        {
            Title = kind switch
            {
                "csv" => "KnowledgeApp FAQ CSVの保存先を選択",
                "backup" => "フルバックアップの保存先と名前を選択",
                _ => "KnowledgeApp JSONの保存先を選択"
            },
            Filter = kind switch
            {
                "csv" => "KnowledgeApp FAQ CSV|*.knowledge-faq.csv",
                "backup" => "KnowledgeAppフルバックアップ|*.faqbackup",
                _ => "KnowledgeApp JSON|*.knowledge-export.json"
            },
            FileName = requestedPath is null ? defaultName : Path.GetFileName(requestedPath),
            InitialDirectory = _storageSettings?.ResolveForDialog($"{kind}-export", requestedPath is null ? null : Path.GetDirectoryName(requestedPath)) ?? string.Empty,
            AddExtension = true,
            OverwritePrompt = !isBackup
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private string? SelectTransferOpenFile(string kind)
    {
        var dialog = new OpenFileDialog
        {
            Title = kind switch
            {
                "csv" => "取り込むKnowledgeApp FAQ CSVを選択",
                "backup" => "復元するフルバックアップを選択",
                _ => "取り込むKnowledgeApp JSONを選択"
            },
            Filter = kind switch
            {
                "csv" => "KnowledgeApp FAQ CSV|*.knowledge-faq.csv",
                "backup" => "KnowledgeAppフルバックアップ|*.faqbackup",
                _ => "KnowledgeApp JSON|*.knowledge-export.json"
            },
            InitialDirectory = _storageSettings?.ResolveForDialog($"{kind}-import") ?? string.Empty,
            Multiselect = false,
            CheckFileExists = true
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static void OpenExternalUrl(Uri uri)
    {
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private static string ResolveUiRoot()
    {
        try
        {
            return UiBundleLocator.Resolve(AppContext.BaseDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new AppProblemException(new AppProblem(
                "SYS-001",
                "同梱された画面ファイルを安全に読み込めません。",
                "exeだけを移動せず、uiフォルダーを含む検証パッケージ全体を新しいフォルダーへ展開してください。開発時はUIビルド後にC#版を再ビルドしてください。"));
        }
    }

    private sealed record HostResponse(
        string Id,
        bool Ok,
        object? Result,
        AppProblem? Error);
}
