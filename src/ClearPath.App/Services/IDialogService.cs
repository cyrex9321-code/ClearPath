using ClearPath.App.Views.Dialogs;
using ClearPath.Core.Cleaning;

namespace ClearPath.App.Services;

/// <summary>UI-only confirmation/result dialogs, owned by the current main window. Never touches the filesystem.</summary>
public interface IDialogService
{
    bool Confirm(string title, string message, IReadOnlyList<string>? details = null, string confirmText = "Confirm", string? warning = null, bool danger = false);

    void ShowResult(string title, IReadOnlyList<ResultRow> rows, string? note = null);

    /// <summary>Shows the purge challenge and returns whether the user typed the exact required text and confirmed.</summary>
    bool ConfirmPurge(PurgeChallenge challenge);
}

public sealed class DialogService : IDialogService
{
    private static System.Windows.Window? Owner => System.Windows.Application.Current?.MainWindow;

    public bool Confirm(string title, string message, IReadOnlyList<string>? details = null, string confirmText = "Confirm", string? warning = null, bool danger = false) =>
        Owner is not null && ConfirmDialog.Show(Owner, title, message, details, confirmText, warning, danger);

    public void ShowResult(string title, IReadOnlyList<ResultRow> rows, string? note = null)
    {
        if (Owner is null) return;
        ResultDialog.Show(Owner, title, rows, note);
    }

    public bool ConfirmPurge(PurgeChallenge challenge) =>
        Owner is not null && PurgeChallengeDialog.Show(Owner, challenge);
}
