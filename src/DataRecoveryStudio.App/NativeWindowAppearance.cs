using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DataRecoveryStudio.App;

public static class NativeWindowAppearance
{
    private const int UseImmersiveDarkMode = 20;
    private const int UseImmersiveDarkModeBefore20H1 = 19;
    private const int CaptionColor = 35;
    private const int TextColor = 36;

    public static bool TryApply(Window window, bool useDarkTitleBar)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            return false;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var enabled = useDarkTitleBar ? 1 : 0;
            var size = Marshal.SizeOf<int>();
            var result = DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref enabled, size);
            if (result != 0)
            {
                result = DwmSetWindowAttribute(handle, UseImmersiveDarkModeBefore20H1, ref enabled, size);
            }

            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                TryApplyColor(window, handle, CaptionColor, "HeaderBackgroundBrush");
                TryApplyColor(window, handle, TextColor, "TextPrimaryBrush");
            }

            return result == 0;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static void TryApplyColor(Window window, IntPtr handle, int attribute, string resourceKey)
    {
        if (window.TryFindResource(resourceKey) is not SolidColorBrush brush)
        {
            return;
        }

        var color = brush.Color;
        var colorReference = color.R | (color.G << 8) | (color.B << 16);
        _ = DwmSetWindowAttribute(handle, attribute, ref colorReference, Marshal.SizeOf<int>());
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr windowHandle, int attribute, ref int attributeValue, int attributeSize);
}
