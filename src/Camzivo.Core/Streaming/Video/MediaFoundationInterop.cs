using System.Runtime.InteropServices;

namespace Camzivo.Core.Streaming.Video;

/// <summary>
/// The parts of Media Foundation this decoder needs: the H.264 decoder MFT, media types, samples and buffers.
///
/// These declarations follow the documented vtable order exactly (mfobjects.h, mftransform.h). Every method is
/// PreserveSig so HRESULTs can be inspected instead of becoming exceptions inside a COM call.
/// </summary>
internal static class MediaFoundationInterop
{
    public const int MfVersion = 0x00020070;

    public const int MfStartupFull = 0;

    public const uint MfETransformNeedMoreInput = 0xC00D6D72;

    public const uint MfETransformStreamChange = 0xC00D6D61;

    public const int MfSinkWriterNoThumbnails = 0;

    public static readonly Guid ClsidH264Decoder = new("62CE7E72-4C71-4D20-B15D-452831A87D9D");

    public static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");

    public static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");

    public static readonly Guid MFVideoFormat_NV12 = new("3231564E-0000-0010-8000-00AA00389B71");

    public static readonly Guid MFVideoFormat_I420 = new("30323449-0000-0010-8000-00AA00389B71");

    public static readonly Guid MFVideoFormat_YV12 = new("32315659-0000-0010-8000-00AA00389B71");

    public static readonly Guid MF_MT_MAJOR_TYPE = new("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F");

    public static readonly Guid MF_MT_SUBTYPE = new("F7E34C9A-42E8-4714-B74B-CB29D72C35E5");

    public static readonly Guid MF_MT_FRAME_SIZE = new("1652C33D-D6B2-4012-B834-72030849A37D");

    public static readonly Guid MF_MT_DEFAULT_STRIDE = new("644B4E48-1E02-4516-B0EB-C01CA9D49AC6");

    public static readonly Guid MF_MT_INTERLACE_MODE = new("E2724BB8-E676-4806-B4B2-A8D6EFB44CCD");

    public static readonly Guid MF_MT_ALL_SAMPLES_INDEPENDENT = new("C9173739-5E56-461C-B713-46FB995CB95F");

    public static readonly Guid MFT_CATEGORY_VIDEO_DECODER = new("D6C02D4B-3833-45A4-8B32-748A9B15C2AB");

