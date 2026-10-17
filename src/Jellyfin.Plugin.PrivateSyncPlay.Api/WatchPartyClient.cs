using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Jellyfin.Plugin.PrivateSyncPlay.Api;

/// <summary>Registers the typed client in the consuming plugin's own assembly context.</summary>
public static class WatchPartyClientExtensions
{
    /// <summary>Registers the single current .NET scripting client for all party operations, without HTTP or reflection.</summary>
    /// <param name="services">The collection provided to IPluginServiceRegistrator.RegisterServices.</param><returns>The collection for chaining.</returns>
    public static IServiceCollection AddPrivateSyncPlayClient(this IServiceCollection services)
    {
        services.TryAddSingleton<IWatchPartyService, WatchPartyClient>();
        return services;
    }
}

/// <summary>A typed adapter safe to bundle in another Jellyfin plugin's isolated load context.</summary>
/// <param name="services">Jellyfin's service provider; resolved only after all plugin registration has completed.</param>
public sealed class WatchPartyClient(IServiceProvider services) : IWatchPartyService
{
    /// <summary>Current API contract key. Only the matching SDK/provider contract is supported; no older-key fallback is attempted.</summary>
    public const string ServiceKey = "Jellyfin.Plugin.PrivateSyncPlay.Api.1.1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter() } };
    private Func<Guid, string, string, string, CancellationToken, Task<string>>? Handler
        => services.GetKeyedService<Func<Guid, string, string, string, CancellationToken, Task<string>>>(ServiceKey);
    /// <inheritdoc />
    public bool IsAvailable
    {
        get { try { return Handler is not null; } catch (Exception) { return false; } }
    }

    private async Task<T> Call<T>(Guid userId, string sessionId, string operation, object request, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var handler = Handler ?? throw new WatchPartyException(503, "The matching Private SyncPlay .NET API is not installed or enabled.");
            var response = await handler(userId, sessionId, operation, JsonSerializer.Serialize(request, Json), token).ConfigureAwait(false);
            if (response is null || response.Length > 1024 * 1024) throw new JsonException();
            using var json = JsonDocument.Parse(response);
            var root = json.RootElement;
            var status = root.GetProperty("statusCode").GetInt32();
            if (status is >= 400 and <= 599) throw new WatchPartyException(status, root.GetProperty("error").GetString() ?? "Watch-party operation failed.");
            if (status != 200) throw new JsonException();
            var result = root.GetProperty("value").Deserialize<T>(Json);
            if (result is null && operation != "GetCurrentParty") throw new JsonException();
            return result!;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (WatchPartyException) { throw; }
        catch (Exception)
        {
            // Provider activation, load-context mismatch and malformed replies are contained at the API boundary.
            throw new WatchPartyException(503, "Private SyncPlay .NET API could not complete this request.");
        }
    }
    private async Task Command(Guid userId, string sessionId, string operation, object request, CancellationToken token)
        => _ = await Call<JsonElement>(userId, sessionId, operation, request, token).ConfigureAwait(false);
    /// <inheritdoc />
    public Task<WatchPartyInfo?> GetCurrentPartyAsync(Guid userId, string sessionId, CancellationToken cancellationToken = default)
        => Call<WatchPartyInfo?>(userId, sessionId, "GetCurrentParty", new { }, cancellationToken);
    /// <inheritdoc />
    public Task<WatchPartyInfo> CreatePartyAsync(Guid userId, string sessionId, CreatePartyOptions options, CancellationToken cancellationToken = default)
        => Call<WatchPartyInfo>(userId, sessionId, "CreateParty", options, cancellationToken);
    /// <inheritdoc />
    public Task<WatchPartyWithInvite> CreatePartyWithInviteAsync(Guid userId, string sessionId, CreatePartyOptions party, CreateInviteOptions invite, CancellationToken cancellationToken = default)
        => Call<WatchPartyWithInvite>(userId, sessionId, "CreatePartyWithInvite", new { party, invite }, cancellationToken);
    /// <inheritdoc />
    public Task<WatchPartyInvite> CreateInviteAsync(Guid userId, string sessionId, Guid partyId, CreateInviteOptions options, CancellationToken cancellationToken = default)
        => Call<WatchPartyInvite>(userId, sessionId, "CreateInvite", new { partyId, options }, cancellationToken);
    /// <inheritdoc />
    public Task RevokeInviteAsync(Guid userId, string sessionId, Guid partyId, Guid inviteId, CancellationToken cancellationToken = default)
        => Command(userId, sessionId, "RevokeInvite", new { partyId, inviteId }, cancellationToken);
    /// <inheritdoc />
    public Task JoinByCodeAsync(Guid userId, string sessionId, string code, CancellationToken cancellationToken = default)
        => Command(userId, sessionId, "JoinByCode", new { code }, cancellationToken);
    /// <inheritdoc />
    public Task JoinByInviteAsync(Guid userId, string sessionId, string token, CancellationToken cancellationToken = default)
        => Command(userId, sessionId, "JoinByInvite", new { token }, cancellationToken);
    /// <inheritdoc />
    public Task LeavePartyAsync(Guid userId, string sessionId, CancellationToken cancellationToken = default)
        => Command(userId, sessionId, "LeaveParty", new { }, cancellationToken);
    /// <inheritdoc />
    public Task EndPartyAsync(Guid userId, string sessionId, Guid partyId, CancellationToken cancellationToken = default)
        => Command(userId, sessionId, "EndParty", new { partyId }, cancellationToken);
    /// <inheritdoc />
    public Task<WatchPartyManagement> GetManagementAsync(Guid userId, string sessionId, Guid partyId, CancellationToken cancellationToken = default)
        => Call<WatchPartyManagement>(userId, sessionId, "GetManagement", new { partyId }, cancellationToken);
    /// <inheritdoc />
    public Task UpdatePolicyAsync(Guid userId, string sessionId, Guid partyId, WatchPartyPolicy policy, CancellationToken cancellationToken = default)
        => Command(userId, sessionId, "UpdatePolicy", new { partyId, policy }, cancellationToken);
    /// <inheritdoc />
    public Task SetAccountPermissionsAsync(Guid userId, string sessionId, Guid partyId, Guid participantId, WatchPartyAccountPermissions permissions, CancellationToken cancellationToken = default)
        => Command(userId, sessionId, "SetAccountPermissions", new { partyId, participantId, permissions }, cancellationToken);
    /// <inheritdoc />
    public Task SetGuestPermissionsAsync(Guid userId, string sessionId, Guid partyId, Guid participantId, WatchPartyGuestPermissions options, CancellationToken cancellationToken = default)
        => Command(userId, sessionId, "SetGuestPermissions", new { partyId, participantId, options }, cancellationToken);
    /// <inheritdoc />
    public Task KickAccountAsync(Guid userId, string sessionId, Guid partyId, Guid participantId, CancellationToken cancellationToken = default)
        => Command(userId, sessionId, "KickAccount", new { partyId, participantId }, cancellationToken);
    /// <inheritdoc />
    public Task KickGuestAndRevokeInviteAsync(Guid userId, string sessionId, Guid partyId, Guid participantId, CancellationToken cancellationToken = default)
        => Command(userId, sessionId, "KickGuestAndRevokeInvite", new { partyId, participantId }, cancellationToken);
    /// <inheritdoc />
    public Task UnblockAccountAsync(Guid userId, string sessionId, Guid partyId, Guid participantId, CancellationToken cancellationToken = default)
        => Command(userId, sessionId, "UnblockAccount", new { partyId, participantId }, cancellationToken);
}
