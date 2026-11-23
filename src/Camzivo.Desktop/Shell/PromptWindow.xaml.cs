using System.Windows;
using System.Windows.Input;

namespace Camzivo.Desktop.Shell;

/// <summary>One-field prompt (rename a device, name a layout). No nested dialogs, no chrome to fight the theme.</summary>
public partial class PromptWindow : Window
{
    public PromptWindow(string heading, string message, string value)
    {
        InitializeComponent();

        HeadingText.Text = heading;
        MessageText.Text = message;
        ValueBox.Text = value;

        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    public string Value => ValueBox.Text.Trim();

    private void Value_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            DialogResult = true;
            e.Handled = true;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
