using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using KnowledgeApp.CSharp;

try { return args.Length > 0 ? Checks.Child(args) : Checks.Run(); }
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
finally { Checks.ReleaseChildren(); }

internal static class Checks
{
    private static int _passed;
    private static readonly List<Process> Children = [];

    internal static int Run()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows checks only.");
        var identity = SingleInstanceIdentity.ForCurrentUser();
        Check(identity == SingleInstanceIdentity.ForCurrentUser(), "stable current-user trial identity");
        Check(identity.MutexName.StartsWith("KnowledgeApp.CSharp.Trial.", StringComparison.Ordinal), "fixed trial product scope");
        var runId = Guid.NewGuid();
        var synthetic = SingleInstanceIdentity.ForSyntheticTest(runId);
        Check(synthetic != identity && synthetic == SingleInstanceIdentity.ForSyntheticTest(runId), "isolated deterministic synthetic scope");
        Check(synthetic != SingleInstanceIdentity.ForSyntheticTest(Guid.NewGuid()), "separate test runs cannot interfere");
        try { _ = SingleInstanceIdentity.ForSyntheticTest(Guid.Empty); throw new Exception("Empty run ID accepted."); }
        catch (ArgumentException) { Check(true, "empty synthetic identity rejected"); }

        var token = SingleInstancePolicy.ActivationRequest.ToArray();
        Check(SingleInstancePolicy.IsActivationRequest(token, true), "fixed activation protocol accepted");
        Check(!SingleInstancePolicy.IsActivationRequest(token, false), "incomplete message rejected");
        foreach (var bad in new[] { "", "activate", "KnowledgeApp.Activate.v2", "KnowledgeApp.Activate.v1\n",
            "KnowledgeApp.Activate.v1\0", "KnowledgeApp.Activate.v1 C:\\outside.msg", "{\"sql\":\"DROP TABLE articles\"}" })
        {
            Check(!SingleInstancePolicy.IsActivationRequest(Encoding.UTF8.GetBytes(bad), true), "non-contract payload rejected");
        }
        Check(SingleInstancePolicy.Notice(SingleInstanceReply.Closing).Contains("終了処理中", StringComparison.Ordinal), "fixed closing notice");
        Check(!SingleInstancePolicy.Notice(SingleInstanceReply.Unavailable).Contains(identity.MutexName, StringComparison.Ordinal), "notice has no identity or path");

        using (var primary = StartChild(runId, "ready"))
        {
            ExpectLine(primary, "PRIMARY");
            using (var secondary = StartChild(runId, "ready"))
            {
                ExpectLine(secondary, "SECONDARY");
                ExpectLine(secondary, "Activated");
                ExpectExit(secondary, 0);
                Check(true, "two-process exclusion before primary-only work and activation acknowledgement");
            }
            Command(primary, "count", "1");
            Check(true, "exactly one activation delivered");

            foreach (var payload in new[] { "wrong", "KnowledgeApp.Activate.v1 C:\\outside.msg", new string('A', 4096) })
            {
                var reply = SendRaw(synthetic, Encoding.UTF8.GetBytes(payload), receive: true);
                Check(reply is SingleInstanceReply.Rejected or SingleInstanceReply.Unavailable,
                    "real IPC rejects unknown, appended, or oversized message");
            }
            Command(primary, "count", "1");
            Check(true, "invalid requests do not invoke activation");

            using (var silent = Connect(synthetic))
            {
                Thread.Sleep(1250);
            }
            Check(SingleInstanceCoordinator.RequestActivationAsync(synthetic).GetAwaiter().GetResult() == SingleInstanceReply.Activated,
                "silent client times out and later activation succeeds");
            Command(primary, "count", "2");
            Check(true, "silent input never invokes callback");

            // A disconnected sender is not fatal to the persistent first-instance pipe.
            _ = SendRaw(synthetic, Encoding.UTF8.GetBytes("invalid"), receive: false);
            Check(SingleInstanceCoordinator.RequestActivationAsync(synthetic).GetAwaiter().GetResult() == SingleInstanceReply.Activated,
                "listener survives sender disconnect");
            StopChild(primary);
            Check(true, "normal owner shutdown releases OS lease");
        }

        using (var restarted = StartChild(runId, "ready"))
        {
            ExpectLine(restarted, "PRIMARY");
            StopChild(restarted);
            Check(true, "same identity restarts without stale lockfile");
        }

