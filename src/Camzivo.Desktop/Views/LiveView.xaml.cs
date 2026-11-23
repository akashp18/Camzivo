using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Camzivo.Desktop.ViewModels;

namespace Camzivo.Desktop.Views;

public partial class LiveView : UserControl
{
    private Point _dragOrigin;

    public LiveView()
    {
        InitializeComponent();
    }

    private void Camera_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        _dragOrigin = e.GetPosition(this);

    /// <summary>
    /// Starts a drag once the pointer has moved: the same row still responds to a plain click, so click-to-place keeps
    /// working exactly as before.
    /// </summary>
    private void Camera_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || sender is not FrameworkElement { DataContext: CameraListEntry camera })
        {
            return;
        }

        var offset = e.GetPosition(this) - _dragOrigin;
        if (Math.Abs(offset.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(offset.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var data = new DataObject(LiveViewModel.CameraDragFormat, camera.Id);
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Move);
    }

    private void LayoutName_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is LiveViewModel { ConfirmLayoutCommand: { } confirm } &&
            confirm.CanExecute(null))
        {
            confirm.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && DataContext is LiveViewModel { CancelLayoutCommand: { } cancel } &&
                 cancel.CanExecute(null))
        {
            cancel.Execute(null);
            e.Handled = true;
        }
    }
}
