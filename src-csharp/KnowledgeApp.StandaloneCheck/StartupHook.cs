using System.Data.Common;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

// Loaded only by the packaging test's child-process DOTNET_STARTUP_HOOKS.
// Exit before WPF App.OnStartup: neither fixed user database is opened.
public static class StartupHook
{
    public static void Initialize()
    {
        try
        {
            var testRoot = Path.GetFullPath(Environment.GetEnvironmentVariable("KNOWLEDGEAPP_STANDALONE_TEST_ROOT")
                ?? throw new InvalidOperationException("Missing test root."));
            var temporary = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
            var leaf = Path.GetFileName(testRoot);
            const string prefix = "knowledgeapp-data-check-";
            Require(Path.GetDirectoryName(testRoot) == temporary && leaf.StartsWith(prefix, StringComparison.Ordinal) &&
                Guid.TryParseExact(leaf[prefix.Length..], "D", out _), "Invalid synthetic test root.");
            var extracted = Path.GetFullPath(AppContext.BaseDirectory);
            var extractRoot = Path.Combine(testRoot, "extract") + Path.DirectorySeparatorChar;
            Require(extracted.StartsWith(extractRoot, StringComparison.OrdinalIgnoreCase), "Assets were not extracted to the owned test root.");
            Require(typeof(object).Assembly.Location.StartsWith(extracted, StringComparison.OrdinalIgnoreCase),
                "The application did not use its bundled .NET runtime.");
            Require(File.Exists(Path.Combine(extracted, "WebView2Loader.dll")), "Missing WebView2 native loader.");
            Require(File.Exists(Path.Combine(extracted, "STANDALONE-README.txt")) &&
                File.Exists(Path.Combine(extracted, "notices", "dependency-inventory.json")), "Missing bundled distribution notices.");

            var host = Assembly.Load("KnowledgeApp.CSharp");
            var data = Assembly.Load("KnowledgeApp.Data");
            var storage = data.GetType("KnowledgeApp.Data.StorageSettingsService", true)!;
            var validateDestination = storage.GetMethod("ValidateScopedDestination")!;
            var outsideExecutableDirectory = Path.GetDirectoryName(Environment.ProcessPath!)!;
            var isolatedDataRoot = Path.Combine(temporary, prefix + Guid.NewGuid().ToString("D"));
            foreach (var forbidden in new[] { extracted, outsideExecutableDirectory })
            {
                var refused = false;
                try { validateDestination.Invoke(null, [isolatedDataRoot, forbidden, "backup-export"]); }
                catch (TargetInvocationException exception) when (exception.InnerException?.GetType().Name == "AppProblemException")
                { refused = true; }
                Require(refused, "The packaged app did not protect both executable directories.");
            }
            // A remembered exchange location must be rechecked after the exe moves.
            // Missing owner files deliberately distinguish a boundary refusal from
            // the generic invalid-configuration error that an old build would give.
            var settings = Path.Combine(testRoot, "device-settings");
            Directory.CreateDirectory(settings);
            var locationConfig = Path.Combine(settings, "codex-local-location.json");
            var resolveExchange = data.GetType("KnowledgeApp.Data.CodexLocationService", true)!.GetMethod("ResolveExchangeRoot")!;
            foreach (var forbidden in new[] { extracted, outsideExecutableDirectory })
            {
                File.WriteAllText(locationConfig, JsonSerializer.Serialize(new
                {
                    version = 1, environmentId = Guid.NewGuid().ToString("D"), generation = 1, root = forbidden
                }));
                var refusedBeforeOwnerRead = false;
                try { resolveExchange.Invoke(null, [testRoot]); }
                catch (TargetInvocationException exception) when (exception.InnerException?.GetType().Name == "AppProblemException")
                {
                    refusedBeforeOwnerRead = exception.InnerException.Message.StartsWith(
                        "CDX-024: Codex連携先がアプリの実行フォルダーと重複しています。", StringComparison.Ordinal);
                }
                Require(refusedBeforeOwnerRead, "Remembered Codex location was not refused before owner access.");
            }
            File.Delete(locationConfig);
            var locator = host.GetType("KnowledgeApp.CSharp.UiBundleLocator", throwOnError: true)!;
            var ui = (string)locator.GetMethod("Resolve")!.Invoke(null, [extracted])!;
            Require(ui == Path.Combine(extracted, "ui"), "Unexpected UI location.");
            var policy = host.GetType("KnowledgeApp.CSharp.CandidateLaunchPolicy", throwOnError: true)!;
            object?[] normal = [Array.Empty<string>(), false];
            Require((bool)policy.GetMethod("TrySelect")!.Invoke(null, normal)! && (bool)normal[1]!, "Default mode changed.");
            object?[] rehearsal = [new[] { "--rehearsal" }, true];
            Require((bool)policy.GetMethod("TrySelect")!.Invoke(null, rehearsal)! && !(bool)rehearsal[1]!, "Rehearsal mode changed.");

            // A memory database exercises the bundled native SQLite library only.
            var sqlite = Assembly.Load("Microsoft.Data.Sqlite").GetType("Microsoft.Data.Sqlite.SqliteConnection", true)!;
            using (var connection = (DbConnection)Activator.CreateInstance(sqlite, "Data Source=:memory:")!)
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT 1";
                Require(Convert.ToInt64(command.ExecuteScalar()) == 1, "Bundled SQLite failed.");
            }

            var destination = Path.Combine(testRoot, "io", "backup-export");
            Directory.CreateDirectory(destination);
            using var probe = new Process { StartInfo = new(Path.Combine(extracted, "KnowledgeApp.PathProbe.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            } };
            probe.StartInfo.Environment.Remove("DOTNET_STARTUP_HOOKS");
            probe.StartInfo.Environment.Remove("KNOWLEDGEAPP_STANDALONE_TEST_ROOT");
            // Do not let an installed runtime hide a framework-dependent helper.
            probe.StartInfo.Environment["DOTNET_ROOT"] = Path.Combine(testRoot, "no-installed-runtime");
            probe.StartInfo.Environment["DOTNET_ROOT_X64"] = Path.Combine(testRoot, "no-installed-runtime");
            probe.StartInfo.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
            Require(probe.Start(), "Could not start bundled path helper.");
            probe.StandardInput.WriteLine(JsonSerializer.Serialize(new { root = testRoot, purpose = "backup-export", path = destination }));
            probe.StandardInput.Close();
            var stdout = probe.StandardOutput.ReadToEndAsync();
            var stderr = probe.StandardError.ReadToEndAsync();
            if (!probe.WaitForExit(15000)) { probe.Kill(entireProcessTree: true); throw new TimeoutException("Bundled helper timed out."); }
            Require(probe.ExitCode == 0, "Bundled path helper failed: " + stderr.GetAwaiter().GetResult());
            using var response = JsonDocument.Parse(stdout.GetAwaiter().GetResult());
            Require(response.RootElement.GetProperty("Writable").GetBoolean(), "Bundled helper did not confirm writable storage.");
            Require(!Directory.EnumerateFiles(destination).Any(), "Path helper retained a temporary file.");
            var startupVerified = false;
            if (Environment.GetEnvironmentVariable("KNOWLEDGEAPP_STANDALONE_BENCHMARK") is { Length: > 0 } benchmarkPath)
            {
                Require(Path.IsPathFullyQualified(benchmarkPath) && Path.GetFileName(benchmarkPath) == "KnowledgeApp.StartupBenchmark.dll",
                    "Unexpected startup benchmark assembly.");
                var syntheticRoot = Path.Combine(temporary, prefix + Guid.NewGuid().ToString("D"));
                Require(!Directory.Exists(syntheticRoot) && !File.Exists(syntheticRoot), "Synthetic startup root collision.");
                Exception? startupFailure = null;
                var startupExitCode = -1;
                // Startup hooks run before the real app entry point. Use a new STA
                // for WPF and the same test-only MainWindow factory as other checks.
                // The bundled host/Data assemblies stay loaded from the actual exe.
                var thread = new Thread(() =>
                {
                    try
                    {
                        var benchmark = Assembly.LoadFrom(benchmarkPath);
                        var main = benchmark.GetType("Program", true)!.GetMethod("Main", BindingFlags.Static | BindingFlags.NonPublic)!;
                        startupExitCode = (int)main.Invoke(null, [new[] { syntheticRoot }])!;
                    }
                    catch (Exception exception) { startupFailure = exception; }
                });
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                Require(thread.Join(TimeSpan.FromSeconds(65)), "Packaged login startup timed out.");
                try
                {
                    if (startupFailure is not null) throw new InvalidOperationException("Packaged login failed.", startupFailure);
                    Require(startupExitCode == 0, "Packaged login or shutdown failed.");
                    startupVerified = true;
                }
                finally
                {
                    var boundary = Assembly.Load("KnowledgeApp.Data").GetType("KnowledgeApp.Data.FileSystemBoundary", true)!;
                    boundary.GetMethod("DeleteSyntheticRoot")!.Invoke(null, [syntheticRoot]);
                }
            }
            File.WriteAllText(Path.Combine(testRoot, "result.json"), JsonSerializer.Serialize(new
            {
                passed = true, extractedDirectory = extracted, bundledRuntime = true, uiManifestVerified = true,
                sqliteMemoryDatabase = true, pathProbe = true, startupModesPreserved = true,
                packagedLoginVerified = startupVerified, applicationDirectoriesProtected = true,
                rememberedCodexLocationProtected = true, userDatabaseOpened = false
            }));
            Environment.Exit(0);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: standalone startup hook: " + exception);
            Environment.Exit(1);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
