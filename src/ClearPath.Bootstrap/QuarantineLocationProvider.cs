using ClearPath.Core.Cleaning;

namespace ClearPath.Bootstrap;

/// <summary>Resolves the validated quarantine location through QuarantineStore's own checks.</summary>
internal sealed class QuarantineLocationProvider(IQuarantineStore store) : IValidatedQuarantineLocation
{
    public string? Resolve()
    {
        var root = store.OpenRoot();
        if (!root.Ok) return null;
        var path = root.Value!.Chain.CanonicalPath;
        root.Value.Dispose();
        return path;
    }
}
