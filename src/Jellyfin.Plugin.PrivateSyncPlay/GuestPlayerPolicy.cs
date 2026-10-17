using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.PrivateSyncPlay.Api;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.PrivateSyncPlay;

public sealed class GuestPlayerOptions
{
    public string[] Features { get; set; } = ["title", "position", "volume", "mute", "subtitles", "fullscreen"];
    public string[] Controls { get; set; } = ["mute", "volume", "subtitles", "fullscreen"];
    public bool Allows(string feature) => GuestPlayerPolicy.Features.Contains(feature, StringComparer.Ordinal)
        && Features?.Contains(feature, StringComparer.Ordinal) == true;
    public void Validate()
    {
        if (Features is null || Controls is null || Features.Length > GuestPlayerPolicy.Features.Length || Controls.Length > 24
            || Features.Distinct(StringComparer.Ordinal).Count() != Features.Length || Controls.Distinct(StringComparer.Ordinal).Count() != Controls.Length
            || Features.Any(f => !GuestPlayerPolicy.Features.Contains(f, StringComparer.Ordinal)) || Controls.Any(id => !GuestPlayerPolicy.ValidId(id)))
            throw new ArgumentException("Invalid guest player allowlist or layout.");
    }
}

public static class GuestPlayerPolicy
{
    public static readonly string[] Features = ["title", "position", "volume", "mute", "subtitles", "fullscreen"];
    public static bool ValidId(string? id) => id is not null && Regex.IsMatch(id, @"\A[a-z][a-z0-9.-]{0,63}\z");
    public static bool KnownOperation(string action) => action is "Options" or "Redeem" or "State" or "Stream" or "Subtitle" or "Leave";
    public static void Require(GuestPlayerOptions? options, string feature)
    {
        if (options?.Allows(feature) != true) throw new PartyException(403, "This guest feature is not enabled.");
    }
}

public sealed record GuestPlayerView(string[] Features, GuestPlayerControl[] Controls);

// Contributions are data, not code. Unknown actions/IDs are not admitted to the player by CSS or DOM presence.
public sealed class GuestPlayerRegistry(IServiceProvider services)
{
    public static readonly GuestPlayerControl[] BuiltIn = [new("mute", "mute", "Mute", "volume_up"),
        new("volume", "volume", "Volume", "volume_up"), new("subtitles", "subtitles", "Subtitles", "closed_caption"),
        new("fullscreen", "fullscreen", "Fullscreen", "fullscreen")];
    public GuestPlayerControl[] Catalog()
    {
        var controls = BuiltIn.ToList();
        var candidates = new List<GuestPlayerControl>();
        try
        {
            foreach (var json in services.GetKeyedServices<string>(GuestPlayerUiExtensions.ServiceKey).Take(64))
            {
                if (json is null || json.Length > 2048) continue;
                try
                {
                    var control = JsonSerializer.Deserialize<GuestPlayerControl>(json);
                    if (control is not null && GuestPlayerPolicy.ValidId(control.Id) && control.Id.Contains('.')
                        && BuiltIn.Any(c => c.Feature == control.Feature) && !string.IsNullOrWhiteSpace(control.Label) && control.Label.Length <= 80
                        && control.Icon is not null && Regex.IsMatch(control.Icon, @"\A[a-z][a-z0-9_]{0,47}\z")) candidates.Add(control);
                }
                catch (JsonException) { /* A malformed contribution grants nothing. */ }
            }
        }
        catch (Exception) { return controls.ToArray(); } // A failing provider must not take down guest playback.
        // Ambiguous registrations fail closed, independent of plugin registration order.
        controls.AddRange(candidates.GroupBy(c => c.Id, StringComparer.Ordinal).Where(g => g.Count() == 1).Select(g => g.Single()));
        return controls.ToArray();
    }
    public GuestPlayerView View(GuestPlayerOptions? options)
    {
        var features = GuestPlayerPolicy.Features.Where(f => options?.Allows(f) == true).ToArray();
        var catalog = Catalog().ToDictionary(c => c.Id, StringComparer.Ordinal);
        var controls = (options?.Controls ?? []).Take(24).Distinct(StringComparer.Ordinal)
            .Where(id => id is not null && catalog.TryGetValue(id, out var c) && features.Contains(c.Feature, StringComparer.Ordinal))
            .Select(id => catalog[id]).ToArray();
        return new(features, controls);
    }
}
