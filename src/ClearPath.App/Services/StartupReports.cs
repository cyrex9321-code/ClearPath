using ClearPath.Core.Cleaning;

namespace ClearPath.App.Services;

/// <summary>
/// Holds the latest capability self-test and startup-recovery reports so Safety Center (and anything else)
/// can display them without re-running the probe/reconciliation. Populated once, in the background, by
/// <see cref="ClearPath.App.App"/> shortly after the main window is shown.
/// </summary>
public sealed class StartupReports
{
    public CapabilityReport? Capability { get; private set; }
    public RecoveryReport? Recovery { get; private set; }

    /// <summary>Raised on a background thread whenever a report is set; subscribers must marshal to the UI thread themselves.</summary>
    public event EventHandler? Changed;

    public void SetCapability(CapabilityReport report)
    {
        Capability = report;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetRecovery(RecoveryReport report)
    {
        Recovery = report;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
