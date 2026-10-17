namespace Jellyfin.Plugin.PrivateSyncPlay;

// A failure is latched until restart. Never silently re-enable privacy enforcement or guest leases.
public sealed class PluginStatus
{
    private string? _failure;
    public bool IsAvailable => Volatile.Read(ref _failure) is null;
    public string? Failure => Volatile.Read(ref _failure);
    public void Disable(string reason) => Interlocked.CompareExchange(ref _failure, reason, null);
    public void EnsureAvailable()
    {
        if (!IsAvailable) throw new PartyException(503, "Private SyncPlay is unavailable. Ask the administrator to check the server log.");
    }
}
