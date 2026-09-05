using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace KnowledgeApp.CSharp;

internal static class SingleInstanceWindowActivation
{
    internal static SingleInstanceReply Restore(MainWindow window)
    {
        if (!window.CanReceiveActivation) return SingleInstanceReply.Closing;
        if (!window.IsLoaded) return SingleInstanceReply.Starting;
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        if (!window.IsVisible) window.Show();

        var handle = new WindowInteropHelper(window).Handle;
        // Preserve a native/WPF modal dialog, including the image/file picker.
        // Never enable its disabled owner or create a replacement window.
        var popup = GetLastActivePopup(handle);
        if (popup != IntPtr.Zero && popup != handle && IsWindowVisible(popup))
        {
            _ = SetForegroundWindow(popup);
        }
        else
        {
            _ = window.Activate();
            _ = SetForegroundWindow(handle);
        }
        return SingleInstanceReply.Activated;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetLastActivePopup(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
}