    [StructLayout(LayoutKind.Sequential)]
    public struct MftRegisterTypeInfo
    {
        public Guid MajorType;
        public Guid Subtype;
    }

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFTEnumEx(
        Guid category,
        uint flags,
        IntPtr inputType,
        IntPtr outputType,
        out IntPtr activates,
        out uint count);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateDXGIDeviceManager(out uint resetToken, out IMFDXGIDeviceManager deviceManager);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFStartup(int version, int flags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFShutdown();

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateMediaType(out IMFMediaType mediaType);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateSample(out IMFSample sample);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateMemoryBuffer(int maxLength, out IMFMediaBuffer buffer);

    [DllImport("ole32.dll", ExactSpelling = true)]
    public static extern int CoCreateInstance(
        ref Guid clsid,
        IntPtr outer,
        int context,
        ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out object instance);

    public static void ThrowIfFailed(int hr, string operation)
    {
        if (hr < 0)
        {
            throw new VideoDecoderException(
                $"Media Foundation {operation} failed with 0x{hr:X8}.",
                Marshal.GetExceptionForHR(hr));
        }
    }

    public static void PackSize(IMFMediaType type, Guid key, int width, int height) =>
        ThrowIfFailed(type.SetUINT64(key, ((ulong)(uint)width << 32) | (uint)height), "SetUINT64");

    public static (int Width, int Height) UnpackSize(IMFMediaType type, Guid key)
    {
        ThrowIfFailed(type.GetUINT64(key, out var packed), "GetUINT64");
        return ((int)(packed >> 32), (int)(packed & 0xFFFFFFFF));
    }
}

[ComImport]
[Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFAttributes
{
    [PreserveSig] int GetItem(ref Guid key, IntPtr value);
    [PreserveSig] int GetItemType(ref Guid key, out int type);
    [PreserveSig] int CompareItem(ref Guid key, IntPtr value, out int result);
    [PreserveSig] int Compare(IMFAttributes theirs, int matchType, out int result);
    [PreserveSig] int GetUINT32(ref Guid key, out int value);
    [PreserveSig] int GetUINT64(ref Guid key, out ulong value);
    [PreserveSig] int GetDouble(ref Guid key, out double value);
    [PreserveSig] int GetGUID(ref Guid key, out Guid value);
    [PreserveSig] int GetStringLength(ref Guid key, out int length);
    [PreserveSig] int GetString(ref Guid key, [Out, MarshalAs(UnmanagedType.LPWStr)] string value, int size, IntPtr length);
    [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out int length);
    [PreserveSig] int GetBlobSize(ref Guid key, out int size);
    [PreserveSig] int GetBlob(ref Guid key, [Out] byte[] buffer, int size, IntPtr blobSize);
    [PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out int size);
    [PreserveSig] int GetUnknown(ref Guid key, ref Guid iid, out IntPtr value);
    [PreserveSig] int SetItem(ref Guid key, IntPtr value);
    [PreserveSig] int DeleteItem(ref Guid key);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32(ref Guid key, int value);
    [PreserveSig] int SetUINT64(ref Guid key, ulong value);
    [PreserveSig] int SetDouble(ref Guid key, double value);
    [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
    [PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [PreserveSig] int SetBlob(ref Guid key, byte[] buffer, int size);
    [PreserveSig] int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetItemByIndex(int index, out Guid key, IntPtr value);
    [PreserveSig] int CopyAllItems(IMFAttributes destination);
}

[ComImport]
[Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaType
{
    // IMFAttributes
    [PreserveSig] int GetItem(ref Guid key, IntPtr value);
    [PreserveSig] int GetItemType(ref Guid key, out int type);
    [PreserveSig] int CompareItem(ref Guid key, IntPtr value, out int result);
    [PreserveSig] int Compare(IMFAttributes theirs, int matchType, out int result);
    [PreserveSig] int GetUINT32(ref Guid key, out int value);
    [PreserveSig] int GetUINT64(ref Guid key, out ulong value);
    [PreserveSig] int GetDouble(ref Guid key, out double value);
    [PreserveSig] int GetGUID(ref Guid key, out Guid value);
    [PreserveSig] int GetStringLength(ref Guid key, out int length);
    [PreserveSig] int GetString(ref Guid key, [Out, MarshalAs(UnmanagedType.LPWStr)] string value, int size, IntPtr length);
    [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out int length);
    [PreserveSig] int GetBlobSize(ref Guid key, out int size);
    [PreserveSig] int GetBlob(ref Guid key, [Out] byte[] buffer, int size, IntPtr blobSize);
    [PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out int size);
    [PreserveSig] int GetUnknown(ref Guid key, ref Guid iid, out IntPtr value);
    [PreserveSig] int SetItem(ref Guid key, IntPtr value);
    [PreserveSig] int DeleteItem(ref Guid key);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32(ref Guid key, int value);
    [PreserveSig] int SetUINT64(ref Guid key, ulong value);
    [PreserveSig] int SetDouble(ref Guid key, double value);
    [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
    [PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [PreserveSig] int SetBlob(ref Guid key, byte[] buffer, int size);
    [PreserveSig] int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetItemByIndex(int index, out Guid key, IntPtr value);
    [PreserveSig] int CopyAllItems(IMFAttributes destination);

    // IMFMediaType
    [PreserveSig] int GetMajorType(out Guid majorType);
    [PreserveSig] int IsCompressedFormat(out int compressed);
    [PreserveSig] int IsEqual(IMFMediaType other, out int flags);
    [PreserveSig] int GetRepresentation(ref Guid representation, out IntPtr value);
    [PreserveSig] int FreeRepresentation(ref Guid representation, IntPtr value);
}

[ComImport]
[Guid("045FA593-8799-42B8-BC8D-8968C6453507")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaBuffer
{
    [PreserveSig] int Lock(out IntPtr buffer, out int maxLength, out int currentLength);
    [PreserveSig] int Unlock();
    [PreserveSig] int GetCurrentLength(out int length);
    [PreserveSig] int SetCurrentLength(int length);
    [PreserveSig] int GetMaxLength(out int length);
}

[ComImport]
[Guid("C40A00F2-B93A-4D80-AE8C-5A1C634F58E4")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSample
{
    // IMFAttributes
    [PreserveSig] int GetItem(ref Guid key, IntPtr value);
    [PreserveSig] int GetItemType(ref Guid key, out int type);
    [PreserveSig] int CompareItem(ref Guid key, IntPtr value, out int result);
    [PreserveSig] int Compare(IMFAttributes theirs, int matchType, out int result);
    [PreserveSig] int GetUINT32(ref Guid key, out int value);
    [PreserveSig] int GetUINT64(ref Guid key, out ulong value);
    [PreserveSig] int GetDouble(ref Guid key, out double value);
    [PreserveSig] int GetGUID(ref Guid key, out Guid value);
    [PreserveSig] int GetStringLength(ref Guid key, out int length);
    [PreserveSig] int GetString(ref Guid key, [Out, MarshalAs(UnmanagedType.LPWStr)] string value, int size, IntPtr length);
    [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out int length);
    [PreserveSig] int GetBlobSize(ref Guid key, out int size);
    [PreserveSig] int GetBlob(ref Guid key, [Out] byte[] buffer, int size, IntPtr blobSize);
    [PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out int size);
    [PreserveSig] int GetUnknown(ref Guid key, ref Guid iid, out IntPtr value);
    [PreserveSig] int SetItem(ref Guid key, IntPtr value);
    [PreserveSig] int DeleteItem(ref Guid key);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32(ref Guid key, int value);
    [PreserveSig] int SetUINT64(ref Guid key, ulong value);
    [PreserveSig] int SetDouble(ref Guid key, double value);
    [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
    [PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [PreserveSig] int SetBlob(ref Guid key, byte[] buffer, int size);
    [PreserveSig] int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetItemByIndex(int index, out Guid key, IntPtr value);
    [PreserveSig] int CopyAllItems(IMFAttributes destination);

    // IMFSample
    [PreserveSig] int GetSampleFlags(out int flags);
    [PreserveSig] int SetSampleFlags(int flags);
    [PreserveSig] int GetSampleTime(out long time);
    [PreserveSig] int SetSampleTime(long time);
    [PreserveSig] int GetSampleDuration(out long duration);
    [PreserveSig] int SetSampleDuration(long duration);
    [PreserveSig] int GetBufferCount(out int count);
    [PreserveSig] int GetBufferByIndex(int index, out IMFMediaBuffer buffer);
    [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
    [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
    [PreserveSig] int RemoveBufferByIndex(int index);
    [PreserveSig] int RemoveAllBuffers();
    [PreserveSig] int GetTotalLength(out int length);
    [PreserveSig] int CopyToBuffer(IMFMediaBuffer buffer);
}

[StructLayout(LayoutKind.Sequential)]
internal struct MftOutputStreamInfo
{
    public uint Flags;
    public uint Size;
    public uint Alignment;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MftOutputDataBuffer
{
    public uint StreamId;
    public IMFSample? Sample;
    public uint Status;
    public IntPtr Events;
}

internal static class MftOutputFlags
{
    public const uint ProvideSamples = 0x00000100;
}

[ComImport]
[Guid("BF94C121-5B05-4E6F-8000-BA598961414D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFTransform
{
    [PreserveSig] int GetStreamLimits(out int inputMin, out int inputMax, out int outputMin, out int outputMax);
    [PreserveSig] int GetStreamCount(out int inputs, out int outputs);
    [PreserveSig] int GetStreamIDs(int inputIdCount, int[] inputIds, int outputIdCount, int[] outputIds);
    [PreserveSig] int GetInputStreamInfo(int inputId, out MftInputStreamInfo info);
    [PreserveSig] int GetOutputStreamInfo(int outputId, out MftOutputStreamInfo info);
    [PreserveSig] int GetAttributes(out IMFAttributes attributes);
    [PreserveSig] int GetInputStreamAttributes(int inputId, out IMFAttributes attributes);
    [PreserveSig] int GetOutputStreamAttributes(int outputId, out IMFAttributes attributes);
    [PreserveSig] int DeleteInputStream(int streamId);
    [PreserveSig] int AddInputStreams(int streamCount, int[] streamIds);
    [PreserveSig] int GetInputAvailableType(int inputId, int typeIndex, out IMFMediaType type);
    [PreserveSig] int GetOutputAvailableType(int outputId, int typeIndex, out IMFMediaType type);
    [PreserveSig] int SetInputType(int inputId, IMFMediaType? type, uint flags);
    [PreserveSig] int SetOutputType(int outputId, IMFMediaType? type, uint flags);
    [PreserveSig] int GetInputCurrentType(int inputId, out IMFMediaType type);
    [PreserveSig] int GetOutputCurrentType(int outputId, out IMFMediaType type);
    [PreserveSig] int GetInputStatus(int inputId, out uint flags);
    [PreserveSig] int GetOutputStatus(out uint flags);
    [PreserveSig] int SetOutputBounds(long lower, long upper);
    [PreserveSig] int ProcessEvent(int inputId, IntPtr @event);
    [PreserveSig] int ProcessMessage(int message, IntPtr param);
    [PreserveSig] int ProcessInput(int inputId, IMFSample sample, uint flags);
    [PreserveSig] int ProcessOutput(uint flags, int outputBufferCount, ref MftOutputDataBuffer outputBuffers, out uint status);
}

[StructLayout(LayoutKind.Sequential)]
internal struct MftInputStreamInfo
{
    public uint Flags;
    public uint Size;
    public uint MaxLatency;
    public uint MinLatency;
    public uint Alignment;
}
