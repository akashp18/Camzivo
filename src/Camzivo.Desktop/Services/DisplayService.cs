using System.Runtime.InteropServices;
using System.Windows;

namespace Camzivo.Desktop.Services;

/// <summary>One physical display, with bounds in device-independent pixels.</summary>
public sealed record DisplayInfo(string Name, Rect Bounds, double Scale, bool IsPrimary)
{
    public string DisplayName => IsPrimary ? $"Display 1 · primary · {Bounds.Width:F0} × {Bounds.Height:F0}" : $"Display · {Bounds.Width:F0} × {Bounds.Height:F0}";
}

/// <summary>
/// Enumerates monitors for the "open the wall on another display" action.
///
/// Bounds are converted from physical pixels to DIPs using each monitor's effective DPI, so a 150% display gets a
/// window the same logical size as the others rather than a third of it. Mixed-DPI correctness is only as good as the
/// process DPI awareness declared by the application manifest, which is why the conversion is explicit here.
/// </summary>
public static class DisplayService
{
    private const int MonitorInfoPrimary = 0x00000001;
    private const int EffectiveDpi = 0;

    public static IReadOnlyList<DisplayInfo> All()
    {
        var displays = new List<DisplayInfo>();

        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
            {
                var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };

                if (!GetMonitorInfo(monitor, ref info))
                {
                    return true;
                }

                var scale = 1.0;
                if (GetDpiForMonitor(monitor, EffectiveDpi, out var dpiX, out _) == 0 && dpiX > 0)
                {
                    scale = dpiX / 96.0;
                }

                var width = (info.Monitor.Right - info.Monitor.Left) / scale;
                var height = (info.Monitor.Bottom - info.Monitor.Top) / scale;

                displays.Add(new DisplayInfo(
                    info.DeviceName,
                    new Rect(info.Monitor.Left / scale, info.Monitor.Top / scale, width, height),
                    scale,
                    (info.Flags & MonitorInfoPrimary) != 0));

                return true;
            }, IntPtr.Zero);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            // Enumeration is a convenience; a machine without shcore still gets the primary work area.
        }

        if (displays.Count == 0)
        {
            var area = SystemParameters.WorkArea;
            displays.Add(new DisplayInfo("primary", area, 1, true));
        }

        return displays;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public int Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
}
