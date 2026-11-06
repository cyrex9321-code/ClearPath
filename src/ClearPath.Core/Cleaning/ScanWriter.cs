namespace ClearPath.Core.Cleaning;

/// <summary>Write side of scan persistence. Scanning depends on this (Core-only); Infrastructure implements it.</summary>
public interface IScanWriter
{
    Task BeginScanAsync(Guid scanId, string kind, CancellationToken cancellationToken);
    Task RecordRootSnapshotAsync(RootSnapshot snapshot, CancellationToken cancellationToken);
    Task RecordItemAsync(ScanItem item, CancellationToken cancellationToken);
    Task RecordSkipAsync(Guid scanId, SkipReason reason, CancellationToken cancellationToken);
    Task CompleteScanAsync(Guid scanId, string state, long filesFound, long bytesFound, long skipped, CancellationToken cancellationToken);
}
