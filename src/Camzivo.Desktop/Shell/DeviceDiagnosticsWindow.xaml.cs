using System.Windows;

namespace Camzivo.Desktop.Shell;

public partial class DeviceDiagnosticsWindow : Window
{
    public DeviceDiagnosticsWindow(object viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
