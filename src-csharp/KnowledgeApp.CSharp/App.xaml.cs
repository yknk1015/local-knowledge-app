using System.Windows;
using System.Windows.Threading;

namespace KnowledgeApp.CSharp;

public partial class App : Application
{
    private SingleInstanceCoordinator? _instance;
    private InstallationActivityGuard? _installation;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            _installation = InstallationActivityGuard.AcquireForCurrentExecutable();
            if (!CandidateLaunchPolicy.TrySelect(e.Args, out var production))
                throw new InvalidOperationException("Unsupported launch arguments.");
            var identity = production ? SingleInstanceIdentity.ForProduction() : SingleInstanceIdentity.ForCurrentUser();
            _instance = SingleInstanceCoordinator.TryAcquire(identity, ActivateExistingWindowAsync);
            if (_instance is null)
            {
                var reply = await SingleInstanceCoordinator.RequestActivationAsync(identity, grantForeground: true);
                if (reply != SingleInstanceReply.Activated)
                {
                    MessageBox.Show(SingleInstancePolicy.Notice(reply), "KnowledgeApp C#",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                Shutdown();
                return;
            }

            // C# production has its own fixed root. Never open or share the legacy
            // Tauri database; migration uses a user-selected full backup in the UI.
            // Only the OS lease owner may construct the DB/WebView. The sole mode
            // argument is never forwarded as a path or command to another process.
            var window = new MainWindow(production);
            MainWindow = window;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Show();
        }
        catch
        {
            MessageBox.Show("C#版の起動排他または画面を準備できませんでした。既存のC#版を確認し、終了後にもう一度起動してください。検証用の起動引数は --rehearsal だけです。",
                "KnowledgeApp C#", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private async Task<SingleInstanceReply> ActivateExistingWindowAsync(CancellationToken cancellationToken)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return SingleInstanceReply.Closing;
        return await Dispatcher.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return MainWindow is MainWindow window
                ? SingleInstanceWindowActivation.Restore(window)
                : SingleInstanceReply.Starting;
        }, DispatcherPriority.Normal, cancellationToken).Task;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        _instance = null;
        _installation?.Dispose();
        _installation = null;
        base.OnExit(e);
    }
}
