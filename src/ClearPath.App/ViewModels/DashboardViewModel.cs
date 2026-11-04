using System.Collections.ObjectModel;
using ClearPath.App.Services;
using ClearPath.Core.Abstractions;
using ClearPath.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace ClearPath.App.ViewModels;

/// <summary>
/// Live system overview. Polls every 2 s only while the Dashboard page is visible and the window is not minimized.
/// All native calls run off the UI thread; only measured values are ever shown.
/// </summary>
public sealed partial class DashboardViewModel : ObservableObject
{
    private const int MaxCpuSamples = 120;
    private const int HistoryRefreshEveryTicks = 5;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly ISystemMetrics _metrics;
    private readonly IDriveInfoProvider _drivesProvider;
    private readonly IOsInfoProvider _osProvider;
    private readonly IRecycleBinInfo _recycleBin;
    private readonly IHistoryRepository _history;
    private readonly ILogger<DashboardViewModel> _logger;

    private CancellationTokenSource? _pollCts;
    private bool _pageVisible;
    private bool _windowMinimized;

    public DashboardViewModel(
        ISystemMetrics metrics,
        IDriveInfoProvider drivesProvider,
        IOsInfoProvider osProvider,
        IRecycleBinInfo recycleBin,
        IHistoryRepository history,
        ILogger<DashboardViewModel> logger)
    {
        _metrics = metrics;
        _drivesProvider = drivesProvider;
        _osProvider = osProvider;
        _recycleBin = recycleBin;
        _history = history;
        _logger = logger;

        CpuSeries =
        [
            new LineSeries<double>
            {
                Values = CpuSamples,
                GeometrySize = 0,
                LineSmoothness = 0.2,
                Name = "CPU",
                YToolTipLabelFormatter = point => Formatters.Percent(point.Model),
            },
        ];
        CpuXAxes = [new Axis { IsVisible = false }];
        CpuYAxes = [new Axis { MinLimit = 0, MaxLimit = 100, MinStep = 25, TextSize = 11, Labeler = v => $"{v:0}%" }];
        ApplyChartTheme();
    }

    // ---- CPU ----
    [ObservableProperty] private string _cpuValue = "Measuring…";
    [ObservableProperty] private string _cpuDetail = "Sampled every 2 seconds while this page is open";

    public ObservableCollection<double> CpuSamples { get; } = [];
    public ISeries[] CpuSeries { get; }
    public Axis[] CpuXAxes { get; }
    public Axis[] CpuYAxes { get; }
    [ObservableProperty] private string _cpuChartStatus = "Waiting for the first CPU samples…";
    [ObservableProperty] private bool _hasCpuSamples;

    // ---- Memory ----
    [ObservableProperty] private string _memoryValue = "Measuring…";
    [ObservableProperty] private string _memoryDetail = string.Empty;
    [ObservableProperty] private double _memoryLoadPercent;
    [ObservableProperty] private bool _memoryAvailable;

    // ---- Recycle Bin (view only) ----
    [ObservableProperty] private string _recycleBinValue = "Measuring…";
    [ObservableProperty] private string _recycleBinDetail = string.Empty;

    // ---- Drives ----
    public ObservableCollection<DriveItemViewModel> Drives { get; } = [];
    [ObservableProperty] private string _drivesMessage = "Measuring…";
    [ObservableProperty] private bool _hasDrivesMessage = true;

    // ---- System ----
    [ObservableProperty] private string _osName = "Measuring…";
    [ObservableProperty] private string _osDetail = string.Empty;
    [ObservableProperty] private string _lastScanText = "Loading…";
    [ObservableProperty] private string _lastCleanupText = "Loading…";

    /// <summary>Called by the page on Loaded/Unloaded.</summary>
    public void SetPageVisible(bool visible)
    {
        _pageVisible = visible;
        if (visible) ApplyChartTheme();
        UpdatePolling();
    }

    /// <summary>Called by the main window when it is minimized/restored.</summary>
    public void SetWindowMinimized(bool minimized)
    {
        _windowMinimized = minimized;
        UpdatePolling();
    }

    private void UpdatePolling()
    {
        var shouldPoll = _pageVisible && !_windowMinimized;
        if (shouldPoll && _pollCts is null)
        {
            _pollCts = new CancellationTokenSource();
            _ = PollAsync(_pollCts.Token);
        }
        else if (!shouldPoll && _pollCts is not null)
        {
            _pollCts.Cancel();
            _pollCts.Dispose();
            _pollCts = null;
        }
    }

