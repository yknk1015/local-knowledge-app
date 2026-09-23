using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using KnowledgeApp.CSharp;
using KnowledgeApp.Data;
using Microsoft.Web.WebView2.Wpf;

internal static class Program
{
    private const string ReadySignalJson = "\"knowledgeapp-startup-benchmark-ready\"";
    private const string StartupProbeScript = """
        (() => {
          if (window.__knowledgeStartupProbe) return;
          window.__knowledgeStartupProbe = true;
          const timer = window.setInterval(() => {
            const form = document.querySelector('.login-form');
            const inputs = form?.querySelectorAll('input');
            const button = form?.querySelector('button[type="submit"]');
            if (!form || !inputs || inputs.length < 2 || Array.from(inputs).some(input => input.disabled) ||
                !button || button.disabled || form.getBoundingClientRect().width <= 0) return;
            clearInterval(timer);
            requestAnimationFrame(() => requestAnimationFrame(() =>
              window.chrome.webview.postMessage('knowledgeapp-startup-benchmark-ready')));
          }, 10);
        })();
        """;

    [STAThread]
    private static int Main(string[] args)
    {
        // This executable is a developer check, never part of an app package.
        if (args is ["--check"]) return RunStartupChecks();
        if (args.Length != 1) return 2;
        var root = FileSystemBoundary.ValidateSyntheticRoot(args[0]);
        var watch = Stopwatch.StartNew();
        var stages = new Dictionary<string, double>();
        var shutdownWarnings = new List<string>();
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var failed = false;
        application.DispatcherUnhandledException += (_, e) =>
        {
            Console.Error.WriteLine(e.Exception.GetType().Name);
            failed = true;
            e.Handled = true;
            application.Shutdown(1);
        };
        application.Startup += (_, _) =>
        {
            var window = new MainWindow(root, () => KnowledgeDatabase.OpenProductionForTest(root),
                stage => stages[stage] = watch.Elapsed.TotalMilliseconds,
                notice =>
                {
                    if (notice == "Browser temporary storage was retained.")
                        shutdownWarnings.Add(notice);
                    else failed = true;
                    Console.Error.WriteLine(notice);
                })
            {
                // A normal rendered window, matching the legacy measurement.
                ShowActivated = false
            };
            var browser = (WebView2)window.FindName("Browser");
            var completed = false;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            void Fail(Exception exception)
            {
                failed = true;
                Console.Error.WriteLine(exception.GetType().Name + ": " + exception.Message);
                timer.Stop();
                window.Close();
            }
            browser.CoreWebView2InitializationCompleted += async (_, args) =>
            {
                try
                {
                    if (!args.IsSuccess) throw args.InitializationException ?? new IOException("Browser initialization failed.");
                    browser.CoreWebView2.WebMessageReceived += (_, message) =>
                    {
                        if (completed || (message.Source != "https://app.knowledge.local/index.html" &&
                            !message.Source.StartsWith("https://app.knowledge.local/index.html#", StringComparison.Ordinal)) ||
                            message.WebMessageAsJson != ReadySignalJson) return;
                        // Match the legacy IPC-receipt endpoint. The parent subtracts
                        // its pre-Process.Start UTC timestamp, excluding stdout delay.
                        var readyUnixMicroseconds = (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) / 10;
                        stages["login-ready"] = watch.Elapsed.TotalMilliseconds;
                        try
                        {
                            if (!File.Exists(Path.Combine(root, "data", "knowledge.db")) ||
                                !Directory.EnumerateFiles(Path.Combine(root, "safety-backups"), "*.faqbackup").Any())
                                throw new IOException("Production initialization/baseline was skipped.");
                            completed = true;
                            timer.Stop();
                            Console.WriteLine("READY " + JsonSerializer.Serialize(new { readyUnixMicroseconds, stages }));
                            Console.Out.Flush();
                            window.Close();
                        }
                        catch (Exception exception) { Fail(exception); }
                    };
                    // This is queued before the host's navigation. The completion
                    // fallback handles an already-created document without a second probe.
                    await browser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(StartupProbeScript);
                    browser.CoreWebView2.NavigationCompleted += async (_, _) =>
                    {
                        try { if (!completed) await browser.CoreWebView2.ExecuteScriptAsync(StartupProbeScript); }
                        catch (Exception exception) { if (!completed) Fail(exception); }
                    };
                }
                catch (Exception exception) { Fail(exception); }
            };
            timer.Tick += (_, _) =>
            {
                if (!completed && watch.Elapsed > TimeSpan.FromSeconds(60))
                    Fail(new TimeoutException("Login did not become ready."));
            };
            window.Closed += (_, _) =>
            {
                timer.Stop();
                Console.WriteLine("CLOSED " + JsonSerializer.Serialize(new { startupReady = completed, failed, shutdownWarnings }));
                Console.Out.Flush();
                application.Shutdown(completed && !failed ? 0 : 1);
            };
            timer.Start();
            window.Show();
        };
        return application.Run();
    }

