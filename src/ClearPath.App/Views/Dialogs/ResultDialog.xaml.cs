using System.Windows;
using Wpf.Ui.Controls;

namespace ClearPath.App.Views.Dialogs;

public sealed record ResultRow(string Label, string Value);

/// <summary>Generic read-only result display: a list of label/value rows and an optional note.</summary>
public sealed partial class ResultDialog : FluentWindow
{
    public ResultDialog()
    {
        InitializeComponent();
    }

    public static void Show(Window owner, string title, IReadOnlyList<ResultRow> rows, string? note = null)
    {
        var dialog = new ResultDialog { Owner = owner };
        dialog.Title = title;
        dialog.DialogTitleBar.Title = title;
        dialog.RowsList.ItemsSource = rows;
        if (!string.IsNullOrWhiteSpace(note))
        {
            dialog.NoteText.Text = note;
            dialog.NoteText.Visibility = Visibility.Visible;
        }
        dialog.ShowDialog();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
