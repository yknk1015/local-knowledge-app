using System.Diagnostics;
using System.Runtime.InteropServices;
using KnowledgeApp.CSharp;

internal static class Program
{
    private static readonly List<Process> Children = [];
    private static int _passed;

    [STAThread]
    private static int Main(string[] args)
    {
        try { return args.Length == 0 ? Run() : Child(args); }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        finally
        {
            foreach (var child in Children)
            {
                try
                {
                    if (!child.HasExited) { child.StandardInput.Close(); Wait(child, 3000); }
                }
                catch (InvalidOperationException) { }
                catch (IOException) { }
            }
        }
    }

    private static int Run()
    {
        Check(LegacyTauriInstanceGuard.ProductionIdentifier == "jp.local.webknowledgesystem",
            "production compatibility name matches the unmodified Tauri identifier (string only)");
        try { _ = LegacyTauriInstanceGuard.SyntheticIdentifier(Guid.Empty); throw new Exception("Empty ID accepted."); }
        catch (ArgumentException) { Check(true, "empty test identity rejected"); }
        var id = Guid.NewGuid();
        var name = LegacyTauriInstanceGuard.SyntheticIdentifier(id);
        Check(name != LegacyTauriInstanceGuard.ProductionIdentifier && name.Contains(id.ToString("N"), StringComparison.Ordinal),
            "all native test objects use a fresh isolated GUID, never production identity");

        var sawWindowFirst = false;
        using (var guard = LegacyTauriInstanceGuard.TryAcquireForSyntheticTest(id, beforeMutex: () =>
        {
            sawWindowFirst = Native.Find(name) != 0 && !Native.MutexExists(name);
        }))
        {
            Check(guard is not null && sawWindowFirst, "discoverable top-level window precedes mutex publication");
            Check(Native.Find(name) != 0 && Native.MutexExists(name), "owner retains both compatibility objects");
            using var duplicate = LegacyTauriInstanceGuard.TryAcquireForSyntheticTest(id);
            Check(duplicate is null, "same-process duplicate is refused without destroying owner's objects");
            using var old = Start(id, "legacy");
            Expect(old, "LEGACY-STOPPED"); Exit(old, 0);
            Check(true, "unmodified legacy startup algorithm exits before any data work when C# owns the session");
            try { Task.Run(guard!.Dispose).GetAwaiter().GetResult(); throw new Exception("Cross-thread disposal accepted."); }
            catch (InvalidOperationException) { Check(true, "guard lifetime is tied to the original owner thread"); }
            Check(Native.Find(name) != 0 && Native.MutexExists(name), "invalid disposal attempt does not release exclusion");
        }
        Check(Native.Find(name) == 0 && !Native.MutexExists(name), "normal disposal removes only owned window and mutex");
        using (var restarted = LegacyTauriInstanceGuard.TryAcquireForSyntheticTest(id))
            Check(restarted is not null, "same test identity can restart after complete cleanup");

        foreach (var mode in new[] { "legacy", "window-only", "mutex-only" })
        {
            var existingId = Guid.NewGuid();
            var existingName = LegacyTauriInstanceGuard.SyntheticIdentifier(existingId);
            using var existing = Start(existingId, mode);
            Expect(existing, "OWNER");
            using var denied = LegacyTauriInstanceGuard.TryAcquireForSyntheticTest(existingId);
            Check(denied is null, $"{mode} already present is fail-closed before host/DB construction");
            Check((mode == "mutex-only" || Native.Find(existingName) != 0) &&
                (mode == "window-only" || Native.MutexExists(existingName)), $"{mode} existing native objects are left untouched");
            Stop(existing);
            using var retry = LegacyTauriInstanceGuard.TryAcquireForSyntheticTest(existingId);
            Check(retry is not null, $"{mode} release permits a clean retry");
        }

        // Demonstrate why a mutex-only fix is insufficient: the actual plugin's
        // ERROR_ALREADY_EXISTS + missing-window path continues into setup.
        var bypassId = Guid.NewGuid();
        using (var owner = Start(bypassId, "mutex-only"))
        {
            Expect(owner, "OWNER");
            using var old = Start(bypassId, "legacy");
            Expect(old, "LEGACY-WOULD-BYPASS"); Exit(old, 0);
            Check(true, "test faithfully detects legacy mutex-without-window startup bypass");
            Stop(owner);
        }

        var callbackId = Guid.NewGuid();
        var callbacks = 0;
        using (var guard = LegacyTauriInstanceGuard.TryAcquireForSyntheticTest(callbackId, () => callbacks++))
        {
            using var old = Start(callbackId, "legacy");
            Expect(old, "LEGACY-STOPPED"); Exit(old, 0);
            Check(callbacks == 1, "legacy WM_COPYDATA produces only the fixed optional activation notification");
            Native.SendIgnoredCopyData(Native.Find(LegacyTauriInstanceGuard.SyntheticIdentifier(callbackId)));
            Check(callbacks == 2, "untrusted copy-data command/path bytes are not interpreted or opened");
        }
        var throwingId = Guid.NewGuid();
        using (var throwing = LegacyTauriInstanceGuard.TryAcquireForSyntheticTest(throwingId,
            () => throw new InvalidOperationException("Synthetic callback failure.")))
        {
            Native.SendIgnoredCopyData(Native.Find(LegacyTauriInstanceGuard.SyntheticIdentifier(throwingId)));
            Check(throwing is not null && Native.MutexExists(LegacyTauriInstanceGuard.SyntheticIdentifier(throwingId)),
                "activation callback failure cannot unwind through WNDPROC or release the lease");
        }

        var failedId = Guid.NewGuid();
        try
        {
            using var fail = LegacyTauriInstanceGuard.TryAcquireForSyntheticTest(failedId,
                beforeMutex: () => throw new InvalidOperationException("Synthetic pre-mutex failure."));
            throw new Exception("Failure seam did not run.");
        }
        catch (InvalidOperationException) { Check(true, "pre-mutex initialization failure is surfaced"); }
        using (var retry = LegacyTauriInstanceGuard.TryAcquireForSyntheticTest(failedId))
            Check(retry is not null, "failed initialization cleans up only its own window/class");

        // Deterministically place an old process between C# window publication
        // and mutex creation. The old process owns the lease; C# must stand down.
        var oldWinsId = Guid.NewGuid();
        Process? oldWinner = null;
        using (var loser = LegacyTauriInstanceGuard.TryAcquireForSyntheticTest(oldWinsId, beforeMutex: () =>
        {
            oldWinner = Start(oldWinsId, "legacy");
            Expect(oldWinner, "OWNER");
        }))
        {
            Check(loser is null, "legacy process winning the publication race forces C# to stop");
            Check(Native.MutexExists(LegacyTauriInstanceGuard.SyntheticIdentifier(oldWinsId)),
                "C# race loser does not release legacy-owned mutex");
        }
        Stop(oldWinner!);
        using (var retry = LegacyTauriInstanceGuard.TryAcquireForSyntheticTest(oldWinsId))
            Check(retry is not null, "publication-race cleanup permits later acquisition");

        for (var iteration = 0; iteration < 12; iteration++)
        {
            var raceId = Guid.NewGuid();
            using var first = Start(raceId, "race-guard");
            using var second = Start(raceId, "race-legacy");
            Expect(first, "READY"); Expect(second, "READY");
            first.StandardInput.WriteLine("go"); first.StandardInput.Flush();
            second.StandardInput.WriteLine("go"); second.StandardInput.Flush();
            var firstRole = Read(first); var secondRole = Read(second);
            Check((firstRole == "OWNER" && secondRole == "LEGACY-STOPPED") ||
                (firstRole == "REFUSED" && secondRole == "OWNER"),
                $"concurrent legacy/C# launch {iteration + 1}: exactly one owner, no bypass");
            if (firstRole == "OWNER") Stop(first); else Exit(first, 0);
            if (secondRole == "OWNER") Stop(second); else Exit(second, 0);
        }

        var abruptId = Guid.NewGuid();
        using (var abrupt = Start(abruptId, "guard"))
        {
            Expect(abrupt, "OWNER");
            abrupt.StandardInput.WriteLine("abandon"); abrupt.StandardInput.Flush(); Exit(abrupt, 17);
        }
        using (var recovered = LegacyTauriInstanceGuard.TryAcquireForSyntheticTest(abruptId))
            Check(recovered is not null, "OS cleanup after synthetic owner self-exit permits restart");

        Console.WriteLine($"LegacyInstanceCheck PASS: {_passed} checks; isolated synthetic child processes and hidden windows only; no production identifiers opened, DB, real app, foreground change, file payload access or process termination.");
        return 0;
    }

