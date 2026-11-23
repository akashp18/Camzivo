using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Camzivo.Desktop.ViewModels;

namespace Camzivo.Desktop.Views;

/// <summary>The camera wall itself, shared by Live View and the secondary monitor window.</summary>
public partial class LiveWallView : UserControl
{
    public LiveWallView()
    {
        InitializeComponent();
    }

    private LiveViewModel? Live => DataContext as LiveViewModel;

    private void Tile_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CameraTileViewModel tile })
        {
            Live?.UpdateTileSize(tile.SlotIndex, e.NewSize.Width, e.NewSize.Height);
        }
    }

    /// <summary>Single click selects the tile; double click makes it the whole wall.</summary>
    private void Tile_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CameraTileViewModel tile } || Live is not { } live)
        {
            return;
        }

        live.SelectSlotCommand.Execute(tile.SlotIndex);

        if (e.ClickCount == 2 && live.SoloCommand.CanExecute(tile))
        {
            live.SoloCommand.Execute(tile);
            e.Handled = true;
        }
    }

    private void Tile_DragOver(object sender, DragEventArgs e)
    {
        var valid = e.Data.GetDataPresent(LiveViewModel.CameraDragFormat);
        e.Effects = valid ? DragDropEffects.Move : DragDropEffects.None;

        if (sender is FrameworkElement { DataContext: CameraTileViewModel tile })
        {
            tile.IsDropTarget = valid;
        }

        e.Handled = true;
    }

    private void Tile_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CameraTileViewModel tile })
        {
            tile.IsDropTarget = false;
        }
    }

    private void Tile_Drop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CameraTileViewModel tile } || Live is not { } live)
        {
            return;
        }

        tile.IsDropTarget = false;

        if (!e.Data.GetDataPresent(LiveViewModel.CameraDragFormat) ||
            e.Data.GetData(LiveViewModel.CameraDragFormat) is not Guid cameraId)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        // The view model owns placement rules (move, swap, duplicate prevention, undo); the drop names the target.
        e.Effects = live.PlaceCamera(cameraId, tile.SlotIndex) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }
}
