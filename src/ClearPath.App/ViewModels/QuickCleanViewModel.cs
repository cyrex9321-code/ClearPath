using System.Collections.ObjectModel;
using System.ComponentModel;
using ClearPath.App.Services;
using ClearPath.App.Views.Dialogs;
using ClearPath.Core.Cleaning;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace ClearPath.App.ViewModels;

/// <summary>
/// Quick Clean: scans the seven allow-listed rule roots (docs/15), shows real results grouped by category,
/// and quarantines the files the user selects. Every number shown comes from a real <see cref="ScanResult"/>
/// or <see cref="CleanupResult"/> — nothing here is simulated.
/// </summary>
public sealed partial class QuickCleanViewModel(
    IScanner scanner,
    IScanStore scanStore,
    IRuleCatalog catalog,
    ICleaner cleaner,
    IDialogService dialogs,
    ILogger<QuickCleanViewModel> logger) : ObservableObject
{
    /// <summary>A few rule roots share one UI category (docs/02 "six categories" over seven roots); this names them.</summary>
    private static readonly Dictionary<string, string> CategoryNameOverrides = new()
    {
        ["wer"] = "Windows Error Reporting (user)",
    };

    private CancellationTokenSource? _scanCts;
    private Guid? _scanId;

    public ObservableCollection<CategoryViewModel> Categories { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartScan))]
    [NotifyPropertyChangedFor(nameof(CanCancelScan))]
    [NotifyPropertyChangedFor(nameof(CanQuarantine))]
    private bool _isScanning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartScan))]
    [NotifyPropertyChangedFor(nameof(CanQuarantine))]
    private bool _isQuarantining;

    [ObservableProperty] private bool _hasScanned;
    [ObservableProperty] private bool _hasResults;
    [ObservableProperty] private string _scanStatusText = "No scan has run yet. Quick Clean only ever looks at the allow-listed categories below.";
    [ObservableProperty] private string? _scanError;
    [ObservableProperty] private string _skipsSummaryText = string.Empty;
    [ObservableProperty] private string? _crossVolumeNote;

    public bool CanStartScan => !IsScanning && !IsQuarantining;
    public bool CanCancelScan => IsScanning;
    public bool CanQuarantine => !IsScanning && !IsQuarantining && Categories.Any(c => c.SelectedCount > 0);

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsScanning) return;
        IsScanning = true;
        HasResults = false;
        ScanError = null;
        SkipsSummaryText = string.Empty;
        CrossVolumeNote = null;
        Categories.Clear();
        ScanStatusText = "Scanning…";
        _scanCts = new CancellationTokenSource();

        var progress = new Progress<ScanProgress>(p =>
            ScanStatusText = $"Scanning {p.RuleId}… {Formatters.Count(p.FilesMatched)} file(s) found so far ({Formatters.Bytes(p.BytesMatched)})");

        try
        {
            // Off the UI thread: the engine's database calls complete synchronously, so awaiting directly would run the
            // whole scan on the dispatcher and freeze the window (measured: unresponsive during a scan).
            var result = await Task.Run(() => scanner.ScanAsync(new ScanRequest([]), progress, _scanCts.Token)).ConfigureAwait(true);
            _scanId = result.ScanId;
            ApplyResult(result);
            var totalFiles = Categories.Sum(c => c.FileCount);
            var totalBytes = Categories.Sum(c => c.TotalBytes);
            ScanStatusText = Categories.Count == 0
                ? "Scan complete. Nothing in the allow-listed categories matched right now."
                : $"Scan complete: {Formatters.Count(totalFiles)} file(s), {Formatters.Bytes(totalBytes)}.";
            HasResults = true;
        }
        catch (OperationCanceledException)
        {
            ScanStatusText = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Quick Clean scan failed");
            ScanError = "The scan could not complete: " + ex.GetBaseException().Message;
            ScanStatusText = "Scan failed.";
        }
        finally
        {
            IsScanning = false;
            HasScanned = true;
            _scanCts?.Dispose();
            _scanCts = null;
        }
    }

    [RelayCommand]
    private void CancelScan() => _scanCts?.Cancel();

    private void ApplyResult(ScanResult result)
    {
        var rulesByCategory = catalog.Rules.GroupBy(r => r.CategoryId).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var total in result.Categories.OrderByDescending(c => c.Bytes))
        {
            var rules = rulesByCategory.GetValueOrDefault(total.CategoryId) ?? [];
            var name = CategoryNameOverrides.GetValueOrDefault(total.CategoryId)
                ?? (rules.Count switch
                {
                    0 => total.CategoryId,
                    1 => rules[0].DisplayName,
                    _ => string.Join(" & ", rules.Select(r => r.DisplayName).Distinct()),
                });
            var riskNote = rules.Count switch
            {
                0 => string.Empty,
                1 => rules[0].RiskNote,
                _ => string.Join(" ", rules.Select(r => r.RiskNote).Distinct()),
            };

            var vm = new CategoryViewModel(result.ScanId, total, name, riskNote, scanStore);
            vm.PropertyChanged += OnCategoryPropertyChanged;
            Categories.Add(vm);
        }

        SkipsSummaryText = result.Skips.Count == 0
            ? string.Empty
            : "Skipped: " + string.Join(", ", result.Skips.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} ({Formatters.Count(kv.Value)})"));

        CrossVolumeNote = result.CrossVolume.Count == 0
            ? null
            : $"{Formatters.Count(result.CrossVolume.Sum(g => g.Files))} file(s) are on a different volume from the quarantine location and cannot be quarantined in this version.";
    }

    private void OnCategoryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CategoryViewModel.SelectedCount))
        {
            OnPropertyChanged(nameof(CanQuarantine));
        }
    }

    [RelayCommand]
    private async Task QuarantineSelectedAsync()
    {
        if (_scanId is not { } scanId) return;
        var selected = Categories.Where(c => c.SelectedCount > 0).ToList();
        if (selected.Count == 0) return;

        var totalFiles = selected.Sum(c => c.SelectedCount);
        var totalBytes = selected.Sum(c => c.SelectedBytes);
        var details = selected
            .Select(c => $"{c.DisplayName}: {Formatters.Count(c.SelectedCount)} file(s), {Formatters.Bytes(c.SelectedBytes)}")
            .ToList();

        var confirmed = dialogs.Confirm(
            "Quarantine selected files",
            $"{Formatters.Count(totalFiles)} file(s), {Formatters.Bytes(totalBytes)} total, will be moved to quarantine. "
            + "They are not deleted — they can be restored from History.",
            details,
            confirmText: "Quarantine");
        if (!confirmed) return;

        var seqs = selected.SelectMany(c => c.SelectedSeqs).ToList();

        IsQuarantining = true;
        try
        {
            var progress = new Progress<int>(n =>
                ScanStatusText = $"Quarantining… {Formatters.Count(n)} of {Formatters.Count(seqs.Count)}");
            var result = await Task.Run(() => cleaner.QuarantineAsync(new QuarantineRequest(scanId, seqs), progress, CancellationToken.None)).ConfigureAwait(true);
            ShowQuarantineResult(result);

            // The scanned source files are gone (or changed) after quarantine; clear the stale result rather
            // than show selection state for files that may no longer exist. A fresh Scan reflects reality.
            Categories.Clear();
            HasResults = false;
            _scanId = null;
            ScanStatusText = "Files quarantined. Run Scan now to see the current state.";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Quarantine failed");
            dialogs.ShowResult("Quarantine failed", [new ResultRow("Error", ex.GetBaseException().Message)]);
        }
        finally
        {
            IsQuarantining = false;
        }
    }

    private void ShowQuarantineResult(CleanupResult result)
    {
        var rows = new List<ResultRow>
        {
            new("Files quarantined", Formatters.Count(result.FilesQuarantined)),
            new("Logical bytes quarantined", Formatters.Bytes(result.LogicalBytesQuarantined)),
            new("Retained in quarantine", Formatters.Bytes(result.BytesRetainedInQuarantine)),
            new("Space freed now", Formatters.Bytes(result.ReclaimedBytes) + " — space is actually freed only once purged from quarantine"),
            new("Files skipped", Formatters.Count(result.FilesSkipped)),
        };
        foreach (var kv in result.SkipsByReason.OrderByDescending(kv => kv.Value))
        {
            rows.Add(new ResultRow("  " + kv.Key, Formatters.Count(kv.Value)));
        }
        if (result.ItemsNeedingAttention > 0)
        {
            rows.Add(new ResultRow("Needing attention", $"{Formatters.Count(result.ItemsNeedingAttention)} — review in Safety Center or History"));
        }
        foreach (var v in result.Volumes)
        {
            rows.Add(new ResultRow($"Volume {v.Root}", $"free before {Formatters.Bytes(v.FreeBefore)}, after {Formatters.Bytes(v.FreeAfter)} (observed — other processes may affect this)"));
        }

        dialogs.ShowResult("Quarantine complete", rows);
    }
}