    private static int Child(string[] args)
    {
        if (args.Length != 3 || args[0] != "child" || !Guid.TryParseExact(args[1], "N", out var id) ||
            id == Guid.Empty || args[2] is not ("guard" or "legacy" or "mutex-only" or "window-only" or "race-guard" or "race-legacy")) return 64;
        var mode = args[2];
        if (mode.StartsWith("race-", StringComparison.Ordinal))
        {
            Console.WriteLine("READY");
            if (Console.ReadLine() != "go") return 65;
            mode = mode[5..];
        }
        var identifier = LegacyTauriInstanceGuard.SyntheticIdentifier(id);
        if (mode == "guard")
        {
            using var guard = LegacyTauriInstanceGuard.TryAcquireForSyntheticTest(id);
            Console.WriteLine(guard is null ? "REFUSED" : "OWNER");
            return guard is null ? 0 : OwnerLoop();
        }
        using var mutex = mode != "window-only" ? new Native.LeaseMutex(identifier) : null;
        if (mode == "legacy" && mutex!.AlreadyExists)
        {
            var target = Native.Find(identifier);
            if (target == 0)
            {
                Console.WriteLine("LEGACY-WOULD-BYPASS");
                return 0;
            }
            // Same branch and synchronous message as tauri-plugin-single-instance
            // 2.4.3 windows.rs. The harness never proceeds into actual app/DB code.
            Native.SendIgnoredCopyData(target);
            Console.WriteLine("LEGACY-STOPPED");
            return 0;
        }
        using var window = mode != "mutex-only" ? new Native.LeaseWindow(identifier) : null;
        Console.WriteLine("OWNER");
        return OwnerLoop();
    }