    private async Task PollAsync(CancellationToken token)
    {
        try
        {
            var tick = 0;
            using var timer = new PeriodicTimer(PollInterval);
            do
            {
                var includeHistory = tick % HistoryRefreshEveryTicks == 0;
                var snapshot = await Task.Run(() => CollectAsync(includeHistory, token), token);
                if (token.IsCancellationRequested) return;
                Apply(snapshot);
                tick++;
            }
            while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException)
        {
            // Page hidden or window minimized.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dashboard polling stopped unexpectedly");
        }
    }

    private async Task<Snapshot> CollectAsync(bool includeHistory, CancellationToken token)
    {
        var cpu = Safe("CPU", _metrics.SampleCpuPercent);
        var memory = Safe("memory", _metrics.GetMemoryStatus);
        var drives = Safe("drives", _drivesProvider.GetFixedDrives);
        var os = Safe("OS info", _osProvider.GetOsInfo);
        var recycle = Safe("Recycle Bin", _recycleBin.Query);

        HistoryResult? history = null;
        if (includeHistory)
        {
            try
            {
                var scan = await _history.GetLastScanAsync(token);
                var cleanup = await _history.GetLastCleanupAsync(token);
                history = new HistoryResult(scan, cleanup, null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reading last scan/cleanup failed");
                history = new HistoryResult(null, null, "Unavailable (history could not be read)");
            }
        }

        return new Snapshot(cpu, memory, drives, os, recycle, history);
    }

    private Reading<T> Safe<T>(string what, Func<Reading<T>> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reading {Metric} failed", what);
            return Reading<T>.Unavailable("Unavailable");
        }
    }

    private void Apply(Snapshot s)
    {
        // CPU
        if (s.Cpu.IsAvailable)
        {
            var value = Math.Round(s.Cpu.Value, 1);
            CpuValue = Formatters.Percent(value);
            CpuDetail = "Sampled every 2 seconds while this page is open";
            CpuSamples.Add(value);
            while (CpuSamples.Count > MaxCpuSamples) CpuSamples.RemoveAt(0);
            HasCpuSamples = true;
            CpuChartStatus = $"{CpuSamples.Count} samples this session (newest on the right)";
        }
        else
        {
            CpuValue = s.Cpu.UnavailableReason ?? "Unavailable";
            CpuDetail = "No CPU sample yet";
        }

        // Memory
        if (s.Memory.IsAvailable && s.Memory.Value is { } mem)
        {
            MemoryAvailable = true;
            MemoryLoadPercent = mem.MemoryLoadPercent;
            MemoryValue = Formatters.Percent(mem.MemoryLoadPercent);
            MemoryDetail = $"{Formatters.Bytes(mem.UsedPhysicalBytes)} used of {Formatters.Bytes(mem.TotalPhysicalBytes)}";
        }
        else
        {
            MemoryAvailable = false;
            MemoryLoadPercent = 0;
            MemoryValue = s.Memory.UnavailableReason ?? "Unavailable";
            MemoryDetail = string.Empty;
        }

        // Recycle Bin
        if (s.Recycle.IsAvailable && s.Recycle.Value is { } bin)
        {
            RecycleBinValue = Formatters.Bytes(bin.TotalBytes);
            RecycleBinDetail = bin.ItemCount == 1 ? "1 item" : $"{Formatters.Count(bin.ItemCount)} items";
        }
        else
        {
            RecycleBinValue = s.Recycle.UnavailableReason ?? "Unavailable";
            RecycleBinDetail = string.Empty;
        }

        // Drives
        ApplyDrives(s.Drives);

        // OS
        if (s.Os.IsAvailable && s.Os.Value is { } os)
        {
            var name = os.IsWindows11 && os.ProductName.StartsWith("Windows 10", StringComparison.OrdinalIgnoreCase)
                ? "Windows 11" + os.ProductName["Windows 10".Length..]
                : os.ProductName;
            OsName = name;
            var version = string.IsNullOrWhiteSpace(os.DisplayVersion) ? string.Empty : $"Version {os.DisplayVersion} · ";
            OsDetail = $"{version}Build {os.BuildNumber}.{os.UpdateBuildRevision} · {os.Architecture} · {os.MachineName}";
        }
        else
        {
            OsName = s.Os.UnavailableReason ?? "Unavailable";
            OsDetail = string.Empty;
        }

        // History
        if (s.History is { } h)
        {
            if (h.Error is not null)
            {
                LastScanText = h.Error;
                LastCleanupText = h.Error;
            }
            else
            {
                LastScanText = h.Scan is null
                    ? "No scans yet"
                    : $"{Formatters.Timestamp(h.Scan.StartedUtc)} · {h.Scan.State} · {Formatters.Count(h.Scan.FilesFound)} files, {Formatters.Bytes(h.Scan.BytesFound)}";
                LastCleanupText = h.Cleanup is null
                    ? "No cleanups yet"
                    : $"{Formatters.Timestamp(h.Cleanup.CreatedUtc)} · {h.Cleanup.State} · {Formatters.Count(h.Cleanup.ItemCount)} item(s), {Formatters.Bytes(h.Cleanup.LogicalBytes)}";
            }
        }
    }