/// <summary>One scanned category: its real totals, and its files paged in from <see cref="IScanStore"/>.</summary>
public sealed partial class CategoryViewModel : ObservableObject
{
    private const int PageSize = 100;

    private readonly Guid _scanId;
    private readonly IScanStore _store;
    private readonly HashSet<long> _selected = [];
    private long _loadedCount;
    private bool _allLoaded;

    public CategoryViewModel(Guid scanId, CategoryTotal total, string displayName, string riskNote, IScanStore store)
    {
        _scanId = scanId;
        _store = store;
        CategoryId = total.CategoryId;
        FileCount = total.Files;
        TotalBytes = total.Bytes;
        DisplayName = displayName;
        RiskNote = riskNote;
    }

    public string CategoryId { get; }
    public string DisplayName { get; }
    public string RiskNote { get; }
    public long FileCount { get; }
    public long TotalBytes { get; }
    public string FileCountText => Formatters.Count(FileCount);
    public string TotalBytesText => Formatters.Bytes(TotalBytes);

    public ObservableCollection<ScanItemRowViewModel> Items { get; } = [];

    public IReadOnlyCollection<long> SelectedSeqs => _selected;

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isLoadingItems;
    [ObservableProperty] private bool _hasMore = true;
    [ObservableProperty] private long _selectedCount;
    [ObservableProperty] private long _selectedBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectAllButtonText))]
    private bool? _allSelected;

    public string SelectAllButtonText => AllSelected == true ? "Clear selection" : "Select all";
    public string SelectedSummaryText => SelectedCount == 0 ? "None selected" : $"{Formatters.Count(SelectedCount)} selected · {Formatters.Bytes(SelectedBytes)}";

    [RelayCommand]
    private async Task ToggleExpandAsync()
    {
        IsExpanded = !IsExpanded;
        if (IsExpanded) await EnsureLoadedAsync(CancellationToken.None);
    }

    [RelayCommand]
    private Task LoadMoreAsync() => LoadPageAsync(CancellationToken.None);

    [RelayCommand]
    private async Task SelectAllAsync()
    {
        var select = AllSelected != true;
        long skip = 0;
        while (true)
        {
            var page = await _store.GetPageAsync(_scanId, CategoryId, (int)skip, PageSize, CancellationToken.None).ConfigureAwait(true);
            if (page.Count == 0) break;
            foreach (var item in page)
            {
                if (item.CrossVolume) continue;
                SetSelected(item.Seq, item.Snapshot.Size, select);
                var row = Items.FirstOrDefault(r => r.Seq == item.Seq);
                if (row is not null) row.IsSelected = select;
            }
            skip += page.Count;
            if (page.Count < PageSize) break;
        }
        AllSelected = select;
    }

    public Task EnsureLoadedAsync(CancellationToken ct) =>
        Items.Count > 0 || _allLoaded ? Task.CompletedTask : LoadPageAsync(ct);

    private async Task LoadPageAsync(CancellationToken ct)
    {
        if (_allLoaded || IsLoadingItems) return;
        IsLoadingItems = true;
        try
        {
            var page = await _store.GetPageAsync(_scanId, CategoryId, (int)_loadedCount, PageSize, ct).ConfigureAwait(true);
            foreach (var item in page)
            {
                var row = new ScanItemRowViewModel(item);
                if (!item.CrossVolume && _selected.Contains(item.Seq)) row.IsSelected = true;
                row.PropertyChanged += OnRowPropertyChanged;
                Items.Add(row);
            }
            _loadedCount += page.Count;
            if (page.Count < PageSize)
            {
                _allLoaded = true;
                HasMore = false;
            }
        }
        finally
        {
            IsLoadingItems = false;
        }
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ScanItemRowViewModel.IsSelected) || sender is not ScanItemRowViewModel row) return;
        SetSelected(row.Seq, row.SizeBytes, row.IsSelected);
    }

    private void SetSelected(long seq, long size, bool selected)
    {
        if (selected)
        {
            if (_selected.Add(seq))
            {
                SelectedCount++;
                SelectedBytes += size;
            }
        }
        else if (_selected.Remove(seq))
        {
            SelectedCount--;
            SelectedBytes -= size;
        }

        var loadedSelectable = Items.Where(i => !i.IsCrossVolume).ToList();
        AllSelected = loadedSelectable.Count == 0
            ? false
            : loadedSelectable.All(i => i.IsSelected) && _allLoaded
                ? true
                : loadedSelectable.All(i => !i.IsSelected)
                    ? false
                    : (bool?)null;
    }
}

/// <summary>One scanned file row. <see cref="DisplayPath"/> is display-only, never used for mutation (docs/15 §4).</summary>
public sealed partial class ScanItemRowViewModel : ObservableObject
{
    public ScanItemRowViewModel(ScanItem item)
    {
        Seq = item.Seq;
        DisplayPath = item.DisplayPath;
        SizeBytes = item.Snapshot.Size;
        SizeText = Formatters.Bytes(SizeBytes);
        IsCrossVolume = item.CrossVolume;
        CrossVolumeNote = item.CrossVolume ? "different volume — not supported yet" : null;
    }

    public long Seq { get; }
    public string DisplayPath { get; }
    public long SizeBytes { get; }
    public string SizeText { get; }
    public bool IsCrossVolume { get; }
    public string? CrossVolumeNote { get; }
    public bool IsSelectable => !IsCrossVolume;

    [ObservableProperty] private bool _isSelected;
}
