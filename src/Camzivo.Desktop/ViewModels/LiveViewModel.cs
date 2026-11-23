using System.Collections.ObjectModel;
using System.Windows.Input;
using Camzivo.Core.Layout;
using Camzivo.Core.Streaming;
using Camzivo.Core.Streaming.Video;
using Camzivo.Desktop.Services;

namespace Camzivo.Desktop.ViewModels;

public sealed class CameraListEntry(Guid id, string name, string group, StreamUriSet? uris = null) : ObservableObject
{
    private bool _isAssigned;

    public Guid Id { get; } = id;

    public string Name { get; } = name;

    public string Group { get; } = group;

    /// <summary>Stream addresses discovered at onboarding; empty means the camera cannot be watched yet.</summary>
    public StreamUriSet Uris { get; } = uris ?? StreamUriSet.Empty;

    public bool IsAssigned
    {
        get => _isAssigned;
        set => Set(ref _isAssigned, value);
    }
}

/// <summary>A named wall the operator saved. Slots are camera identities in tile order.</summary>
public sealed record SavedLayoutOption(Guid Id, string Name, int GridSlots, IReadOnlyList<Guid?> Slots);

public sealed class LayoutOptionViewModel(int count, string label, Action<int> apply) : ObservableObject
{
    private bool _isSelected;

    public int Count { get; } = count;

    public string Label { get; } = label;

    public string Tooltip { get; } = $"{count} tile layout (Ctrl+{(count == 1 ? 1 : Math.Min(8, Array.IndexOf(LayoutCounts, count) + 1))})";

    public ICommand Command { get; } = new RelayCommand(_ => apply(count));

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    private static readonly int[] LayoutCounts = [1, 4, 9, 16, 25, 36, 49, 64];
}

/// <summary>
/// Live View state. Layout geometry, stream quality and decode scheduling come from Camzivo.Core, so the wall shows
/// the same decisions the real engine will make. Nothing here claims to be live footage: demo tiles report
/// PREVIEW, and tiles past the decode budget report QUEUED.
/// </summary>
public sealed class LiveViewModel : ObservableObject
{
    public const int DecodeBudget = 16;

    public const string EmptySlotHint = "CLICK A CAMERA TO ASSIGN";

    /// <summary>Clipboard format used when a camera is dragged from the list onto the wall.</summary>
    public const string CameraDragFormat = "Camzivo.CameraId";

    private readonly List<CameraListEntry> _cameras = [];
    private readonly List<Guid?[]> _undo = [];
    private readonly Dictionary<int, (int Width, int Height)> _tileSizes = [];
    private readonly bool _demo;
    private bool _wallSuspended;
    private DateTime _lastSizeRefresh = DateTime.MinValue;
    private readonly ObservableCollection<CameraTileViewModel> _tiles = [];
    private readonly ObservableCollection<SavedLayoutOption> _layouts = [];
    private StreamEngine? _engine;
    private SavedLayoutOption? _selectedLayout;
    private bool _isLayoutEditorOpen;
    private string _layoutEditorName = string.Empty;
    private string _layoutEditorTitle = string.Empty;
    private string? _layoutEditorError;
    private LayoutEditorMode _layoutEditorMode;
    private StreamTargetResolver? _resolver;
    private Guid?[] _slotAssignments = [];
    private string _searchText = string.Empty;
    private LayoutPreset _preset = LayoutPreset.Nine;
    private int _rows = 3;
    private int _columns = 3;
    private string _layoutSummary = string.Empty;
    private string _cameraCountText = string.Empty;
    private int _selectedSlot;
    private bool _hasNoMatches;