    private static int OwnerLoop()
    {
        var input = Task.Run(Console.ReadLine);
        while (true)
        {
            Native.Pump();
            if (!input.IsCompleted) { Thread.Sleep(2); continue; }
            var command = input.GetAwaiter().GetResult();
            if (command is null or "stop") return 0;
            if (command == "abandon") Environment.Exit(17); // Only this owned synthetic child exits itself.
            return 66;
        }
    }

    private static Process Start(Guid id, string mode)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Missing test executable.");
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("child"); start.ArgumentList.Add(id.ToString("N")); start.ArgumentList.Add(mode);
        var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start synthetic child.");
        Children.Add(child);
        return child;
    }

    private static string? Read(Process process)
    {
        var task = process.StandardOutput.ReadLineAsync();
        var timer = Stopwatch.StartNew();
        while (!task.IsCompleted && timer.ElapsedMilliseconds < 10000) { Native.Pump(); Thread.Sleep(2); }
        if (!task.IsCompleted) throw new TimeoutException("Synthetic child response timed out.");
        return task.GetAwaiter().GetResult();
    }

    private static void Expect(Process process, string expected)
    {
        var actual = Read(process);
        if (actual != expected) throw new InvalidOperationException($"Expected {expected}; got {actual}.");
    }

    private static void Wait(Process process, int milliseconds)
    {
        var timer = Stopwatch.StartNew();
        while (!process.HasExited && timer.ElapsedMilliseconds < milliseconds) { Native.Pump(); Thread.Sleep(2); }
    }

    private static void Exit(Process process, int expected)
    {
        Wait(process, 10000);
        if (!process.HasExited || process.ExitCode != expected) throw new InvalidOperationException("Synthetic child did not exit as expected.");
    }

    private static void Stop(Process process)
    {
        process.StandardInput.WriteLine("stop"); process.StandardInput.Flush(); Exit(process, 0);
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + name);
        _passed++;
        Console.WriteLine("PASS: " + name);
    }
}

