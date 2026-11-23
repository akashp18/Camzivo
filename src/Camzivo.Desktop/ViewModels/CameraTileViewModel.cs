using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Camzivo.Core.Streaming.Video;

namespace Camzivo.Desktop.ViewModels;

public enum TileStatus
{
    Empty = 0,

    /// <summary>Demo/sample content. Never claims to be live footage.</summary>
    Preview,

    Live,
    Queued,

    /// <summary>Placed and scheduled, but no video engine is running yet. Not live, not offline.</summary>
    Waiting,

    Offline,
}

public sealed class CameraTileViewModel : ObservableObject
{
    private TileStatus _status;
    private string _qualityChip;
    private string _detail;
    private bool _isSelected;
    private bool _isDropTarget;
    private WriteableBitmap? _frame;

    public CameraTileViewModel(int slotIndex, Guid? cameraId, string name, TileStatus status, string qualityChip, string detail)
    {
        SlotIndex = slotIndex;
        CameraId = cameraId;
        Name = name;
        _status = status;
        _qualityChip = qualityChip;
        _detail = detail;
    }

    public int SlotIndex { get; }

    public Guid? CameraId { get; private set; }

    public string Name { get; private set; }

    public bool IsEmpty => Status == TileStatus.Empty;

    public bool HasCamera => !IsEmpty;

    /// <summary>Screen-reader label: name, state and stream quality in one phrase.</summary>
    public string AutomationName => IsEmpty
        ? $"Slot {SlotIndex + 1}, empty"
        : $"{Name}, {StatusText}{(string.IsNullOrEmpty(QualityChip) ? string.Empty : ", " + QualityChip)} stream";

    public TileStatus Status
    {
        get => _status;
        private set
        {
            if (Set(ref _status, value))
            {
                Raise(nameof(StatusText));
                Raise(nameof(IsEmpty));
                Raise(nameof(HasCamera));
                Raise(nameof(AutomationName));
            }
        }
    }

    public string QualityChip
    {
        get => _qualityChip;
        private set
        {
            if (Set(ref _qualityChip, value))
            {
                Raise(nameof(AutomationName));
                Raise(nameof(QualityHint));
            }
        }
    }

    public string Detail
    {
        get => _detail;
        private set => Set(ref _detail, value);
    }

    /// <summary>Explains the MAIN/SUB chip, so a soft tile is never a mystery.</summary>
    public string QualityHint => _qualityChip switch
    {
        "MAIN" => "Main stream: full resolution, highest decode cost",
        "SUB" => "Substream: lower resolution, chosen for this tile size",
        _ => "No stream selected for this tile",
    };

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    /// <summary>True while a camera is dragged over this tile, so the drop target is visible before the drop.</summary>
    public bool IsDropTarget
    {
        get => _isDropTarget;
        set => Set(ref _isDropTarget, value);
    }

    /// <summary>
    /// The presented picture. One bitmap is allocated per size and then reused: writing pixels into an existing
    /// WriteableBitmap costs no managed allocation per frame, which is what keeps a 16-tile wall smooth.
    /// </summary>
    /// <summary>
    /// Kept only so the benchmark can measure the previous presentation path (WritePixels) against the current one
    /// on the same build. Off unless a command line asks for it.
    /// </summary>
    public static bool LegacyPresent { get; set; }

    public WriteableBitmap? Frame
    {
        get => _frame;
        private set
        {
            if (Set(ref _frame, value))
            {
                Raise(nameof(HasFrame));
            }
        }
    }

    public bool HasFrame => _frame is not null;

    /// <summary>
    /// The decoded frame behind the bitmap. Kept so a snapshot saves exactly what was presented — the picture on the
    /// tile — instead of asking the decoder for a new one that the operator never saw.
    /// </summary>
    public VideoFrame? LastFrame { get; private set; }

    /// <summary>Called on the UI thread with a decoded frame. Never invents a picture when there is none.</summary>
    public void PresentVideoFrame(VideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        LastFrame = frame;

        if (_frame is null || _frame.PixelWidth != frame.Width || _frame.PixelHeight != frame.Height)
        {
            Frame = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);
        }

        var bitmap = _frame!;
        var stride = frame.Width * 4;

        if (LegacyPresent)
        {
            bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Pixels, frame.Stride, 0);
            return;
        }

        bitmap.Lock();
        try
        {
            if (stride == frame.Stride && stride == bitmap.BackBufferStride)
            {
                // One copy straight into the back buffer. WritePixels would copy twice (into an intermediate buffer,
                // then into the bitmap) and allocate a managed array per frame.
                Marshal.Copy(frame.Pixels, 0, bitmap.BackBuffer, frame.Pixels.Length);
            }
            else
            {
                for (var row = 0; row < frame.Height; row++)
                {
                    Marshal.Copy(
                        frame.Pixels,
                        row * frame.Stride,
                        bitmap.BackBuffer + (row * bitmap.BackBufferStride),
                        stride);
                }
            }

            bitmap.AddDirtyRect(new Int32Rect(0, 0, frame.Width, frame.Height));
        }
        finally
        {
            bitmap.Unlock();
        }
    }

    public string StatusText => Status switch
    {
        TileStatus.Live => "LIVE",
        TileStatus.Preview => "PREVIEW",
        TileStatus.Queued => "QUEUED",
        TileStatus.Waiting => "WAITING",
        TileStatus.Offline => "OFFLINE",
        _ => "UNASSIGNED",
    };

    public void Update(TileStatus status, string qualityChip, string detail)
    {
        Status = status;
        QualityChip = qualityChip;
        Detail = detail;
    }

    public void Assign(Guid cameraId, string name, TileStatus status, string qualityChip, string detail)
    {
        if (CameraId != cameraId)
        {
            // A different camera must not inherit the previous camera's last picture for even one frame.
            Frame = null;
            LastFrame = null;
        }

        CameraId = cameraId;
        Name = name;
        Raise(nameof(Name));
        Update(status, qualityChip, detail);
    }

    public void Clear()
    {
        CameraId = null;
        Name = "Empty";
        Frame = null;
        LastFrame = null;
        Raise(nameof(Name));
        Update(TileStatus.Empty, string.Empty, LiveViewModel.EmptySlotHint);
    }
}
