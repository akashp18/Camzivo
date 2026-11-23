using System.Collections.ObjectModel;
using System.Windows.Input;
using Camzivo.Core.Devices;

namespace Camzivo.Desktop.ViewModels;

public sealed record DeviceRowViewModel(
    Guid Id,
    string Name,
    string Endpoint,
    string Vendor,
    string CapabilitySummary,
    string State,
    bool IsNvr = false,
    int ChannelCount = 0,
    string KindLabel = "CAMERA");

public sealed record ChannelRowViewModel(
    Guid Id,
    Guid DeviceId,
    string Channel,
    string Name,
    string Device,
    string Streams,
    string State);

public sealed class DevicesViewModel : ObservableObject
{
    private string _summary = "Loading devices…";
    private DeviceRowViewModel? _selectedRow;
    private string _searchText = string.Empty;

    public DevicesViewModel(bool demo)
    {
        IsDemo = demo;

        AddCommand = new RelayCommand(_ => RequestAdd());
        DiscoverCommand = new RelayCommand(_ => RequestAdd());
        TestNvrConnectionCommand = new RelayCommand(_ => TestNvrConnectionRequested?.Invoke(this, EventArgs.Empty));

        RemoveCommand = new RelayCommand(parameter =>
        {
            if (parameter is DeviceRowViewModel row && row.Id != Guid.Empty)
            {
                RemoveRequested?.Invoke(this, row.Id);
            }
        });

        ImportChannelsCommand = new RelayCommand(parameter =>
        {
            if (parameter is DeviceRowViewModel { IsNvr: true } row && row.Id != Guid.Empty)
            {
                ImportChannelsRequested?.Invoke(this, row.Id);
            }
        });

        DiagnosticsCommand = new RelayCommand(parameter =>
        {
            if (parameter is DeviceRowViewModel row && row.Id != Guid.Empty)
            {
                DiagnosticsRequested?.Invoke(this, row.Id);
            }
        });

        RemoveChannelCommand = new RelayCommand(parameter =>
        {
            if (parameter is ChannelRowViewModel row)
            {
                RemoveChannelRequested?.Invoke(this, row.Id);
            }
        });

        ReconnectChannelCommand = new RelayCommand(parameter =>
        {
            if (parameter is ChannelRowViewModel row)
            {
                ReconnectChannelRequested?.Invoke(this, row.Id);
            }
        });

        RenameCommand = new RelayCommand(
            parameter =>
            {
                var id = parameter switch
                {
                    DeviceRowViewModel row => row.Id,
                    Guid deviceId => deviceId,
                    _ => Guid.Empty,
                };

                if (id != Guid.Empty)
                {
                    RenameRequested?.Invoke(this, id);
                }
            },
            parameter => parameter is DeviceRowViewModel row && row.Id != Guid.Empty);

        ReconnectDeviceCommand = new RelayCommand(
            parameter =>
            {
                if (parameter is DeviceRowViewModel row && row.Id != Guid.Empty)
                {
                    ReconnectDeviceRequested?.Invoke(this, row.Id);
                }
            });
    }

    public ObservableCollection<DeviceRowViewModel> Rows { get; } = [];

    public ObservableCollection<DeviceRowViewModel> FilteredRows { get; } = [];

    public DeviceRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (!Set(ref _selectedRow, value))
            {
                return;
            }