internal static class Native
{
    private static readonly WindowProcDelegate Procedure = DefWindowProc;
    internal static nint Find(string identifier) => FindWindow(identifier + "-sic", identifier + "-siw");
    internal static bool MutexExists(string identifier)
    {
        var handle = OpenMutex(0x00100000, false, identifier + "-sim");
        if (handle == 0) return false;
        _ = CloseHandle(handle); return true;
    }

    internal sealed class LeaseMutex : IDisposable
    {
        private readonly nint _handle;
        internal bool AlreadyExists { get; }
        internal LeaseMutex(string identifier)
        {
            _handle = CreateMutex(0, true, identifier + "-sim");
            AlreadyExists = Marshal.GetLastPInvokeError() == 183;
            if (_handle == 0) throw new InvalidOperationException("Synthetic native mutex creation failed.");
        }
        public void Dispose() { if (!AlreadyExists) _ = ReleaseMutex(_handle); _ = CloseHandle(_handle); }
    }

    internal sealed class LeaseWindow : IDisposable
    {
        private readonly string _name;
        private readonly nint _module = GetModuleHandle(null);
        private readonly nint _window;
        internal LeaseWindow(string identifier)
        {
            _name = identifier + "-sic";
            var windowClass = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(),
                Procedure = Marshal.GetFunctionPointerForDelegate(Procedure), Instance = _module, ClassName = _name };
            if (RegisterClassEx(ref windowClass) == 0) throw new InvalidOperationException("Synthetic window class creation failed.");
            _window = CreateWindowEx(0x08000080, _name, identifier + "-siw", 0, 0, 0, 0, 0, 0, 0, _module, 0);
            if (_window == 0) throw new InvalidOperationException("Synthetic native window creation failed.");
        }
        public void Dispose() { _ = DestroyWindow(_window); _ = UnregisterClass(_name, _module); }
    }

    internal static void SendIgnoredCopyData(nint window)
    {
        const string text = "C:\\synthetic-never-read|--arbitrary-command|DROP TABLE articles";
        var payload = Marshal.StringToHGlobalAnsi(text);
        try
        {
            var data = new CopyData { Kind = 1542, Length = text.Length + 1, Data = payload };
            _ = SendMessage(window, 0x004a, 0, ref data);
        }
        finally { Marshal.FreeHGlobal(payload); }
    }

    internal static void Pump()
    {
        while (PeekMessage(out var message, 0, 0, 0, 1))
        {
            _ = TranslateMessage(ref message); _ = DispatchMessage(ref message);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProcDelegate(nint window, uint message, nuint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size; public uint Style; public nint Procedure; public int ClassExtra; public int WindowExtra;
        public nint Instance; public nint Icon; public nint Cursor; public nint Background;
        public string? MenuName; public string ClassName; public nint SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct CopyData { public nuint Kind; public int Length; public nint Data; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time;
        public int X; public int Y; public uint Private;
    }
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? name);
    [DllImport("kernel32.dll", EntryPoint = "CreateMutexW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateMutex(nint attributes, [MarshalAs(UnmanagedType.Bool)] bool owner, string name);
    [DllImport("kernel32.dll", EntryPoint = "OpenMutexW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenMutex(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReleaseMutex(nint handle);
    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClass windowClass);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint exStyle, string className, string name, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string className, string name);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW", CharSet = CharSet.Unicode)]
    private static extern nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
    private static extern nint SendMessage(nint window, uint message, nuint wParam, ref CopyData data);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterClass(string name, nint instance);
    [DllImport("user32.dll", EntryPoint = "PeekMessageW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PeekMessage(out Message message, nint window, uint min, uint max, uint remove);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW", CharSet = CharSet.Unicode)]
    private static extern nint DispatchMessage(ref Message message);
}
