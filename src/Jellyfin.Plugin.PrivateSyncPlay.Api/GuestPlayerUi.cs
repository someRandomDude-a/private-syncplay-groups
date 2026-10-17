using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.PrivateSyncPlay.Api;

/// <summary>A declarative guest control. No HTML, scripts, URLs or server commands are accepted.</summary>
/// <param name="Id">Stable namespaced ID, e.g. example.captions.</param>
/// <param name="Feature">One of mute, volume, subtitles or fullscreen.</param>
/// <param name="Label">Plain-text accessible label.</param>
/// <param name="Icon">A Jellyfin Material icon class name; ignored for volume sliders.</param>
public sealed record GuestPlayerControl(string Id, string Feature, string Label, string Icon);

/// <summary>Cross-load-context registration for explicitly enabled, guest-safe player controls.</summary>
public static class GuestPlayerUiExtensions
{
    /// <summary>Versioned DI key. Framework strings avoid plugin assembly load-context mismatches.</summary>
    public const string ServiceKey = "Jellyfin.Plugin.PrivateSyncPlay.GuestUi.v1";
    /// <summary>Contributes a control to the administrator's designer. It is disabled until explicitly added.</summary>
    /// <param name="services">Jellyfin's plugin service collection.</param>
    /// <param name="control">Descriptor of a control backed only by a built-in local guest action.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddPrivateSyncPlayGuestControl(this IServiceCollection services, GuestPlayerControl control)
    {
        ArgumentNullException.ThrowIfNull(control);
        services.AddKeyedSingleton<string>(ServiceKey, JsonSerializer.Serialize(control));
        return services;
    }
}