        var blockedRunId = Guid.NewGuid();
        var blockedIdentity = SingleInstanceIdentity.ForSyntheticTest(blockedRunId);
        using (var occupiedPipe = new NamedPipeServerStream(blockedIdentity.PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Message, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance))
        {
            try
            {
                using var unexpected = SingleInstanceCoordinator.TryAcquire(blockedIdentity,
                    _ => Task.FromResult(SingleInstanceReply.Activated));
                throw new InvalidOperationException("An occupied IPC endpoint was accepted.");
            }
            catch (IOException) { Check(true, "preoccupied IPC endpoint fails closed before host startup"); }
            catch (UnauthorizedAccessException) { Check(true, "preoccupied IPC endpoint fails closed before host startup"); }
        }
        using (var retry = StartChild(blockedRunId, "ready"))
        {
            ExpectLine(retry, "PRIMARY");
            StopChild(retry);
            Check(true, "endpoint creation failure releases the OS lease for another process to retry");
        }

        using (var primary = StartChild(Guid.NewGuid(), "closing"))
        {
            ExpectLine(primary, "PRIMARY");
            var reply = SingleInstanceCoordinator.RequestActivationAsync(ChildIdentity(primary)).GetAwaiter().GetResult();
            Check(reply == SingleInstanceReply.Closing, "closing owner refuses reactivation");
            using var duplicate = StartChild(Guid.ParseExact(primary.StartInfo.ArgumentList[^2], "N"), "ready");
            ExpectLine(duplicate, "SECONDARY");
            ExpectLine(duplicate, "Closing");
            ExpectExit(duplicate, 0);
            Check(true, "shutdown holds its lease and forbids a second primary");
            StopChild(primary);
        }

        using (var primary = StartChild(Guid.NewGuid(), "starting"))
        {
            ExpectLine(primary, "PRIMARY");
            var response = SingleInstanceCoordinator.RequestActivationAsync(ChildIdentity(primary));
            Thread.Sleep(250);
            Command(primary, "ready", "READY");
            Check(response.GetAwaiter().GetResult() == SingleInstanceReply.Activated, "early request retries until window is ready");
            StopChild(primary);
        }

        foreach (var mode in new[] { "starting", "unresponsive" })
        {
            using var primary = StartChild(Guid.NewGuid(), mode);
            ExpectLine(primary, "PRIMARY");
            var clock = Stopwatch.StartNew();
            var reply = SingleInstanceCoordinator.RequestActivationAsync(ChildIdentity(primary)).GetAwaiter().GetResult();
            Check(reply == SingleInstanceReply.Unavailable && clock.Elapsed < TimeSpan.FromSeconds(7),
                $"{mode} owner produces bounded result without another instance");
            StopChild(primary);
        }

        var missingClock = Stopwatch.StartNew();
        Check(SingleInstanceCoordinator.RequestActivationAsync(SingleInstanceIdentity.ForSyntheticTest(Guid.NewGuid()))
                .GetAwaiter().GetResult() == SingleInstanceReply.Unavailable && missingClock.Elapsed < TimeSpan.FromSeconds(7),
            "owner disappearing before pipe connection produces bounded failure");