            Raise(nameof(HasSelection));
            Raise(nameof(SelectedChannels));
            Raise(nameof(SelectedDetail));
            Raise(nameof(SelectedTitle));
        }
    }

    public bool HasSelection => SelectedRow is not null;

    public string SelectedTitle => SelectedRow?.Name ?? "Select a device";

    public string SelectedDetail => SelectedRow is null
        ? "Choose a device to see its capabilities and channels."
        : $"{SelectedRow.KindLabel} · {SelectedRow.Endpoint} · {SelectedRow.CapabilitySummary} · {SelectedRow.State}";

    public IReadOnlyList<ChannelRowViewModel> SelectedChannels => SelectedRow is null
        ? []
        : Channels.Where(channel => channel.DeviceId == SelectedRow.Id).ToList();

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

    /// <summary>Every imported channel, flattened. The recorder row above says how many belong to it.</summary>
    public ObservableCollection<ChannelRowViewModel> Channels { get; } = [];

    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    public bool HasDevices => Rows.Count > 0;

    public bool HasChannels => Channels.Count > 0;

    public bool IsDemo { get; }

    /// <summary>Asks the shell to open the Add Camera dialog; the view model never creates windows.</summary>
    public event EventHandler? AddRequested;

    /// <summary>Asks the shell to remove a device (and its stored password).</summary>
    public event EventHandler<Guid>? RemoveRequested;

    /// <summary>Asks the shell to re-read an existing recorder's channel list.</summary>
    public event EventHandler<Guid>? ImportChannelsRequested;

    /// <summary>Asks the shell to open the read-only compatibility report for a saved device.</summary>
    public event EventHandler<Guid>? DiagnosticsRequested;

    /// <summary>Asks the shell to open the manual, metadata-only recorder connection test.</summary>
    public event EventHandler? TestNvrConnectionRequested;

    public event EventHandler<Guid>? RemoveChannelRequested;

    public event EventHandler<Guid>? ReconnectChannelRequested;

    public ICommand AddCommand { get; }

    public ICommand DiscoverCommand { get; }

    public ICommand RemoveCommand { get; }

    public ICommand ImportChannelsCommand { get; }

    public ICommand DiagnosticsCommand { get; }

    public ICommand TestNvrConnectionCommand { get; }

    public ICommand RemoveChannelCommand { get; }

    public ICommand ReconnectChannelCommand { get; }

    public ICommand RenameCommand { get; }

    public ICommand ReconnectDeviceCommand { get; }

    public event EventHandler<Guid>? RenameRequested;

    public event EventHandler<Guid>? ReconnectDeviceRequested;

    public void RequestAdd() => AddRequested?.Invoke(this, EventArgs.Empty);

    public void Load(IReadOnlyList<Device> devices, IReadOnlyList<NvrChannel>? channels = null)
    {
        var byDevice = devices.ToDictionary(device => device.Id);

        Rows.Clear();

        foreach (var device in devices)
        {
            var own = channels?.Where(channel => channel.NvrId == device.Id).ToList() ?? [];
            var count = own.Count;

            var streams = device.Kind == DeviceKind.Nvr
                ? count == 1 ? "1 channel" : $"{count} channels"
                : device.Uris.HasMain
                    ? device.Uris.HasSub ? "main + sub stream" : "main stream"
                    : "no stream address: re-test the device";

            Rows.Add(new DeviceRowViewModel(
                device.Id,
                device.DisplayName,
                device.Endpoint.Display,
                device.Vendor ?? "unverified",
                streams,
                device.Enabled ? "ENABLED" : "DISABLED",
                IsNvr: device.Kind == DeviceKind.Nvr,
                ChannelCount: count,
                KindLabel: device.Kind == DeviceKind.Nvr ? "NVR" : "CAMERA"));
        }

        Channels.Clear();

        foreach (var channel in channels ?? [])
        {
            var parent = byDevice.GetValueOrDefault(channel.NvrId);
            var streams = channel.Uris.HasMain
                ? channel.Uris.HasSub ? "main + sub" : "main"
                : "no stream address";

            Channels.Add(new ChannelRowViewModel(
                channel.Id,
                channel.NvrId,
                channel.Channel.ToString(System.Globalization.CultureInfo.InvariantCulture),
                channel.Name,
                parent?.DisplayName ?? "recorder",
                streams,
                channel.Enabled ? "ENABLED" : "DISABLED"));
        }

        if (IsDemo && Rows.Count == 0)
        {
            Rows.Add(new DeviceRowViewModel(
                Guid.Empty,
                "Warehouse NVR",
                "http://192.168.18.40:80",
                "dahua",
                "8 channels",
                "ENABLED",
                IsNvr: true,
                ChannelCount: 8,
                KindLabel: "NVR"));
        }

        Summary = Rows.Count switch
        {
            0 => "No cameras registered",
            1 => "1 device registered",
            _ => $"{Rows.Count} devices registered",
        };

        ApplyFilter();

        if (SelectedRow is not null)
        {
            var refreshed = Rows.FirstOrDefault(row => row.Id == SelectedRow.Id);
            _selectedRow = refreshed;
            Raise(nameof(SelectedRow));
        }

        Raise(nameof(HasDevices));
        Raise(nameof(HasChannels));
        Raise(nameof(HasSelection));
        Raise(nameof(SelectedChannels));
        Raise(nameof(SelectedDetail));
        Raise(nameof(SelectedTitle));
    }

    private void ApplyFilter()
    {
        var filtered = string.IsNullOrWhiteSpace(_searchText)
            ? Rows.ToList()
            : Rows.Where(row =>
                row.Name.Contains(_searchText, StringComparison.OrdinalIgnoreCase) ||
                row.Endpoint.Contains(_searchText, StringComparison.OrdinalIgnoreCase) ||
                row.Vendor.Contains(_searchText, StringComparison.OrdinalIgnoreCase)).ToList();

        FilteredRows.Clear();
        foreach (var row in filtered)
        {
            FilteredRows.Add(row);
        }

        if (SelectedRow is not null && FilteredRows.All(row => row.Id != SelectedRow.Id))
        {
            SelectedRow = null;
        }
    }
}
