using System.Windows;
using ClearPath.App.Services;
using ClearPath.Core.Cleaning;
using Wpf.Ui.Controls;

namespace ClearPath.App.Views.Dialogs;

/// <summary>
/// Shows the exact <see cref="PurgeChallenge"/> (item count, bytes, required text) and only enables the
/// confirm button once the typed text matches <see cref="PurgeChallenge.RequiredText"/> verbatim.
/// </summary>
public sealed partial class PurgeChallengeDialog : FluentWindow
{
    private readonly string _requiredText;

    public PurgeChallengeDialog(PurgeChallenge challenge)
    {
        InitializeComponent();
        _requiredText = challenge.RequiredText;
        RequiredTextBlock.Text = challenge.RequiredText;
        SummaryText.Text = $"{Formatters.Count(challenge.ItemSeqs.Count)} file(s), {Formatters.Bytes(challenge.LogicalBytes)} logical, will be permanently removed from quarantine.";
    }

    public bool Confirmed { get; private set; }

    /// <summary>Shows the dialog modally and returns whether the user confirmed with the exact required text.</summary>
    public static bool Show(Window owner, PurgeChallenge challenge)
    {
        var dialog = new PurgeChallengeDialog(challenge) { Owner = owner };
        dialog.ShowDialog();
        return dialog.Confirmed;
    }

    private void OnConfirmationChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        ConfirmButton.IsEnabled = string.Equals(ConfirmationBox.Text, _requiredText, StringComparison.Ordinal);
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
