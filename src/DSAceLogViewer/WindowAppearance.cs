using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DSAceLogViewer;

internal static class WindowAppearance
{
    public static void UseDarkTitleBar(Window window)
    {
        var enabled = 1;
        _ = DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, 20, ref enabled, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
