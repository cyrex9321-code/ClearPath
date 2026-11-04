using System.Collections.ObjectModel;
using ClearPath.App.Services;
using ClearPath.Core.Abstractions;
using ClearPath.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace ClearPath.App.ViewModels;

public sealed record HistoryRow(
    string Date, string Kind, string Mode, string State, string Items, string NeedingAttention, string Size)
{
    public static HistoryRow From(OperationSummary o) => new(
        Formatters.Timestamp(o.CreatedUtc),
        o.Kind,
        o.Mode,
        o.State,
        Formatters.Count(o.ItemCount),
        Formatters.Count(o.ItemsNeedingAttention),
        Formatters.Bytes(o.LogicalBytes));
}

public sealed partial class HistoryViewModel(IHistoryRepository history, ILogger<HistoryViewModel> logger) : ObservableObject
{
    public ObservableCollection<HistoryRow> Rows { get; } = [];

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
            Rows.Clear();
            foreach (var o in ops) Rows.Add(HistoryRow.From(o));
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
}
