using System.Windows;
using System.Windows.Interop;
using AV1Studio.Models;
using AV1Studio.Native;

namespace AV1Studio.Views;

public static class WindowChrome
{
    /// <summary>Dark title bar (the app is always dark), applied before the window is first painted.</summary>
    public static void UseDarkTitleBar(Window w) => w.SourceInitialized += (_, _) => Refresh(w);

    public static void Refresh(Window w)
    {
        var h = new WindowInteropHelper(w).Handle;
        if (h != IntPtr.Zero) Win32.SetDarkTitleBar(h, true);
    }
}
