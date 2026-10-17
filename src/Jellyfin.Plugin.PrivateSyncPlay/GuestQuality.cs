namespace Jellyfin.Plugin.PrivateSyncPlay;

// Called under PartyStore.Gate. Feedback adjusts only a shared video budget, never playback/user policy.
public sealed class GuestQuality
{
    private static readonly int[] Ladder = [256, 384, 512, 768, 1000, 1500, 2000, 3000, 4000, 6000, 8000, 10000];
    private readonly Dictionary<Guid, Sample> _samples = [];
    private int _current, _cap;
    private bool _networkLimited;
    private DateTimeOffset _changed, _recovering;

    public void Report(Guid guest, string generation, int? bandwidthKbps, double bufferSeconds, bool stalled, DateTimeOffset now)
    {
        if (bandwidthKbps is < 1 or > 200000 || !double.IsFinite(bufferSeconds) || bufferSeconds is < 0 or > 120)
            throw new PartyException(400, "Invalid guest network sample.");
        if (_samples.TryGetValue(guest, out var previous) && now - previous.Received < TimeSpan.FromSeconds(4)) return;
        _samples[guest] = new(generation, bandwidthKbps, bufferSeconds, stalled, now);
    }

    public int Select(int cap, IReadOnlyCollection<Guid> guests, string? generation, DateTimeOffset now)
    {
        foreach (var id in _samples.Keys.Where(id => !guests.Contains(id)).ToArray()) _samples.Remove(id);
        var fresh = _samples.Values.Where(s => s.Generation == generation && now - s.Received <= TimeSpan.FromSeconds(25)).ToArray();
        if (_current == 0 || cap != _cap)
        {
            _current = _current == 0 || (!_networkLimited && fresh.Length == 0) ? cap : Math.Min(_current, cap);
            _cap = cap; _changed = now; _recovering = default;
        }
        var target = cap;
        foreach (var sample in fresh)
        {
            // Leave 30% network headroom, plus room for audio and MPEG-TS overhead.
            if (sample.BandwidthKbps is { } bandwidth) target = Math.Min(target, Math.Max(256, (int)(bandwidth * .7) - 160));
            if (sample.Stalled && sample.BufferSeconds < 2 && sample.Received >= _changed) target = Math.Min(target, Math.Max(256, _current * 3 / 4));
        }
        target = Math.Min(cap, target);
        if (target < _current * .85 && now - _changed >= TimeSpan.FromSeconds(5))
        {
            _current = Math.Min(cap, Ladder.LastOrDefault(rate => rate <= target, 256));
            _networkLimited = true;
            _changed = now; _recovering = default;
        }
        else if (fresh.Length == guests.Count && fresh.Length > 0 && target >= _current * 1.4
            && fresh.All(s => s.BandwidthKbps.HasValue && !s.Stalled && s.BufferSeconds >= 6))
        {
            if (_recovering == default) _recovering = now;
            if (now - _recovering >= TimeSpan.FromSeconds(60))
            {
                _current = Math.Min(target, Ladder.FirstOrDefault(rate => rate > _current, cap));
                _changed = now; _recovering = default;
            }
        }
        else _recovering = default;
        return Math.Min(_current, cap);
    }

    private sealed record Sample(string Generation, int? BandwidthKbps, double BufferSeconds, bool Stalled, DateTimeOffset Received);
}
