using System.ComponentModel;
using System.Runtime.InteropServices;

namespace KnowledgeApp.CSharp;

/// <summary>
/// Keeps the unmodified Tauri 2.4.3 single-instance protocol closed to a second
/// database owner in this Windows session. This is not a cross-session DB lock.
/// Acquire only after the C# current-user lease, and release only after DB close.
/// </summary>
public sealed class LegacyTauriInstanceGuard : IDisposable
{
    internal const string ProductionIdentifier = "jp.local.webknowledgesystem";
    private const int ErrorAlreadyExists = 183;
    private const int ErrorFileNotFound = 2;
    private const uint Synchronize = 0x00100000;
    private const uint WmCopyData = 0x004a;
    private static readonly WindowProcedure Procedure = WindowProc;
    private static readonly Dictionary<nint, LegacyTauriInstanceGuard> Windows = [];
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;
    private readonly string _className;
    private readonly string _windowName;
    private readonly string _mutexName;
    private readonly Action? _activationRequested;
    private readonly nint _module;
    private nint _window;
    private nint _mutex;
    private ushort _classAtom;
    private bool _ownsMutex;
    private bool _disposed;

    private LegacyTauriInstanceGuard(string identifier, Action? activationRequested)
    {
        _className = identifier + "-sic";
        _windowName = identifier + "-siw";
        _mutexName = identifier + "-sim";
        _activationRequested = activationRequested;
        _module = GetModuleHandle(null);
        if (_module == 0) throw NativeFailure();
    }

    // The production name cannot come from arguments, IPC, files, or a caller path.
    public static LegacyTauriInstanceGuard? TryAcquireForProduction(Action? activationRequested = null) =>
        TryAcquire(ProductionIdentifier, activationRequested, null);

    // Compiled into the test harness, but no host/UI contract exposes this seam.
    internal static LegacyTauriInstanceGuard? TryAcquireForSyntheticTest(Guid runId,
        Action? activationRequested = null, Action? beforeMutex = null) =>
        TryAcquire(SyntheticIdentifier(runId), activationRequested, beforeMutex);

    internal static string SyntheticIdentifier(Guid runId) => runId != Guid.Empty
        ? "KnowledgeApp.LegacyGuardCheck." + runId.ToString("N")
        : throw new ArgumentException("A fresh synthetic identity is required.", nameof(runId));

    private static LegacyTauriInstanceGuard? TryAcquire(string identifier, Action? activationRequested,
        Action? beforeMutex)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var guard = new LegacyTauriInstanceGuard(identifier, activationRequested);
        try
        {
            // Never send a message to, activate, destroy, or take ownership from an
            // existing window/mutex: it may belong to the old app or be unknown.
            if (FindWindow(guard._className, guard._windowName) != 0 || MutexExists(guard._mutexName))
            {
                guard.Dispose();
                return null;
            }

            var windowClass = new WindowClass
            {
                Size = (uint)Marshal.SizeOf<WindowClass>(),
                Procedure = Marshal.GetFunctionPointerForDelegate(Procedure),
                Instance = guard._module,
                ClassName = guard._className
            };
            guard._classAtom = RegisterClassEx(ref windowClass);
            if (guard._classAtom == 0) throw NativeFailure();

            // IMPORTANT: old Tauri continues startup if the mutex exists but this
            // top-level window is absent. Publish the window BEFORE the mutex.
            // A message-only HWND is not discoverable by Tauri's FindWindowW.
            guard._window = CreateWindowEx(0x08000000 | 0x00000080,
                guard._className, guard._windowName, 0, 0, 0, 0, 0, 0, 0, guard._module, 0);
            if (guard._window == 0) throw NativeFailure();
            lock (Windows) Windows.Add(guard._window, guard);
            beforeMutex?.Invoke();

            guard._mutex = CreateMutex(0, true, guard._mutexName);
            var mutexError = Marshal.GetLastPInvokeError();
            if (guard._mutex == 0) throw NativeFailure(mutexError);
            if (mutexError == ErrorAlreadyExists)
            {
                // A concurrent Tauri launch won. The returned handle grants no
                // mutex ownership; close only our handle/window, never Release it.
                guard.Dispose();
                return null;
            }
            guard._ownsMutex = true;
            return guard;
        }
        catch
        {
            guard.Dispose();
            throw;
        }
    }

    private static bool MutexExists(string name)
    {
        var handle = OpenMutex(Synchronize, false, name);
        var error = Marshal.GetLastPInvokeError();
        if (handle != 0)
        {
            _ = CloseHandle(handle);
            return true;
        }
        if (error != ErrorFileNotFound) throw NativeFailure(error);
        return false;
    }

    private static nint WindowProc(nint window, uint message, nuint wParam, nint lParam)
    {
        if (message == WmCopyData)
        {
            // Ignore all bytes/pointers/arguments. WM_COPYDATA is just a fixed
            // optional activation notification, never a file or command request.
            LegacyTauriInstanceGuard? owner;
            lock (Windows) Windows.TryGetValue(window, out owner);
            try { owner?._activationRequested?.Invoke(); }
            catch { /* No managed exception may unwind through a native WNDPROC. */ }
            return 1;
        }
        return DefWindowProc(window, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (Environment.CurrentManagedThreadId != _ownerThread)
            throw new InvalidOperationException("The legacy instance guard must be released on its owner thread.");
        _disposed = true;
        // The caller has closed the DB. Remove the mutex before the window so no
        // stale mutex-without-window interval invites an old Tauri startup bypass.
        if (_mutex != 0)
        {
            if (_ownsMutex) _ = ReleaseMutex(_mutex);
            _ = CloseHandle(_mutex);
            _mutex = 0;
        }
        if (_window != 0)
        {
            lock (Windows) Windows.Remove(_window);
            _ = DestroyWindow(_window);
            _window = 0;
        }
        if (_classAtom != 0)
        {
            _ = UnregisterClass(_className, _module);
            _classAtom = 0;
        }
    }

    private static Win32Exception NativeFailure(int? error = null) => new(error ?? Marshal.GetLastPInvokeError(),
        "The legacy single-instance guard could not be established; no database may be opened.");

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size;
        public uint Style;
        public nint Procedure;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public string? MenuName;
        public string ClassName;
        public nint SmallIcon;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandle(string? moduleName);
    [DllImport("kernel32.dll", EntryPoint = "CreateMutexW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateMutex(nint attributes, [MarshalAs(UnmanagedType.Bool)] bool initialOwner, string name);
    [DllImport("kernel32.dll", EntryPoint = "OpenMutexW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenMutex(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseMutex(nint mutex);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint FindWindow(string className, string windowName);
    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClass windowClass);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint extendedStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW", CharSet = CharSet.Unicode)]
    private static extern nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClass(string className, nint instance);
}