    public LiveViewModel(bool demo)
    {
        _demo = demo;

        LayoutOptions =
        [
            new LayoutOptionViewModel(1, "1", ApplyLayout),
            new LayoutOptionViewModel(4, "4", ApplyLayout),
            new LayoutOptionViewModel(9, "9", ApplyLayout),
            new LayoutOptionViewModel(16, "16", ApplyLayout),
            new LayoutOptionViewModel(25, "25", ApplyLayout),
            new LayoutOptionViewModel(36, "36", ApplyLayout),
            new LayoutOptionViewModel(49, "49", ApplyLayout),
            new LayoutOptionViewModel(64, "64", ApplyLayout),
        ];

        SelectSlotCommand = new RelayCommand(parameter =>
        {
            if (parameter is CameraTileViewModel tile)
            {
                SelectSlot(tile.SlotIndex);
            }
            else if (int.TryParse(parameter?.ToString(), out var slot))
            {
                SelectSlot(slot);
            }
        });

        AssignCommand = new RelayCommand(parameter =>
        {
            if (parameter is CameraListEntry entry)
            {
                Assign(entry);
            }
        });

        ClearSelectedCommand = new RelayCommand(_ => ClearSelected(), _ => CanClearSelected);

        UndoCommand = new RelayCommand(_ => Undo(), _ => _undo.Count > 0);

        // A snapshot is only offered when the selected tile is showing a decoded frame. A button that saves a blank
        // image would be worse than a disabled one.
        SnapshotCommand = new RelayCommand(
            _ =>
            {
                if (SelectedCameraId is { } id)
                {
                    SnapshotRequested?.Invoke(id);
                }
            },
            _ => CanSnapshot);

        BeginSaveLayoutCommand = new RelayCommand(_ => BeginLayoutEditor(LayoutEditorMode.Save));
        BeginRenameLayoutCommand = new RelayCommand(
            _ => BeginLayoutEditor(LayoutEditorMode.Rename),
            _ => SelectedLayout is not null);
        BeginDuplicateLayoutCommand = new RelayCommand(
            _ => BeginLayoutEditor(LayoutEditorMode.Duplicate),
            _ => SelectedLayout is not null);
        DeleteLayoutCommand = new RelayCommand(
            _ =>
            {
                if (SelectedLayout is { } layout)
                {
                    DeleteLayoutRequested?.Invoke(layout.Name);
                }
            },
            _ => SelectedLayout is not null);
        ConfirmLayoutCommand = new RelayCommand(_ => ConfirmLayout(), _ => IsLayoutEditorOpen);
        CancelLayoutCommand = new RelayCommand(_ => CloseLayoutEditor(), _ => IsLayoutEditorOpen);
        ApplySavedLayoutCommand = new RelayCommand(parameter =>
        {
            if (parameter is SavedLayoutOption layout)
            {
                ApplyLayoutRequested?.Invoke(layout);
            }
        });
        OpenOnDisplayCommand = new RelayCommand(_ => OpenWallOnDisplayRequested?.Invoke());
        SoloCommand = new RelayCommand(
            parameter =>
            {
                if (parameter is CameraTileViewModel tile)
                {
                    Solo(tile.SlotIndex);
                }
            },
            parameter => parameter is CameraTileViewModel { CameraId: not null });

        foreach (var option in LayoutOptions)
        {
            option.IsSelected = option.Count == 9;
        }

        if (demo)
        {
            // Route demo cameras through the same placement path as real devices, so the sample wall exercises
            // the production rule instead of a special case that can drift.
            LoadCameras(DemoCameras);
        }
        else
        {
            Refresh();
        }
    }

    /// <summary>Resolves a camera to a playable address and credentials (never inside the view model).</summary>
    public delegate StreamTarget? StreamTargetResolver(CameraListEntry camera, StreamQuality quality);

    public ObservableCollection<CameraTileViewModel> Tiles => _tiles;

    public ObservableCollection<SavedLayoutOption> Layouts => _layouts;

    public SavedLayoutOption? SelectedLayout
    {
        get => _selectedLayout;
        set
        {
            if (!Set(ref _selectedLayout, value))
            {
                return;
            }

            Raise(nameof(LayoutSelectionLabel));
            RefreshLayoutCommands();
        }
    }

    public string LayoutSelectionLabel => SelectedLayout?.Name ?? (Layouts.Count == 0 ? "No saved layouts" : "Choose a layout");

    public bool HasLayouts => Layouts.Count > 0;

