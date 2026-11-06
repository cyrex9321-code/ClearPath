using System.Collections.ObjectModel;
using ClearPath.App.Services;
using ClearPath.App.Views.Dialogs;
using ClearPath.Core.Abstractions;
using ClearPath.Core.Cleaning;
using ClearPath.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace ClearPath.App.ViewModels;

/// <summary>
/// One operation row. Restore/Purge are only offered for a Quarantine operation that still has at least one
/// item in the <see cref="ItemState.Quarantined"/> state (checked against the real journal, not guessed).
/// </summary>
public sealed partial class HistoryRowViewModel : ObservableObject
{
    public HistoryRowViewModel(OperationSummary o)
    {
        OperationId = o.Id;
        Kind = o.Kind;
        Date = Formatters.Timestamp(o.CreatedUtc);
        Mode = o.Mode;
        State = o.State;
        Items = Formatters.Count(o.ItemCount);
        NeedingAttention = Formatters.Count(o.ItemsNeedingAttention);
        Size = Formatters.Bytes(o.LogicalBytes);
    }

    public Guid OperationId { get; }
    public string Date { get; }
    public string Kind { get; }
    public string Mode { get; }
    public string State { get; }
    public string Items { get; }
    public string NeedingAttention { get; }
    public string Size { get; }

    /// <summary>Seqs currently in <see cref="ItemState.Quarantined"/>, set by <see cref="HistoryViewModel.RefreshAsync"/>.</summary>
    public IReadOnlyList<long> QuarantinedSeqs { get; set; } = [];

    [ObservableProperty] private bool _canRestore;
    [ObservableProperty] private bool _canPurge;
    [ObservableProperty] private bool _isBusy;
}

public sealed partial class HistoryViewModel(
    IHistoryRepository history,
    IOperationJournal journal,
    ICleaner cleaner,
    IDialogService dialogs,
    ILogger<HistoryViewModel> logger) : ObservableObject
{
    public ObservableCollection<HistoryRowViewModel> Rows { get; } = [];

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private bool _hasRows;
    [ObservableProperty] private string? _error;

    public async Task RefreshAsync()
    {
        IsLoading = Rows.Count == 0;
        try
        {
            var ops = await history.GetOperationsAsync(0, 100);
            var rows = new List<HistoryRowViewModel>();
            foreach (var o in ops)
            {
                var row = new HistoryRowViewModel(o);
                if (o.Kind == "Quarantine")
                {
                    try
                    {
                        var items = await journal.GetItemsAsync(o.Id, CancellationToken.None);
                        var quarantined = items.Where(i => i.State == ItemState.Quarantined).Select(i => i.Seq).ToList();
                        row.QuarantinedSeqs = quarantined;
                        row.CanRestore = quarantined.Count > 0;
                        row.CanPurge = quarantined.Count > 0;
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Reading journal items for operation {OperationId} failed", o.Id);
                    }
                }
                rows.Add(row);
            }

            Rows.Clear();
            foreach (var r in rows) Rows.Add(r);
            Error = null;
            HasRows = Rows.Count > 0;
            IsEmpty = Rows.Count == 0;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Reading operation history failed");
            Rows.Clear();
            HasRows = false;
            IsEmpty = false;
            Error = "The history could not be read from the database. See the log folder for details (Settings, Open log folder).";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task RestoreAsync(HistoryRowViewModel row)
    {
        if (row.IsBusy || row.QuarantinedSeqs.Count == 0) return;

        var confirmed = dialogs.Confirm(
            "Restore quarantined files",
            $"{Formatters.Count(row.QuarantinedSeqs.Count)} file(s) from this operation will be restored to their original location. "
            + "If a file already exists there, the restored copy keeps both (named \"(restored N)\") — nothing is ever overwritten.",
            confirmText: "Restore");
        if (!confirmed) return;

        row.IsBusy = true;
        try
        {
            var result = await Task.Run(() => cleaner.RestoreAsync(row.OperationId, row.QuarantinedSeqs, RestoreConflictChoice.KeepBoth, CancellationToken.None));
            ShowRestoreResult(result);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Restore failed for operation {OperationId}", row.OperationId);
            dialogs.ShowResult("Restore failed", [new ResultRow("Error", ex.GetBaseException().Message)]);
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task PurgeAsync(HistoryRowViewModel row)
    {
        if (row.IsBusy || row.QuarantinedSeqs.Count == 0) return;

        row.IsBusy = true;
        try
        {
            PurgeChallenge challenge;
            try
            {
                challenge = await cleaner.CreatePurgeChallengeAsync(row.OperationId, row.QuarantinedSeqs, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Creating purge challenge failed for operation {OperationId}", row.OperationId);
                dialogs.ShowResult("Purge not available", [new ResultRow("Error", ex.GetBaseException().Message)]);
                return;
            }

            if (!dialogs.ConfirmPurge(challenge)) return;

            var result = await Task.Run(() => cleaner.PurgeAsync(challenge.Nonce, challenge.RequiredText, CancellationToken.None));
            ShowPurgeResult(result);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Purge failed for operation {OperationId}", row.OperationId);
            dialogs.ShowResult("Purge failed", [new ResultRow("Error", ex.GetBaseException().Message)]);
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    private void ShowRestoreResult(CleanupResult result)
    {
        var rows = new List<ResultRow>
        {
            new("Files restored", Formatters.Count(result.FilesQuarantined)),
            new("Logical bytes restored", Formatters.Bytes(result.LogicalBytesQuarantined)),
            new("Files skipped", Formatters.Count(result.FilesSkipped)),
        };
        foreach (var kv in result.SkipsByReason.OrderByDescending(kv => kv.Value))
        {
            rows.Add(new ResultRow("  " + kv.Key, Formatters.Count(kv.Value)));
        }
        if (result.ItemsNeedingAttention > 0)
        {
            rows.Add(new ResultRow("Needing attention", $"{Formatters.Count(result.ItemsNeedingAttention)} — review in Safety Center"));
        }
        dialogs.ShowResult("Restore complete", rows);
    }

    private void ShowPurgeResult(PurgeResult result)
    {
        var rows = new List<ResultRow>
        {
            new("Permanently removed", Formatters.Count(result.PurgedVerified)),
            new("Verified logical bytes removed", Formatters.Bytes(result.VerifiedLogicalBytes)),
            new("Refused", Formatters.Count(result.Refused)),
            new("Removal pending", Formatters.Count(result.RemovalPending)),
            new("Needing attention", Formatters.Count(result.NeedsAttention)),
        };
        foreach (var v in result.Volumes)
        {
            rows.Add(new ResultRow($"Volume {v.Root}", $"free before {Formatters.Bytes(v.FreeBefore)}, after {Formatters.Bytes(v.FreeAfter)} (observed)"));
        }
        dialogs.ShowResult("Purge complete", rows, "This removed the last copy of these files. It cannot be undone.");
    }
}
