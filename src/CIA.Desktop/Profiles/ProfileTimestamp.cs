namespace CIA.Desktop.Profiles;

internal static class ProfileTimestamp
{
    public static bool TryAdvance(
        DateTimeOffset currentUpdatedAtUtc,
        TimeProvider timeProvider,
        out DateTimeOffset updatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        var currentUtc = timeProvider.GetUtcNow().ToUniversalTime();
        if (currentUtc > currentUpdatedAtUtc)
        {
            updatedAtUtc = currentUtc;
            return true;
        }

        if (currentUpdatedAtUtc == DateTimeOffset.MaxValue)
        {
            updatedAtUtc = default;
            return false;
        }

        updatedAtUtc = currentUpdatedAtUtc.AddTicks(1);
        return true;
    }
}