        var abandonedId = Guid.NewGuid();
        var abandonedIdentity = SingleInstanceIdentity.ForSyntheticTest(abandonedId);
        using (var keepObjectAlive = new Mutex(false, abandonedIdentity.MutexName,
            new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = false }))
        {
            using (var abrupt = StartChild(abandonedId, "ready"))
            {
                ExpectLine(abrupt, "PRIMARY");
                abrupt.StandardInput.WriteLine("abandon");
                abrupt.StandardInput.Flush();
                ExpectExit(abrupt, 17);
            }
            using var recovered = SingleInstanceCoordinator.TryAcquire(abandonedIdentity,
                _ => Task.FromResult(SingleInstanceReply.Activated));
            Check(recovered is not null, "abandoned OS mutex recovers after synthetic child self-exit");
        }

        var raceId = Guid.NewGuid();
        using (var first = StartChild(raceId, "race"))
        using (var second = StartChild(raceId, "race"))
        {
            ExpectLine(first, "WAITING");
            ExpectLine(second, "WAITING");
            first.StandardInput.WriteLine("go");
            first.StandardInput.Flush();
            second.StandardInput.WriteLine("go");
            second.StandardInput.Flush();
            var role1 = ReadLine(first);
            var role2 = ReadLine(second);
            Check(new[] { role1, role2 }.Count(role => role == "PRIMARY") == 1 &&
                new[] { role1, role2 }.Count(role => role == "SECONDARY") == 1, "simultaneous launch elects exactly one primary");
            var owner = role1 == "PRIMARY" ? first : second;
            var duplicate = role1 == "SECONDARY" ? first : second;
            ExpectLine(duplicate, "Activated");
            ExpectExit(duplicate, 0);
            StopChild(owner);
        }

        Console.WriteLine($"SingleInstanceCheck PASS: {_passed} checks; synthetic processes only; no DB, WebView, user process, or foreground change.");
        return 0;
    }

    internal static int Child(string[] arguments)
    {
        if (arguments.Length != 3 || arguments[0] != "child" ||
            !Guid.TryParseExact(arguments[1], "N", out var runId) || runId == Guid.Empty ||
            arguments[2] is not ("ready" or "starting" or "closing" or "unresponsive" or "race")) return 64;
        var mode = arguments[2];
        if (mode == "race")
        {
            Console.WriteLine("WAITING");
            if (Console.ReadLine() != "go") return 65;
            mode = "ready";
        }
        var count = 0;
        var identity = SingleInstanceIdentity.ForSyntheticTest(runId);
        using var lease = SingleInstanceCoordinator.TryAcquire(identity, async token =>
        {
            var current = Volatile.Read(ref mode);
            if (current == "unresponsive") await Task.Delay(Timeout.InfiniteTimeSpan, token);
            if (current == "starting") return SingleInstanceReply.Starting;
            if (current == "closing") return SingleInstanceReply.Closing;
            Interlocked.Increment(ref count);
            return SingleInstanceReply.Activated;
        });
        if (lease is null)
        {
            Console.WriteLine("SECONDARY");
            Console.WriteLine(SingleInstanceCoordinator.RequestActivationAsync(identity).GetAwaiter().GetResult());
            return 0;
        }
        Console.WriteLine("PRIMARY");
        // No application/data constructors exist in this harness. Only PRIMARY
        // reaches the service loop, which stands in for permitted host startup.
        while (true)
        {
            var command = Console.ReadLine();
            if (command is null or "stop") return 0;
            if (command == "count") Console.WriteLine(Volatile.Read(ref count));
            else if (command == "ready") { Volatile.Write(ref mode, "ready"); Console.WriteLine("READY"); }
            else if (command == "abandon") Environment.Exit(17); // This isolated child only; no user process is terminated.
            else return 66;
        }
    }

    private static Process StartChild(Guid id, string mode)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Missing harness executable.");
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(Checks).Assembly.Location);
        start.ArgumentList.Add("child");
        start.ArgumentList.Add(id.ToString("N"));
        start.ArgumentList.Add(mode);
        var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start synthetic child.");
        Children.Add(child);
        return child;
    }

    private static SingleInstanceIdentity ChildIdentity(Process process) =>
        SingleInstanceIdentity.ForSyntheticTest(Guid.ParseExact(process.StartInfo.ArgumentList[^2], "N"));

    private static NamedPipeClientStream Connect(SingleInstanceIdentity identity)
    {
        var client = new NamedPipeClientStream(".", identity.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        client.Connect(3000);
        client.ReadMode = PipeTransmissionMode.Message;
        return client;
    }

    private static SingleInstanceReply SendRaw(SingleInstanceIdentity identity, byte[] payload, bool receive)
    {
        using var client = Connect(identity);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            client.WriteAsync(payload, timeout.Token).AsTask().GetAwaiter().GetResult();
            if (!receive) return SingleInstanceReply.Unavailable;
            var reply = new byte[2];
            var count = client.ReadAsync(reply, timeout.Token).AsTask().GetAwaiter().GetResult();
            if (count != 1 || !client.IsMessageComplete) return SingleInstanceReply.Unavailable;
            client.WriteAsync(new byte[] { 6 }, timeout.Token).AsTask().GetAwaiter().GetResult();
            return (SingleInstanceReply)reply[0];
        }
        catch (IOException) { return SingleInstanceReply.Unavailable; }
    }

    private static void StopChild(Process child)
    {
        child.StandardInput.WriteLine("stop");
        child.StandardInput.Flush();
        ExpectExit(child, 0);
    }

    private static void Command(Process child, string command, string expected)
    {
        child.StandardInput.WriteLine(command);
        child.StandardInput.Flush();
        ExpectLine(child, expected);
    }

    private static string? ReadLine(Process child) =>
        child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();

    private static void ExpectLine(Process child, string expected)
    {
        var actual = ReadLine(child);
        if (actual != expected) throw new InvalidOperationException($"Expected synthetic output {expected}, got {actual}.");
    }

    private static void ExpectExit(Process child, int expected)
    {
        if (!child.WaitForExit(10000) || child.ExitCode != expected)
            throw new InvalidOperationException("Synthetic child did not exit as expected.");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException($"FAIL: {name}");
        _passed++;
        Console.WriteLine($"PASS: {name}");
    }

    internal static void ReleaseChildren()
    {
        foreach (var child in Children)
        {
            // Cooperative cleanup only for children created by this harness.
            try
            {
                if (!child.HasExited)
                {
                    child.StandardInput.Close();
                    _ = child.WaitForExit(3000);
                }
            }
            catch (InvalidOperationException) { }
            catch (IOException) { }
        }
    }
}
