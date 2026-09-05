using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KnowledgeApp.Data;

internal static partial class FinalInteropCheck
{
    private static readonly List<string> Roots = [];
    private static int _passed;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static bool _withLegacy;

    private static async Task<int> Main(string[] args)
    {
        try
        {
            _withLegacy = args.SequenceEqual(new[] { "--with-legacy" });
            if (args.Length != 0 && !_withLegacy) throw new ArgumentException("Only --with-legacy is accepted; no real data paths are accepted.");
            if (_withLegacy) _ = LegacySource.Resolve();
            CheckProductionRouteContracts();
            if (_withLegacy) await CheckBackupRoundtrip();
            else Console.WriteLine("NOT RUN: Rust cross-runtime backup checks. Use --with-legacy and an explicitly selected legacy source to run them.");
            await CheckPluginCommands("pwsh");
            await CheckPluginCommands(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"));
            Console.WriteLine($"FinalInteropCheck: {_passed} checks passed; generated synthetic data only. Plugin command integration is not an installed-plugin/AI-session acceptance test.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FinalInteropCheck failed: " + exception);
            return 1;
        }
        finally
        {
            foreach (var root in Roots)
            {
                try { FileSystemBoundary.DeleteSyntheticRoot(root); }
                catch (Exception exception) { Console.Error.WriteLine("Owned synthetic cleanup failed: " + exception.GetType().Name); }
            }
        }
    }

    private static string NewRoot()
    {
        var root = FileSystemBoundary.ValidateSyntheticRoot(Path.Combine(Path.GetTempPath(), $"knowledgeapp-data-check-{Guid.NewGuid():D}"));
        if (Directory.Exists(root) || File.Exists(root)) throw new IOException("Synthetic root collision.");
        Roots.Add(root);
        return root;
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        _passed++;
        Console.WriteLine("PASS: " + label);
    }

    private static string Checkout()
    {
        var ancestor = new DirectoryInfo(AppContext.BaseDirectory);
        while (ancestor is not null && !File.Exists(Path.Combine(ancestor.FullName, "src-csharp", "KnowledgeApp.Data", "KnowledgeApp.Data.csproj"))) ancestor = ancestor.Parent;
        return ancestor?.FullName ?? throw new InvalidOperationException("Run from the source checkout.");
    }

    private static async Task<(int ExitCode, string Output, string Error)> Run(ProcessStartInfo info, string? input = null)
    {
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.StandardOutputEncoding = Utf8;
        info.StandardErrorEncoding = Utf8;
        info.RedirectStandardInput = input is not null;
        if (input is not null) info.StandardInputEncoding = Utf8;
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start fixed synthetic check.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (input is not null)
        {
            await process.StandardInput.WriteAsync(input);
            process.StandardInput.Close();
        }
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout, await stderr);
    }

    private static JsonDocument Body(string text) => JsonDocument.Parse(JsonSerializer.Serialize(new
    {
        type = "doc", content = new[] { new { type = "paragraph", content = new[] { new { type = "text", text } } } }
    }));

    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static void ExpectProblem(Action action, string code, string label)
    {
        try { action(); throw new InvalidOperationException("Expected rejection: " + label); }
        catch (AppProblemException exception) when (exception.Problem.Code == code) { Check(true, label); }
    }
}
