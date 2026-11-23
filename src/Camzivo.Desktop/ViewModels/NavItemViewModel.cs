namespace Camzivo.Desktop.ViewModels;

public enum NavSection
{
    LiveView,
    Playback,
    Devices,
    Events,
    Exports,
    Health,
    Settings,
}

public sealed class NavItemViewModel(NavSection section, string title, string iconKey, string hint) : ObservableObject
{
    private bool _isSelected;

    public NavSection Section { get; } = section;

    public string Title { get; } = title;

    /// <summary>Resource key of the vector icon in Themes/Icons.xaml. No icon font dependency.</summary>
    public string IconKey { get; } = iconKey;

    public string Hint { get; } = hint;

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public override string ToString() => Title;
}