    private static int RunStartupChecks()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var dispatcherFailure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        application.DispatcherUnhandledException += (_, e) =>
        {
            dispatcherFailure.TrySetResult(e.Exception);
            e.Handled = true;
        };
        application.Startup += async (_, _) =>
        {
            var exitCode = 1;
            try
            {
                foreach (var scenario in new[] { "login", "database-failure", "close-environment", "close-database" })
                    await CheckStartup(scenario, dispatcherFailure.Task);
                Console.WriteLine("PASS: four real-host startup lifecycle checks; only owned synthetic data used.");
                exitCode = 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"FAIL: {exception.GetType().Name}: {exception.Message}");
            }
            application.Shutdown(exitCode);
        };
        return application.Run();
    }

    private static async Task CheckStartup(string scenario, Task<Exception> dispatcherFailure)
    {
        var root = FileSystemBoundary.ValidateSyntheticRoot(Path.Combine(
            Path.GetTempPath(), $"knowledgeapp-csharp-search-{Guid.NewGuid():D}"));
        var watch = Stopwatch.StartNew();
        var stages = new Dictionary<string, double>();
        var notices = new List<string>();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MainWindow? window = null;
        KnowledgeDatabase? openedDatabase = null;
        string? browserRoot = null;
        var databaseCalled = false;
        var loginReady = false;
        string? databaseBefore = null;
        string[] existingBackups = [];
        try
        {
            if (scenario == "database-failure")
            {
                using (KnowledgeDatabase.OpenProductionForTest(root)) { }
                databaseBefore = Hash(Path.Combine(root, "data", "knowledge.db"));
                existingBackups = Directory.GetFiles(Path.Combine(root, "safety-backups"), "*.faqbackup");
            }
            window = new MainWindow(root, () =>
            {
                databaseCalled = true;
                if (scenario == "database-failure") throw new IOException("Synthetic database preparation failure.");
                openedDatabase = KnowledgeDatabase.OpenProductionForTest(root);
                if (scenario == "close-database")
                {
                    // A UI close request cannot interrupt this synchronous owner. Queue it
                    // while preparation is active, then let it run at the next async wait.
                    window!.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => window!.Close()));
                    Thread.Sleep(125);
                }
                return openedDatabase;
            }, stage =>
            {
                stages[stage] = watch.Elapsed.TotalMilliseconds;
                browserRoot ??= ReadField<TemporaryBrowserStorage>(window!, "_temporaryBrowserStorage")?.Root;
                if (scenario == "close-environment" && stage == "environment-ready") window!.Close();
            }, notice =>
            {
                notices.Add(notice);
                browserRoot ??= ReadField<TemporaryBrowserStorage>(window!, "_temporaryBrowserStorage")?.Root;
                if (notice == "Browser temporary storage was retained.") WriteShutdownState(window!, scenario, watch);
            })
            {
                ShowInTaskbar = false, Opacity = 0, Left = -30000, Top = -30000,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            window.Closed += (_, _) => closed.TrySetResult();
            var browser = (WebView2)window.FindName("Browser");
            window.Show();

            if (scenario == "login")
            {
                var deadline = DateTime.UtcNow.AddSeconds(30);
                while (DateTime.UtcNow < deadline && !closed.Task.IsCompleted)
                {
                    if (dispatcherFailure.IsCompleted) throw await dispatcherFailure;
                    if (browser.CoreWebView2 is not null && await browser.CoreWebView2.ExecuteScriptAsync(
                        "Boolean(document.querySelector('#login-title') && document.querySelector('input[type=password]') && document.querySelector('.login-card button[type=submit]:enabled'))") == "true")
                    {
                        loginReady = true;
                        break;
                    }
                    await Task.Delay(25);
                }
                Require(loginReady, "actual login did not become usable");
                Require(openedDatabase is not null && openedDatabase.OpenInfo.DatabasePath == Path.Combine(root, "data", "knowledge.db"),
                    "host changed its owned synthetic data root");
                Require(openedDatabase!.StartupSafetyBackup is not null && File.Exists(openedDatabase.StartupSafetyBackup.DestinationPath),
                    "startup safety backup was not completed before login");
                Require(new AuthenticationService(openedDatabase).GetCurrentUser() is null, "login was bypassed");
                Require(stages["navigate"] >= stages["data-ready"] && stages["navigate"] >= stages["webview-ready"],
                    "navigation preceded database/browser readiness");
                window.Close();
            }

            await closed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            if (dispatcherFailure.IsCompleted) throw await dispatcherFailure;
            var retained = notices.Contains("Browser temporary storage was retained.");
            // Environment creation can own native resources before a browser PID
            // exists. The pre-existing bounded shutdown deliberately retains that
            // profile when release cannot be proved; do not weaken that boundary.
            if (scenario == "close-environment" && retained)
                Require(browserRoot is not null && Directory.Exists(browserRoot), "unconfirmed browser storage was deleted");
            else
            {
                Require(browserRoot is not null && !Directory.Exists(browserRoot), "owned browser temporary storage was not cleaned");
                Require(!retained, "browser release could not be confirmed");
            }
            if (scenario != "login") Require(!stages.ContainsKey("navigate"), "an interrupted/failed startup navigated");
            if (scenario == "database-failure")
            {
                Require(databaseCalled && notices.Count == 1, "database failure was not reported exactly once");
                Require(Hash(Path.Combine(root, "data", "knowledge.db")) == databaseBefore, "failed preparation modified the existing synthetic DB");
                Require(Directory.GetFiles(Path.Combine(root, "safety-backups"), "*.faqbackup").Order().SequenceEqual(existingBackups.Order()),
                    "failed preparation replaced or created a safety backup");
            }
            else
            {
                Require(notices.Count == (scenario == "close-environment" && retained ? 1 : 0),
                    "normal/interrupted startup reported an unexpected problem");
                if (scenario == "close-environment") Require(!databaseCalled, "database opened after an early close");
            }
            if (openedDatabase is not null)
            {
                var disposed = false;
                try { openedDatabase.QuickCheckForTest(); }
                catch (ObjectDisposedException) { disposed = true; }
                Require(disposed, "closed host retained a live database owner");
                Require(File.Exists(Path.Combine(root, "data", "knowledge.db")), "shutdown deleted persistent synthetic data");
                using var reopened = KnowledgeDatabase.OpenProductionForTest(root);
                Require(!reopened.OpenInfo.Created, "saved database did not reopen after shutdown");
            }
            Console.WriteLine("PASS " + scenario + " " + JsonSerializer.Serialize(stages));
        }
        finally
        {
            if (window is not null && !closed.Task.IsCompleted)
            {
                window.Close();
                await Task.WhenAny(closed.Task, Task.Delay(TimeSpan.FromSeconds(12)));
            }
            // The browser owns a separate directory. Never delete a retained profile
            // or a DB that is still owned by a host whose close did not finish.
            if (window is null || closed.Task.IsCompleted) FileSystemBoundary.DeleteSyntheticRoot(root);
        }
    }

    private static T? ReadField<T>(MainWindow window, string name) =>
        (T?)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);

    private static void WriteShutdownState(MainWindow window, string scenario, Stopwatch watch)
    {
        Console.Error.WriteLine("SHUTDOWN " + JsonSerializer.Serialize(new
        {
            scenario,
            elapsedMs = watch.Elapsed.TotalMilliseconds,
            initializationFinished = ReadField<TaskCompletionSource>(window, "_initializationFinished")!.Task.IsCompleted,
            browserCreationStarted = ReadField<bool>(window, "_browserCreationStarted"),
            browserDisposed = ReadField<bool>(window, "_browserDisposed"),
            browserProcessKnown = ReadField<uint?>(window, "_webViewProcessId") is not null,
            browserExitSignal = ReadField<TaskCompletionSource>(window, "_browserExitSignal")!.Task.IsCompleted,
            exitedBrowserCount = ReadField<HashSet<uint>>(window, "_exitedBrowserProcessIds")!.Count
        }));
    }

    private static string Hash(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
