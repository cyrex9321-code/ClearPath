using System.Collections.ObjectModel;
using System.Windows;
using ClearPath.App.Services;
using ClearPath.Core.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace ClearPath.App.ViewModels;

/// <summary>One named check from the startup capability self-test (docs/15 §1).</summary>
public sealed record CapabilityCheckRow(string Name, bool Passed);

public sealed partial class SafetyCenterViewModel(
    IPrivilegeInfo privilege,
    IHistoryRepository history,
    ISettingsStore settings,
    IAppPaths paths,
    StartupReports startupReports,
    ILogger<SafetyCenterViewModel> logger) : ObservableObject
{
    // Privilege
    [ObservableProperty] private string _userName = "Loading…";
    [ObservableProperty] private string _elevationText = "Loading…";
    [ObservableProperty] private string _integrityLevel = "Loading…";
    [ObservableProperty] private bool _showNotElevatedWarning;
    [ObservableProperty] private string? _privilegeUnavailable;

    // Quarantine / recovery
    [ObservableProperty] private string _quarantineItems = "Loading…";
    [ObservableProperty] private string _quarantineBytes = "Loading…";
    [ObservableProperty] private string _expiredItems = "Loading…";
    [ObservableProperty] private string _pendingRecovery = "Loading…";
    [ObservableProperty] private string _needingAttention = "Loading…";
    [ObservableProperty] private string _lastScan = "Loading…";
    [ObservableProperty] private string? _snapshotError;

    [ObservableProperty] private string _retentionText = string.Empty;
    public string QuarantineLocation => settings.Current.QuarantineLocation ?? paths.DefaultQuarantineDirectory;

    public ObservableCollection<string> Exclusions { get; } = [];
    [ObservableProperty] private bool _hasNoExclusions = true;

    // Capability self-test (docs/15 §1); Capability is set by App shortly after startup.
    [ObservableProperty] private bool _capabilityRunning = true;
    [ObservableProperty] private bool _capabilityAllPassed;
    [ObservableProperty] private string? _capabilityFailureDetail;
    [ObservableProperty] private string _capabilityRanAt = string.Empty;
    public ObservableCollection<CapabilityCheckRow> CapabilityChecks { get; } = [];
    public string CapabilitySummaryText => $"{(CapabilityAllPassed ? "All checks passed" : "One or more checks failed")} · ran {CapabilityRanAt}";

    // Startup recovery reconciliation (docs/15 §8); Recovery is set by App shortly after startup.
    [ObservableProperty] private bool _recoveryRunning = true;
    [ObservableProperty] private int _recoveryReconciled;
    [ObservableProperty] private int _recoveryStillAttention;
    [ObservableProperty] private int _recoveryRetried;
    [ObservableProperty] private bool _hasRecoveryAttention;
    public ObservableCollection<string> RecoveryNotes { get; } = [];
    public string RecoveryAttentionMessage => $"{RecoveryStillAttention} item(s) were left needing attention after startup recovery. Review them in History.";

    private bool _subscribed;

    public async Task RefreshAsync()
    {
        if (!_subscribed)
        {
            _subscribed = true;
            startupReports.Changed += (_, _) => Application.Current?.Dispatcher.BeginInvoke(ApplyStartupReports);
        }
        ApplyStartupReports();

        var current = settings.Current;
        RetentionText = $"{current.QuarantineRetentionDays} days (items are flagged as expired after this; nothing is ever purged automatically)";
        OnPropertyChanged(nameof(QuarantineLocation));

        Exclusions.Clear();
        foreach (var e in current.Exclusions) Exclusions.Add(e);
        HasNoExclusions = Exclusions.Count == 0;

        try
        {
            var reading = await Task.Run(privilege.GetCurrent);
            if (reading.IsAvailable && reading.Value is { } p)
            {
                PrivilegeUnavailable = null;
                UserName = p.UserName;
                IntegrityLevel = p.IntegrityLevel;
                ElevationText = p.IsElevated ? "Elevated (running as administrator)" : "Not elevated";
                ShowNotElevatedWarning = !p.IsElevated;
            }
            else
            {
                PrivilegeUnavailable = reading.UnavailableReason ?? "Unavailable";
                UserName = IntegrityLevel = ElevationText = PrivilegeUnavailable;
                ShowNotElevatedWarning = false;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Reading privilege status failed");
            PrivilegeUnavailable = "Unavailable";
            UserName = IntegrityLevel = ElevationText = "Unavailable";
            ShowNotElevatedWarning = false;
        }

        try
        {
            var snap = await history.GetSafetySnapshotAsync(current.QuarantineRetentionDays);
            SnapshotError = null;
            QuarantineItems = Formatters.Count(snap.QuarantinedItemCount);
            QuarantineBytes = Formatters.Bytes(snap.QuarantinedLogicalBytes);
            ExpiredItems = Formatters.Count(snap.ExpiredItemCount);
            PendingRecovery = Formatters.Count(snap.OperationsPendingRecovery);
            NeedingAttention = Formatters.Count(snap.ItemsNeedingAttention);
            LastScan = snap.LastScan is null
                ? "No scans yet"
                : $"{Formatters.Timestamp(snap.LastScan.StartedUtc)} · {snap.LastScan.State} · {Formatters.Count(snap.LastScan.FilesFound)} files, {Formatters.Bytes(snap.LastScan.BytesFound)}";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Reading safety snapshot failed");
            SnapshotError = "The safety information could not be read from the history database. See the log for details.";
            QuarantineItems = QuarantineBytes = ExpiredItems = PendingRecovery = NeedingAttention = LastScan = "Unavailable";
        }
    }

    private void ApplyStartupReports()
    {
        if (startupReports.Capability is { } cap)
        {
            CapabilityRunning = false;
            CapabilityAllPassed = cap.AllPassed;
            CapabilityFailureDetail = cap.FailureDetail;
            CapabilityRanAt = Formatters.Timestamp(cap.RanUtc);
            CapabilityChecks.Clear();
            foreach (var kv in cap.Results)
            {
                CapabilityChecks.Add(new CapabilityCheckRow(kv.Key, kv.Value));
            }
            OnPropertyChanged(nameof(CapabilitySummaryText));
        }
        else
        {
            CapabilityRunning = true;
        }

        if (startupReports.Recovery is { } rec)
        {
            RecoveryRunning = false;
            RecoveryReconciled = rec.Reconciled;
            RecoveryStillAttention = rec.StillAttention;
            RecoveryRetried = rec.Retried;
            HasRecoveryAttention = rec.StillAttention > 0;
            RecoveryNotes.Clear();
            foreach (var note in rec.Notes) RecoveryNotes.Add(note);
            OnPropertyChanged(nameof(RecoveryAttentionMessage));
        }
        else
        {
            RecoveryRunning = true;
        }
    }
}
