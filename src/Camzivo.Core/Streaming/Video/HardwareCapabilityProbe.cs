using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Camzivo.Core.Streaming.Video;

/// <summary>What this machine actually offers for hardware video, measured rather than assumed.</summary>
public sealed record HardwareCapability(
    int HardwareH264Decoders,
    int SoftwareH264Decoders,
    bool D3D11DeviceCreated,
    int D3D11FeatureLevel,
    bool DxgiDeviceManagerCreated,
    string Detail)
{
    public bool HardwareDecodeAvailable => HardwareH264Decoders > 0;
}

/// <summary>
/// Answers the question the acceleration work depends on: does this machine expose a hardware H.264 decoder through
/// Media Foundation, can a D3D11 device be created, and can Media Foundation wrap that device?
///
/// It deliberately stops at evidence. Our decode path is synchronous (ProcessInput/ProcessOutput, CPU NV12 output);
/// hardware H.264 MFTs are asynchronous and hand back D3D11 textures, so using them is a different pipeline, not a
/// flag on the existing one. The probe documents what is present without pretending it is wired up.
/// </summary>
[SupportedOSPlatform("windows")]
public static class HardwareCapabilityProbe
{
    private const uint MftEnumFlagAll = 0x3F;
    private const uint MftEnumFlagHardware = 0x4;

    private const int D3DDriverTypeHardware = 1;
    private const uint D3D11CreateDeviceBgraSupport = 0x20;
    private const uint D3D11CreateDeviceVideoSupport = 0x800;
    private const uint D3D11SdkVersion = 7;

    public static HardwareCapability Probe()
    {
        var hardware = 0;
        var software = 0;
        var enumHr = 0;
        string detail;

        try
        {
            if (MediaFoundationInterop.MFStartup(MediaFoundationInterop.MfVersion, MediaFoundationInterop.MfStartupFull) >= 0)
            {
                try
                {
                    (hardware, enumHr) = Count(MftEnumFlagHardware);

                    // Software decoders are whatever remains once hardware ones are taken out: asking for a
                    // combined flag mask is not portable across driver registrations.
                    software = Math.Max(0, Count(MftEnumFlagAll).Count - hardware);
                }
                finally
                {
                    MediaFoundationInterop.MFShutdown();
                }

                detail = hardware > 0
                    ? "Media Foundation enumerates a hardware H.264 decoder MFT."
                    : $"Media Foundation hardware enumeration returned no H.264 decoder MFT (hr=0x{enumHr:X8}).";
            }
            else
            {
                detail = "Media Foundation could not be started.";
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            detail = "Media Foundation is not available.";
        }

        var (created, featureLevel, deviceDetail) = CreateDevice();
        var manager = false;
        var managerDetail = string.Empty;

        if (created)
        {
            manager = TryCreateDeviceManager(out managerDetail);
        }

        return new HardwareCapability(
            hardware,
            software,
            created,
            featureLevel,
            manager,
            string.Join(" ", new[] { detail, deviceDetail, managerDetail }.Where(part => part.Length > 0)));
    }

    private static (int Count, int Hr) Count(uint flags)
    {
        // The input type filter is what the enumerator actually matches on; with both types NULL this Windows
        // build returns S_OK and zero items.
        var input = new MediaFoundationInterop.MftRegisterTypeInfo
        {
            MajorType = MediaFoundationInterop.MFMediaType_Video,
            Subtype = MediaFoundationInterop.MFVideoFormat_H264,
        };

        var inputPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<MediaFoundationInterop.MftRegisterTypeInfo>());
        Marshal.StructureToPtr(input, inputPtr, false);

        int hr;
        IntPtr activates;
        uint count;

        try
        {
            hr = MediaFoundationInterop.MFTEnumEx(
                MediaFoundationInterop.MFT_CATEGORY_VIDEO_DECODER,
                flags,
                inputPtr,
                IntPtr.Zero,
                out activates,
                out count);
        }
        finally
        {
            Marshal.FreeCoTaskMem(inputPtr);
        }

        if (hr < 0 || activates == IntPtr.Zero)
        {
            return (0, hr);
        }

        for (var index = 0; index < count; index++)
        {
            var item = Marshal.ReadIntPtr(activates, index * IntPtr.Size);
            if (item != IntPtr.Zero)
            {
                Marshal.Release(item);
            }
        }

        Marshal.FreeCoTaskMem(activates);
        return ((int)count, hr);
    }

    private static (bool Created, int FeatureLevel, string Detail) CreateDevice()
    {
        var hr = D3D11CreateDevice(
            IntPtr.Zero,
            D3DDriverTypeHardware,
            IntPtr.Zero,
            D3D11CreateDeviceBgraSupport | D3D11CreateDeviceVideoSupport,
            IntPtr.Zero,
            0,
            D3D11SdkVersion,
            out var device,
            out var featureLevel,
            out var context);

        if (hr < 0 || device == IntPtr.Zero)
        {
            return (false, 0, $"D3D11CreateDevice failed (0x{hr:X8}).");
        }

        if (context != IntPtr.Zero)
        {
            Marshal.Release(context);
        }

        Marshal.Release(device);
        return (true, featureLevel, $"D3D11 device created at feature level 0x{featureLevel:X}.");
    }

    private static bool TryCreateDeviceManager(out string detail)
    {
        var hr = MediaFoundationInterop.MFCreateDXGIDeviceManager(out _, out var manager);

        if (hr < 0 || manager is null)
        {
            detail = $"MFCreateDXGIDeviceManager failed (0x{hr:X8}).";
            return false;
        }

        Marshal.ReleaseComObject(manager);
        detail = "Media Foundation can create a DXGI device manager.";
        return true;
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(
        IntPtr adapter,
        int driverType,
        IntPtr software,
        uint flags,
        IntPtr featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        out IntPtr device,
        out int featureLevel,
        out IntPtr immediateContext);
}

/// <summary>The one member needed to prove the object can be created; the rest of the interface is unused.</summary>
[ComImport]
[Guid("EB533D5D-2DB6-40F8-97A9-494692014F07")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFDXGIDeviceManager
{
    [PreserveSig]
    int ResetDevice([MarshalAs(UnmanagedType.IUnknown)] object device, uint resetToken);
}
