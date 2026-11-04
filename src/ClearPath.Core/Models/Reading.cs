namespace ClearPath.Core.Models;

/// <summary>
/// A measured value that may be unavailable. ClearPath never substitutes a guess for a value
/// it could not measure; consumers must render <see cref="UnavailableReason"/> instead.
/// </summary>
public readonly record struct Reading<T>
{
    private Reading(bool isAvailable, T? value, string? reason)
    {
        IsAvailable = isAvailable;
        Value = value;
        UnavailableReason = reason;
    }

    public bool IsAvailable { get; }
    public T? Value { get; }
    public string? UnavailableReason { get; }

    public static Reading<T> Of(T value) => new(true, value, null);
    public static Reading<T> Unavailable(string reason) => new(false, default, reason);

    public override string ToString() => IsAvailable ? $"{Value}" : $"Unavailable ({UnavailableReason})";
}
