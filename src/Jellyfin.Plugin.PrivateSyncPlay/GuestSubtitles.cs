using MediaBrowser.Controller.MediaEncoding;

namespace Jellyfin.Plugin.PrivateSyncPlay;

// Native text conversion only, never a general subtitle proxy. All callers reauthorize after awaiting.
public sealed class GuestSubtitles(ISubtitleEncoder encoder, TimeProvider clock) : IDisposable
{
    private readonly SemaphoreSlim _conversion = new(2);
    private readonly object _gate = new();
    private readonly Dictionary<(Guid, string, int), (DateTimeOffset Expires, byte[] Bytes)> _cache = [];
    public async Task<byte[]> Read(Guid party, string generation, GuestSubtitleRequest request, CancellationToken token)
    {
        var key = (party, generation, request.Index);
        lock (_gate)
        {
            foreach (var old in _cache.Where(x => x.Value.Expires <= clock.GetUtcNow()).Select(x => x.Key).ToArray()) _cache.Remove(old);
            if (_cache.TryGetValue(key, out var cached)) return cached.Bytes;
        }
        if (!await _conversion.WaitAsync(0, token).ConfigureAwait(false)) throw new PartyException(503, "Subtitles are preparing. Try again shortly.");
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            await using var input = await encoder.GetSubtitles(request.Video, request.SourceId, request.Index, "vtt", 0, 0, true, deadline.Token).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await input.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) != 0)
            {
                if (output.Length + read > 2 * 1024 * 1024) throw new PartyException(413, "Subtitle track is too large for guest delivery.");
                output.Write(buffer, 0, read);
            }
            var bytes = output.ToArray();
            if (!System.Text.Encoding.UTF8.GetString(bytes.AsSpan(0, Math.Min(bytes.Length, 16))).TrimStart('\uFEFF').StartsWith("WEBVTT", StringComparison.Ordinal))
                throw new PartyException(415, "Subtitle conversion is unavailable.");
            lock (_gate)
            {
                if (_cache.Count >= 8) _cache.Remove(_cache.MinBy(x => x.Value.Expires).Key);
                _cache[key] = (clock.GetUtcNow().AddMinutes(1), bytes);
            }
            return bytes;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new PartyException(503, "Subtitle conversion timed out."); }
        finally { _conversion.Release(); }
    }
    public void Dispose() { lock (_gate) _cache.Clear(); /* Semaphore can still have in-flight requests during shutdown. */ }
}
