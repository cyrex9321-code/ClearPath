namespace ClearPath.Windows.FileSystem;

/// <summary>Mutable holder updated once by the startup capability self-test; read by the writer thereafter.</summary>
public sealed class CapabilitySettings : ICapabilitySettings
{
    public bool RenameExSupported { get; set; } = true;
    public bool AllPassed { get; set; } = true;
}
