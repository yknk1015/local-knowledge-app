using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using KnowledgeApp.CSharp;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

// Exercise the actual WPF resource handler and built React UI with real WebView2.
// Never run App.OnStartup/OnLoaded: no production or persistent rehearsal DB opens.
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        application.DispatcherUnhandledException += (_, e) =>
        {
            failure.TrySetResult(e.Exception);
            e.Handled = true; // Report a failed test and clean up the owned browser.
        };
        application.Startup += async (_, _) =>
        {
            var exitCode = 1;
            try
            {
                await Run(args, failure.Task).WaitAsync(TimeSpan.FromSeconds(60));
                if (failure.Task.IsCompleted) throw await failure.Task;
                Console.WriteLine("PASS: real WebView2 startup, reload, denied resources and browser shutdown; no user DB opened.");
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

    private static async Task Run(string[] args, Task<Exception> failure)
    {
        var ui = args.Length == 1 ? Path.GetFullPath(args[0]) : Path.Combine(AppContext.BaseDirectory, "ui");
        if (!File.Exists(Path.Combine(ui, "index.html"))) throw new IOException("Built UI is missing.");
        var storage = TemporaryBrowserStorage.Create();
        Console.WriteLine("Startup check: create isolated WPF window (no DB initialization).");
        var window = new MainWindow(production: false)
        {
            ShowInTaskbar = false, Opacity = 0, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -30000, Top = -30000
        };
        var loaded = typeof(MainWindow).GetMethod("OnLoaded", BindingFlags.Instance | BindingFlags.NonPublic,
            [typeof(object), typeof(RoutedEventArgs)])!;
        window.Loaded -= (RoutedEventHandler)loaded.CreateDelegate(typeof(RoutedEventHandler), window);
        var browser = (WebView2)window.FindName("Browser");
        var browserExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            window.Show();
            Console.WriteLine("Startup check: create WebView2 environment.");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: storage.ProfileDirectory);
            environment.BrowserProcessExited += (_, _) => browserExited.TrySetResult();
            await browser.EnsureCoreWebView2Async(environment);
            Console.WriteLine("Startup check: register production resource handler.");
            var core = browser.CoreWebView2;
            var resourcesType = typeof(MainWindow).Assembly.GetType("KnowledgeApp.CSharp.HostResourceFiles")!;
            var resources = Activator.CreateInstance(resourcesType, ui,
                Path.Combine(storage.Root, "attachments"), Path.Combine(storage.Root, "staged"));
            typeof(MainWindow).GetMethod("ConfigureHostSecurity", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [resources]);
            await core.AddScriptToExecuteOnDocumentCreatedAsync(HostSecurityPolicy.BridgeInitializationScript("{}"));
            // Only initial unauthenticated UI state is synthetic. Real host resource handling stays intact.
            core.WebMessageReceived += (_, e) =>
            {
                using var message = JsonDocument.Parse(e.WebMessageAsJson);
                var request = message.RootElement;
                var command = request.GetProperty("command").GetString();
                object? result = command == "get_connection_settings" ? new { settings = (object?)null, activeShared = false } : null;
                core.PostWebMessageAsJson(JsonSerializer.Serialize(new
                {
                    id = request.GetProperty("id").GetString(), ok = command is "get_current_user" or "get_connection_settings", result
                }));
            };
            var responses = new List<int>();
            core.WebResourceResponseReceived += (_, e) => responses.Add(e.Response.StatusCode);
            for (var run = 0; run < 3; run++)
            {
                Console.WriteLine($"Startup check: load login UI {run + 1}.");
                var navigated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                void NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e) => navigated.TrySetResult(e.IsSuccess);
                core.NavigationCompleted += NavigationCompleted;
                try
                {
                    if (run == 0) core.Navigate(HostSecurityPolicy.DocumentUrl + "#/search");
                    else core.Reload();
                    await Until(() => Task.FromResult(navigated.Task.IsCompleted), failure);
                    if (!await navigated.Task) throw new Exception("Built UI navigation failed.");
                }
                finally { core.NavigationCompleted -= NavigationCompleted; }
                await Until(async () => await core.ExecuteScriptAsync(
                    "Boolean(document.querySelector('#login-title') && document.querySelector('input[type=password]') && getComputedStyle(document.querySelector('.login-card')).backgroundColor !== 'rgba(0, 0, 0, 0)')") == "true", failure);
                await Task.Delay(250);
                if (failure.IsCompleted) throw await failure;
            }
            if (responses.Count(status => status == 200) < 6) throw new Exception("UI resources were not served.");
            await core.ExecuteScriptAsync("let denied = document.createElement('script'); denied.src = '/assets/missing-startup-check.js'; document.head.append(denied);");
            await Until(() => Task.FromResult(responses.Contains(403)), failure);
            if (await core.ExecuteScriptAsync("Boolean(document.querySelector('#login-title'))") != "true")
                throw new Exception("Denied resource destroyed the login UI.");
            if (failure.IsCompleted) throw await failure;
        }
        finally
        {
            browser.Dispose();
            window.Close();
            await Task.WhenAny(browserExited.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            if (!browserExited.Task.IsCompleted) throw new TimeoutException("Synthetic browser did not exit.");
            if (!storage.TryCleanup())
                throw new IOException("Synthetic browser storage cleanup failed.");
        }
    }

    private static async Task Until(Func<Task<bool>> ready, Task<Exception> failure)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (failure.IsCompleted) throw await failure;
            if (await ready()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException("The actual login UI/resource response did not become ready.");
    }
}