    /// <summary>Inline naming panel; no modal dialog for a one-field action.</summary>
    public bool IsLayoutEditorOpen
    {
        get => _isLayoutEditorOpen;
        private set
        {
            if (Set(ref _isLayoutEditorOpen, value))
            {
                ConfirmLayoutCommand.RaiseCanExecuteChanged();
                CancelLayoutCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string LayoutEditorTitle
    {
        get => _layoutEditorTitle;
        private set => Set(ref _layoutEditorTitle, value);
    }

    public string LayoutEditorName
    {
        get => _layoutEditorName;
        set
        {
            if (Set(ref _layoutEditorName, value))
            {
                LayoutEditorError = null;
            }
        }
    }

    public string? LayoutEditorError
    {
        get => _layoutEditorError;
        private set
        {
            if (Set(ref _layoutEditorError, value))
            {
                Raise(nameof(HasLayoutEditorError));
            }
        }
    }

    public bool HasLayoutEditorError => !string.IsNullOrEmpty(LayoutEditorError);

    public RelayCommand BeginSaveLayoutCommand { get; }

    public RelayCommand BeginRenameLayoutCommand { get; }

    public RelayCommand BeginDuplicateLayoutCommand { get; }

    public RelayCommand DeleteLayoutCommand { get; }

    public RelayCommand ConfirmLayoutCommand { get; }

    public RelayCommand CancelLayoutCommand { get; }

    public RelayCommand ApplySavedLayoutCommand { get; }

    public RelayCommand OpenOnDisplayCommand { get; }

    /// <summary>Switches the wall to one camera without losing the other placements.</summary>
    public RelayCommand SoloCommand { get; }

    public void Solo(int slot)
    {
        if (slot < 0 || slot >= _slotAssignments.Length || _slotAssignments[slot] is not { } cameraId)
        {
            return;
        }

        PushUndo();

        var existing = Array.IndexOf(_slotAssignments, (Guid?)cameraId);
        if (existing >= 0)
        {
            _slotAssignments[existing] = null;
        }

        _slotAssignments[0] = cameraId;
        ApplyLayout(1);
        SelectedSlot = 0;
    }

    /// <summary>The shell persists layouts; the wall only says what to store or apply.</summary>
    public event Action<string, IReadOnlyList<Guid?>, int>? SaveLayoutRequested;

    public event Action<string, string>? RenameLayoutRequested;

    public event Action<string, string>? DuplicateLayoutRequested;

    public event Action<string>? DeleteLayoutRequested;

    public event Action<SavedLayoutOption>? ApplyLayoutRequested;

    public event Action? OpenWallOnDisplayRequested;

    /// <summary>Applies a stored layout: grid first, then the tile assignment.</summary>
    public void ApplySavedLayout(SavedLayoutOption layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        ApplyLayout(layout.GridSlots);
        RestoreWall(layout.Slots);
        SelectedLayout = layout;
    }

    public void SetLayouts(IEnumerable<SavedLayoutOption> layouts, string? selectedName = null)
    {
        ArgumentNullException.ThrowIfNull(layouts);

        _layouts.Clear();
        foreach (var layout in layouts)
        {
            _layouts.Add(layout);
        }

        SelectedLayout = _layouts.FirstOrDefault(
            layout => string.Equals(layout.Name, selectedName, StringComparison.OrdinalIgnoreCase));

        Raise(nameof(HasLayouts));
        Raise(nameof(LayoutSelectionLabel));
        RefreshLayoutCommands();
    }

    /// <summary>Shows a rejected layout operation in the still-open editor instead of a message box.</summary>
    public void ShowLayoutError(string message)
    {
        IsLayoutEditorOpen = true;
        LayoutEditorError = message;
    }

    public int GridSlots => LayoutGrid.FromPreset(_preset).SlotCount;

    public IReadOnlyList<Guid?> CaptureWall() => _slotAssignments.ToArray();

    private void BeginLayoutEditor(LayoutEditorMode mode)
    {
        _layoutEditorMode = mode;
        LayoutEditorTitle = mode switch
        {
            LayoutEditorMode.Save => "Save this wall as",
            LayoutEditorMode.Rename => "Rename layout",
            _ => "Duplicate layout as",
        };

        LayoutEditorName = mode switch
        {
            LayoutEditorMode.Save => SuggestLayoutName(),
            _ => SelectedLayout?.Name ?? string.Empty,
        };

        LayoutEditorError = null;
        IsLayoutEditorOpen = true;
    }

    private string SuggestLayoutName()
    {
        for (var index = 1; index <= 40; index++)
        {
            var candidate = $"Layout {index}";
            if (Layouts.All(layout => !string.Equals(layout.Name, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }

        return "Layout";
    }

    private void ConfirmLayout()
    {
        var name = LayoutEditorName.Trim();

        if (name.Length == 0)
        {
            LayoutEditorError = "A layout needs a name.";
            return;
        }

        if (name.Length > 60)
        {
            LayoutEditorError = "A layout name can be at most 60 characters.";
            return;
        }

        switch (_layoutEditorMode)
        {
            case LayoutEditorMode.Save:
                SaveLayoutRequested?.Invoke(name, CaptureWall(), GridSlots);
                break;

            case LayoutEditorMode.Rename when SelectedLayout is { } rename:
                RenameLayoutRequested?.Invoke(rename.Name, name);
                break;

            case LayoutEditorMode.Duplicate when SelectedLayout is { } duplicate:
                DuplicateLayoutRequested?.Invoke(duplicate.Name, name);
                break;
        }

        CloseLayoutEditor();
    }

    private void CloseLayoutEditor()
    {
        IsLayoutEditorOpen = false;
        LayoutEditorError = null;
    }

    private void RefreshLayoutCommands()
    {
        BeginRenameLayoutCommand.RaiseCanExecuteChanged();
        BeginDuplicateLayoutCommand.RaiseCanExecuteChanged();
        DeleteLayoutCommand.RaiseCanExecuteChanged();
    }

    private enum LayoutEditorMode
    {
        Save,
        Rename,
        Duplicate,
    }

    /// <summary>
    /// Binds the video engine. Tiles only reach LIVE when a frame is presented, so a wall with an unreachable camera
    /// shows WAITING or OFFLINE with the reason rather than a stale picture.
    /// </summary>
    public void BindStreams(StreamEngine engine, StreamTargetResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(engine);

        if (_engine is not null)
        {
            _engine.FramePresented -= OnFramePresented;
            _engine.StatusChanged -= OnStreamStatus;
        }

        _engine = engine;
        _resolver = resolver;
        _engine.FramePresented += OnFramePresented;
        _engine.StatusChanged += OnStreamStatus;

        Refresh();
    }

    public ObservableCollection<CameraListEntry> FilteredCameras { get; } = [];

    public IReadOnlyList<LayoutOptionViewModel> LayoutOptions { get; }

    public ICommand SelectSlotCommand { get; }

    public ICommand AssignCommand { get; }

    public ICommand ClearSelectedCommand { get; }

    /// <summary>Reverts the last placement, clear or layout change (20 steps, memory only).</summary>
    public ICommand UndoCommand { get; }

    public ICommand SnapshotCommand { get; }

    /// <summary>The shell owns the file system; the wall only says which camera was asked for.</summary>
    public event Action<Guid>? SnapshotRequested;

    public CameraTileViewModel? SelectedTile =>
        SelectedSlot >= 0 && SelectedSlot < _tiles.Count ? _tiles[SelectedSlot] : null;

    public Guid? SelectedCameraId =>
        SelectedSlot >= 0 && SelectedSlot < _slotAssignments.Length ? _slotAssignments[SelectedSlot] : null;

    /// <summary>True only when the selected tile has a presented frame to save.</summary>
    public bool CanSnapshot => SelectedTile?.LastFrame is not null;

    /// <summary>True when a search is active and nothing matches — the tree must say so instead of looking empty.</summary>
    public bool HasNoMatches
    {
        get => _hasNoMatches;
        private set => Set(ref _hasNoMatches, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value))
            {
                ApplyFilter();
            }
        }
    }

    public int Rows
    {
        get => _rows;
        private set => Set(ref _rows, value);
    }

    public int Columns
    {
        get => _columns;
        private set => Set(ref _columns, value);
    }

    public string LayoutSummary
    {
        get => _layoutSummary;
        private set => Set(ref _layoutSummary, value);
    }

    public string CameraCountText
    {
        get => _cameraCountText;
        private set => Set(ref _cameraCountText, value);
    }

    public LayoutPreset Preset => _preset;

    public int SelectedSlot
    {
        get => _selectedSlot;
        private set
        {
            if (Set(ref _selectedSlot, value))
            {
                foreach (var tile in _tiles)
                {
                    tile.IsSelected = tile.SlotIndex == value;
                }

                Raise(nameof(CanClearSelected));
                Raise(nameof(SelectedTile));
                Raise(nameof(SelectedCameraId));
                Raise(nameof(CanSnapshot));
                (ClearSelectedCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (SnapshotCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanClearSelected =>
        SelectedSlot >= 0 &&
        SelectedSlot < _slotAssignments.Length &&
        _slotAssignments[SelectedSlot] is not null;

    public bool HasCameras => _cameras.Count > 0;

    /// <summary>Drives the wall-level onboarding state: a first run must explain what to do, not show nine hints.</summary>
    public bool ShowOnboarding => _cameras.Count == 0;

    /// <summary>Replaces the camera list, keeping the operator's placement where the camera still exists.</summary>
    public void LoadCameras(IEnumerable<CameraListEntry> cameras)
    {
        _cameras.Clear();
        _cameras.AddRange(cameras);

        // Placement needs the slot array sized for the current layout before it can look for empty slots.
        EnsureAssignmentCapacity(LayoutGrid.FromPreset(_preset).SlotCount);

        var known = _cameras.Select(camera => camera.Id).ToHashSet();
        for (var slot = 0; slot < _slotAssignments.Length; slot++)
        {
            if (_slotAssignments[slot] is { } id && !known.Contains(id))
            {
                _slotAssignments[slot] = null;
            }
        }

        // Fill empty slots in order so a new device appears on the wall without the operator having to place it.
        // Only visible slots are filled: auto-placing into a tile the current layout does not show would start a
        // decoder nobody can see.
        var visibleSlots = LayoutGrid.FromPreset(_preset).SlotCount;
        var placed = _slotAssignments.Where(id => id is not null).Select(id => id!.Value).ToHashSet();
        var unplaced = _cameras.Where(camera => !placed.Contains(camera.Id)).ToList();

        for (var slot = 0; slot < visibleSlots && unplaced.Count > 0; slot++)
        {
            if (_slotAssignments[slot] is null)
            {
                _slotAssignments[slot] = unplaced[0].Id;
                unplaced.RemoveAt(0);
            }
        }

        Refresh();
    }

    public void ApplyLayout(int count)
    {
        _preset = count switch
        {
            1 => LayoutPreset.Single,
            4 => LayoutPreset.Four,
            9 => LayoutPreset.Nine,
            16 => LayoutPreset.Sixteen,
            25 => LayoutPreset.TwentyFive,
            36 => LayoutPreset.ThirtySix,
            49 => LayoutPreset.FortyNine,
            64 => LayoutPreset.SixtyFour,
            _ => LayoutPreset.Nine,
        };

        foreach (var option in LayoutOptions)
        {
            option.IsSelected = option.Count == count;
        }

        Refresh();
    }

    /// <summary>
    /// Places a camera: first empty slot, otherwise the selected slot (so the operator can replace a tile
    /// deliberately). This is the click path that the empty slots advertise.
    /// </summary>
    public void Assign(CameraListEntry entry)
    {
        var target = SlotAssignment.NextTarget(_slotAssignments, SelectedSlot);
        if (target == SlotAssignment.NoSlot)
        {
            return;
        }

        PushUndo();

        // A camera already on the wall must move, not appear twice: the scheduler allocates one decoder per camera,
        // so a duplicate tile would display a state it does not own.
        var existing = SlotAssignment.ExistingSlot(_slotAssignments, entry.Id);
        if (existing != SlotAssignment.NoSlot && existing != target)
        {
            _slotAssignments[existing] = null;
        }

        _slotAssignments[target] = entry.Id;
        SelectedSlot = target;
        Refresh();
    }

    /// <summary>
    /// Places a camera on a specific tile, which is what a drop means. Moving an already placed camera swaps it with
    /// whatever occupied the target (or simply moves it into an empty tile); dropping a camera from the list onto an
    /// occupied tile replaces that tile's camera. A camera can never occupy two tiles: the scheduler allocates one
    /// decoder per camera and a duplicate tile would display a stream it does not own.
    ///
    /// Returns false when nothing changed, so a cancelled or redundant drop leaves the wall (and undo history) alone.
    /// </summary>
    public bool PlaceCamera(Guid cameraId, int slot)
    {
        if (slot < 0 || slot >= _slotAssignments.Length)
        {
            return false;
        }

        if (!_cameras.Any(camera => camera.Id == cameraId))
        {
            // A device removed while the drag was in flight is an invalid drop, not a crash.
            return false;
        }

        if (_slotAssignments[slot] == cameraId)
        {
            return false;
        }

        var from = Array.IndexOf(_slotAssignments, (Guid?)cameraId);
        var occupant = _slotAssignments[slot];

        PushUndo();

        _slotAssignments[slot] = cameraId;

        if (from >= 0)
        {
            // Swap: the displaced camera takes the dragged camera's old tile.
            _slotAssignments[from] = occupant;
        }

        SelectedSlot = slot;
        Refresh();
        return true;
    }

    /// <summary>
    /// The view reports actual tile pixels. Quality follows the tile the operator can actually see, not a fixed
    /// 1920×1080 assumption: a 220-pixel tile has no use for a 1920×1080 main stream.
    ///
    /// Only a decision change triggers a re-plan, and never more than four times a second, so a drag-resize cannot
    /// churn decoder restarts.
    /// </summary>
    public void UpdateTileSize(int slot, double width, double height)
    {
        if (slot < 0 || width < 1 || height < 1)
        {
            return;
        }

        var next = ((int)Math.Round(width), (int)Math.Round(height));
        if (_tileSizes.TryGetValue(slot, out var previous) &&
            Math.Abs(previous.Width - next.Item1) < 8 &&
            Math.Abs(previous.Height - next.Item2) < 8)
        {
            return;
        }

        _tileSizes[slot] = next;

        if (DateTime.UtcNow - _lastSizeRefresh < TimeSpan.FromMilliseconds(250))
        {
            return;
        }

        if (!QualityWouldChange())
        {
            return;
        }

        _lastSizeRefresh = DateTime.UtcNow;
        Refresh();
    }

    /// <summary>Minimising the window stops every decode: nothing is on screen, so nothing should be decoded.</summary>
    public void SetWallSuspended(bool suspended)
    {
        if (_wallSuspended == suspended)
        {
            return;
        }

        _wallSuspended = suspended;
        Refresh();
    }

    /// <summary>True when a reported tile size would pick a different stream than the current one.</summary>
    private bool QualityWouldChange()
    {
        var grid = LayoutGrid.FromPreset(_preset);
        var byId = _cameras.ToDictionary(camera => camera.Id);

        for (var slot = 0; slot < Math.Min(grid.SlotCount, _slotAssignments.Length); slot++)
        {
            if (_slotAssignments[slot] is not { } id || !byId.TryGetValue(id, out var camera) || !camera.Uris.HasAny)
            {
                continue;
            }

            var (width, height) = TileSizeFor(slot, grid);
            var selection = StreamSelectionPolicy.Select(new StreamSelectionContext(
                StreamQuality.Auto,
                IsFullScreen: grid.SlotCount == 1,
                width,
                height,
                IsVisible: true,
                camera.Uris));

            var tile = slot < _tiles.Count ? _tiles[slot] : null;
            var current = tile?.QualityChip == "MAIN" ? StreamQuality.Main : StreamQuality.Sub;

            if (selection.HasStream && selection.Quality != current)
            {
                return true;
            }
        }

        return false;
    }

    private (int Width, int Height) TileSizeFor(int slot, LayoutGrid grid) =>
        _tileSizes.TryGetValue(slot, out var size)
            ? size
            : ((int)(1920.0 / grid.Columns), (int)(1080.0 / grid.Rows));

    /// <summary>Snapshot of the wall for persistence (slot order, empty slots as null).</summary>
    public Guid?[] ExportWall() => (Guid?[])_slotAssignments.Clone();

    /// <summary>
    /// Applies a stored wall. Cameras that no longer exist are dropped (a deleted device must not leave a phantom
    /// tile) and duplicates collapse to one tile, so a hand-edited preference cannot confuse the scheduler.
    /// </summary>
    public void RestoreWall(IReadOnlyList<Guid?> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);

        PushUndo();

        var grid = LayoutGrid.FromPreset(_preset);
        EnsureAssignmentCapacity(Math.Max(grid.SlotCount, slots.Count));

        var known = _cameras.Select(camera => camera.Id).ToHashSet();
        var restored = new Guid?[_slotAssignments.Length];
        var seen = new HashSet<Guid>();

        for (var index = 0; index < slots.Count && index < restored.Length; index++)
        {
            if (slots[index] is { } id && known.Contains(id) && seen.Add(id))
            {
                restored[index] = id;
            }
        }

        _slotAssignments = restored;
        Refresh();
    }

    public void ClearSelected()
    {
        if (!CanClearSelected)
        {
            return;
        }

        PushUndo();
        _slotAssignments[SelectedSlot] = null;
        Refresh();
    }

    private void SelectSlot(int slot)
    {
        if (slot < 0 || slot >= _slotAssignments.Length)
        {
            return;
        }

        SelectedSlot = slot;
    }

    private void Refresh()
    {
        var grid = LayoutGrid.FromPreset(_preset);
        Rows = grid.Rows;
        Columns = grid.Columns;

        EnsureAssignmentCapacity(grid.SlotCount);

        var byId = _cameras.ToDictionary(camera => camera.Id);
        var assigned = new List<(int Slot, CameraListEntry Camera)>();
        var activeTargets = new List<(CameraListEntry Camera, StreamQuality Quality)>();
        for (var slot = 0; slot < grid.SlotCount; slot++)
        {
            if (_slotAssignments[slot] is { } id && byId.TryGetValue(id, out var camera))
            {
                assigned.Add((slot, camera));
            }
        }

        var demands = assigned
            .OrderByDescending(pair => pair.Slot == SelectedSlot)
            .Select((pair, index) => new TileDemand(
                pair.Camera.Id,
                IsVisible: true,
                IsFullScreen: grid.SlotCount == 1,
                TileArea: TileSizeFor(pair.Slot, grid).Width * TileSizeFor(pair.Slot, grid).Height,
                LastInteractedTicks: -index))
            .ToList();

        var plan = StreamScheduler.Plan(demands, DecodeBudget);

        SyncTiles(grid.SlotCount);

        for (var slot = 0; slot < grid.SlotCount; slot++)
        {
            var tile = _tiles[slot];

            if (_slotAssignments[slot] is not { } id || !byId.TryGetValue(id, out var camera))
            {
                tile.Clear();
                tile.IsSelected = slot == SelectedSlot;
                continue;
            }

            var (tileWidth, tileHeight) = TileSizeFor(slot, grid);
            var selection = StreamSelectionPolicy.Select(new StreamSelectionContext(
                StreamQuality.Auto,
                IsFullScreen: grid.SlotCount == 1,
                tileWidth,
                tileHeight,
                IsVisible: true,
                camera.Uris));

            var isActive = plan.IsActive(camera.Id) && !_wallSuspended;

            if (isActive && camera.Uris.HasAny)
            {
                activeTargets.Add((camera, selection.Quality));
            }
            var quality = selection.Quality switch
            {
                StreamQuality.Main => "MAIN",
                StreamQuality.Sub => "SUB",
                _ => "—",
            };

            // Only a presented frame may produce TileStatus.Live. Everything else is queued, waiting, or offline
            // with a reason — the demo path says PREVIEW and never pretends to be a camera.
            var status = _wallSuspended
                ? TileStatus.Queued
                : !isActive
                    ? TileStatus.Queued
                : _demo
                    ? TileStatus.Preview
                    : tile.Status == TileStatus.Live && tile.HasFrame
                        ? TileStatus.Live
                        : TileStatus.Waiting;

            var detail = status switch
            {
                TileStatus.Queued => _wallSuspended ? "SUSPENDED · WINDOW MINIMISED" : "WAITING FOR DECODE SLOT",
                TileStatus.Preview => "SAMPLE CONTENT · NO CAMERA",
                TileStatus.Live => "LIVE",
                _ => _engine is null
                    ? "VIDEO ENGINE NOT STARTED"
                    : camera.Uris.HasAny ? "CONNECTING" : "NO STREAM ADDRESS",
            };

            // Tiles are long-lived: only their content changes. Recreating them on every refresh rebuilt up to 64
            // containers, dropped focus and churned allocations (BUG-04).
            if (tile.CameraId == camera.Id && string.Equals(tile.Name, camera.Name, StringComparison.Ordinal))
            {
                tile.Update(status, quality, detail);
            }
            else
            {
                tile.Assign(camera.Id, camera.Name, status, quality, detail);
            }

            tile.IsSelected = slot == SelectedSlot;
        }

        ApplyStreams(activeTargets);

        var assignedIds = assigned.Select(pair => pair.Camera.Id).ToHashSet();
        foreach (var camera in _cameras)
        {
            camera.IsAssigned = assignedIds.Contains(camera.Id);
        }

        LayoutSummary = $"{grid.Rows} × {grid.Columns} · {grid.SlotCount} slots · {assigned.Count} assigned · decode budget {DecodeBudget}";
        CameraCountText = _cameras.Count == 0
            ? "No cameras yet"
            : $"{(assigned.Count == 0 ? "No cameras placed" : $"{assigned.Count} of {_cameras.Count} placed")}";

        Raise(nameof(HasCameras));
        Raise(nameof(ShowOnboarding));

        Raise(nameof(CanClearSelected));
        Raise(nameof(SelectedTile));
        Raise(nameof(SelectedCameraId));
        Raise(nameof(CanSnapshot));
        (ClearSelectedCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SnapshotCommand as RelayCommand)?.RaiseCanExecuteChanged();
        ApplyFilter();
    }

    /// <summary>Starts streams for active tiles and stops them for everything else (the decode budget is enforced here).</summary>
    private void ApplyStreams(IReadOnlyList<(CameraListEntry Camera, StreamQuality Quality)> active)
    {
        if (_engine is null)
        {
            return;
        }

        var keep = new HashSet<Guid>();

        foreach (var (camera, quality) in active)
        {
            var target = _resolver?.Invoke(camera, quality);
            if (target is null)
            {
                _engine.Detach(camera.Id);
                continue;
            }

            _engine.Attach(camera.Id, target.Uri, target.Username, target.Password);
            keep.Add(camera.Id);
        }

        foreach (var id in _engine.ActiveCameras.ToList())
        {
            if (!keep.Contains(id))
            {
                _engine.Detach(id);
            }
        }
    }

    private void OnFramePresented(Guid cameraId, VideoFrame frame)
    {
        var tile = _tiles.FirstOrDefault(candidate => candidate.CameraId == cameraId);
        if (tile is null)
        {
            return;
        }

        tile.PresentVideoFrame(frame);

        if (tile.Status != TileStatus.Live)
        {
            tile.Update(TileStatus.Live, tile.QualityChip, "LIVE");
        }

        if (tile.SlotIndex == SelectedSlot)
        {
            Raise(nameof(CanSnapshot));
            (SnapshotCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private void OnStreamStatus(Guid cameraId, StreamStatus status)
    {
        var tile = _tiles.FirstOrDefault(candidate => candidate.CameraId == cameraId);
        if (tile is null || status.State == StreamState.Live)
        {
            return;
        }

        var state = status.State == StreamState.Failed ? TileStatus.Offline : TileStatus.Waiting;
        tile.Update(state, tile.QualityChip, status.Detail);
    }

    /// <summary>
    /// Grows the placement array, never shrinks it: an operator who switches to 4 tiles and back to 9 must find the
    /// cameras where they left them, not an empty wall. Hidden slots keep their camera but are not started, because
    /// only the visible slots take part in scheduling.
    /// </summary>
    private void EnsureAssignmentCapacity(int slots)
    {
        if (_slotAssignments.Length >= slots)
        {
            return;
        }

        var next = new Guid?[slots];
        Array.Copy(_slotAssignments, next, _slotAssignments.Length);
        _slotAssignments = next;

        SelectedSlot = Math.Clamp(SelectedSlot, 0, Math.Max(0, slots - 1));
    }

    private void SyncTiles(int slotCount)
    {
        while (_tiles.Count < slotCount)
        {
            _tiles.Add(new CameraTileViewModel(_tiles.Count, null, "Empty", TileStatus.Empty, string.Empty, EmptySlotHint));
        }

        while (_tiles.Count > slotCount)
        {
            _tiles.RemoveAt(_tiles.Count - 1);
        }
    }

    private void PushUndo()
    {
        _undo.Add((Guid?[])_slotAssignments.Clone());
        while (_undo.Count > 20)
        {
            _undo.RemoveAt(0);
        }

        (UndoCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void Undo()
    {
        if (_undo.Count == 0)
        {
            return;
        }

        _slotAssignments = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        Refresh();
        (UndoCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void ApplyFilter()
    {
        var filtered = string.IsNullOrWhiteSpace(_searchText)
            ? _cameras
            : _cameras.Where(camera =>
                camera.Name.Contains(_searchText, StringComparison.OrdinalIgnoreCase) ||
                camera.Group.Contains(_searchText, StringComparison.OrdinalIgnoreCase)).ToList();

        // Reconcile rather than clear-and-refill: clearing destroys the list's keyboard focus on every wall
        // refresh, which made arrow-key placement impossible (BUG-05).
        if (FilteredCameras.Count == filtered.Count && FilteredCameras.SequenceEqual(filtered))
        {
            HasNoMatches = _cameras.Count > 0 && filtered.Count == 0;
            return;
        }

        HasNoMatches = _cameras.Count > 0 && filtered.Count == 0;

        FilteredCameras.Clear();
        foreach (var camera in filtered)
        {
            FilteredCameras.Add(camera);
        }
    }

    private static readonly CameraListEntry[] DemoCameras =
    [
        new(new Guid("11111111-1111-1111-1111-111111111111"), "Gate North", "Perimeter"),
        new(new Guid("22222222-2222-2222-2222-222222222222"), "Loading Bay", "Logistics"),
        new(new Guid("33333333-3333-3333-3333-333333333333"), "Reception", "Interior"),
        new(new Guid("44444444-4444-4444-4444-444444444444"), "Car Park East", "Perimeter"),
        new(new Guid("55555555-5555-5555-5555-555555555555"), "Warehouse Aisle 3", "Logistics"),
        new(new Guid("66666666-6666-6666-6666-666666666666"), "Perimeter West", "Perimeter"),
        new(new Guid("77777777-7777-7777-7777-777777777777"), "Server Room", "Interior"),
        new(new Guid("88888888-8888-8888-8888-888888888888"), "Dock Ramp", "Logistics"),
        new(new Guid("99999999-9999-9999-9999-999999999999"), "Staff Entrance", "Interior"),
    ];
}
