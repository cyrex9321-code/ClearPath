namespace ClearPath.Windows.FileSystem;

/// <summary>Mutable holder updated once by the startup capability self-test; read by the writer thereafter.</summary>
public sealed class CapabilitySettings : ICapabilitySettings, ClearPath.Core.Cleaning.ICapabilityGate
{
    public bool RenameExSupported { get; set; } = true;
    public bool AllPassed { get; set; } = false; // fail closed until the self-test has actually run and passed (docs/15 §11.7)
    public bool OperationsAllowed => AllPassed;
}