    private void ApplyDrives(Reading<IReadOnlyList<DriveStatus>> reading)
    {
        if (!reading.IsAvailable || reading.Value is null)
        {
            Drives.Clear();
            DrivesMessage = reading.UnavailableReason ?? "Unavailable";
            HasDrivesMessage = true;
            return;
        }

        var list = reading.Value;
        if (list.Count == 0)
        {
            Drives.Clear();
            DrivesMessage = "No fixed drives found";
            HasDrivesMessage = true;
            return;
        }

        HasDrivesMessage = false;
        var sameLayout = Drives.Count == list.Count;
        for (var i = 0; sameLayout && i < list.Count; i++)
        {
            sameLayout = string.Equals(Drives[i].RootPath, list[i].RootPath, StringComparison.OrdinalIgnoreCase);
        }

        if (sameLayout)
        {
            for (var i = 0; i < list.Count; i++) Drives[i].Update(list[i]);
        }
        else
        {
            Drives.Clear();
            foreach (var d in list) Drives.Add(new DriveItemViewModel(d));
        }
    }

    private void ApplyChartTheme()
    {
        var dark = ThemeService.IsDark;
        var text = dark ? new SKColor(0xD0, 0xD0, 0xD0) : new SKColor(0x40, 0x40, 0x40);
        var grid = dark ? new SKColor(0xFF, 0xFF, 0xFF, 0x1F) : new SKColor(0x00, 0x00, 0x00, 0x1F);
        var accent = new SKColor(0x3B, 0x8E, 0xEA);

        foreach (var axis in CpuYAxes)
        {
            axis.LabelsPaint = new SolidColorPaint(text);
            axis.SeparatorsPaint = new SolidColorPaint(grid) { StrokeThickness = 1 };
        }

        if (CpuSeries[0] is LineSeries<double> line)
        {
            line.Stroke = new SolidColorPaint(accent) { StrokeThickness = 2.5f };
            line.Fill = new SolidColorPaint(accent.WithAlpha(0x30));
        }
    }

    private sealed record HistoryResult(ScanSummary? Scan, OperationSummary? Cleanup, string? Error);

    private sealed record Snapshot(
        Reading<double> Cpu,
        Reading<MemoryStatus> Memory,
        Reading<IReadOnlyList<DriveStatus>> Drives,
        Reading<OsInfo> Os,
        Reading<RecycleBinStatus> Recycle,
        HistoryResult? History);
}

/// <summary>One fixed drive card.</summary>
public sealed partial class DriveItemViewModel : ObservableObject
{
    public DriveItemViewModel(DriveStatus status)
    {
        RootPath = status.RootPath;
        Update(status);
    }

    public string RootPath { get; }

    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _detail = string.Empty;
    [ObservableProperty] private string _usedText = string.Empty;
    [ObservableProperty] private string _freeText = string.Empty;
    [ObservableProperty] private string _totalText = string.Empty;
    [ObservableProperty] private double _usedPercent;
    [ObservableProperty] private string _usedPercentText = string.Empty;

    public void Update(DriveStatus status)
    {
        Title = string.IsNullOrWhiteSpace(status.VolumeLabel) ? status.RootPath : $"{status.VolumeLabel} ({status.RootPath})";
        Detail = status.FileSystem;
        UsedText = Formatters.Bytes(status.UsedBytes);
        FreeText = Formatters.Bytes(status.FreeBytes);
        TotalText = Formatters.Bytes(status.TotalBytes);
        UsedPercent = Math.Round(status.UsedFraction * 100, 1);
        UsedPercentText = Formatters.Percent(UsedPercent) + " used";
    }
}
