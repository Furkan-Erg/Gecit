using System.Runtime.InteropServices;

namespace Gecit;

static partial class NativeTheme
{
    public static void UseDarkTitleBar(IntPtr hwnd)
    {
        int on = 1;
        DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));   // DWMWA_USE_IMMERSIVE_DARK_MODE
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
