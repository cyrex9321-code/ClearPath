using System.Windows;
using Wpf.Ui.Controls;

namespace ClearPath.App.Views.Dialogs;

/// <summary>Generic confirmation dialog: a message, an optional bullet list of exact details, and an optional warning line.</summary>
public sealed partial class ConfirmDialog : FluentWindow
{
    public ConfirmDialog()
    {
        InitializeComponent();
    }

    public bool Confirmed { get; private set; }

    public static bool Show(
        Window owner,
        string title,
        string message,
        IReadOnlyList<string>? details = null,
        string confirmText = "Confirm",
        string? warning = null,
        bool danger = false)
    {
        var dialog = new ConfirmDialog { Owner = owner };
        dialog.Title = title;
        dialog.DialogTitleBar.Title = title;
        dialog.MessageText.Text = message;
        dialog.ConfirmButton.Content = confirmText;
        if (danger) dialog.ConfirmButton.Appearance = ControlAppearance.Danger;

        if (details is { Count: > 0 })
        {
            dialog.DetailsList.ItemsSource = details;
        }

        if (!string.IsNullOrWhiteSpace(warning))
        {
            dialog.WarningText.Text = warning;
            dialog.WarningText.Visibility = Visibility.Visible;
        }

        dialog.ShowDialog();
        return dialog.Confirmed;
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        Close();
    }
}
