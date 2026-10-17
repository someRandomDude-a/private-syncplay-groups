using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.SyncPlay;
using MediaBrowser.Controller.SyncPlay.Requests;
using MediaBrowser.Controller.SyncPlay.PlaybackRequests;
using MediaBrowser.Model.SyncPlay;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.PrivateSyncPlay;

public sealed class PrivateSyncPlayManager : ISyncPlayManager, IDisposable
{
    private readonly ISyncPlayManager _inner;
    private readonly PartyStore _store;
    private readonly ISessionManager _sessions;
    private readonly Func<Configuration> _configuration;
    private readonly ILogger<PrivateSyncPlayManager> _logger;
    public PluginStatus Status { get; }
    public Configuration Config { get { Status.EnsureAvailable(); return _configuration(); } }

    public PrivateSyncPlayManager(CoreEngine engine, PartyStore store, ISessionManager sessions, Func<Configuration> configuration,
        PluginStatus? status = null, ILogger<PrivateSyncPlayManager>? logger = null)
    {
        _inner = engine.Value;
        _store = store;
        _sessions = sessions;
        _configuration = configuration;
        Status = status ?? new PluginStatus();
        _logger = logger ?? NullLogger<PrivateSyncPlayManager>.Instance;
        _sessions.SessionEnded += SessionEnded;
    }

    public GroupInfoDto NewGroup(SessionInfo session, NewGroupRequest request, CancellationToken cancellationToken)
        => Create(session, request, null, null, cancellationToken);

    public GroupInfoDto Create(SessionInfo session, NewGroupRequest request, GroupMode? mode, int? capacity, CancellationToken token)
    {
        lock (_store.Gate)
        {
            var config = Config;
            var effective = mode ?? config.DefaultFor(session.UserId);
            if (!config.CanCreate(session.UserId, effective)) throw new PartyException(403, "This group mode is not permitted.");
            if (string.IsNullOrWhiteSpace(request.GroupName) || request.GroupName.Length > 200) throw new PartyException(400, "A group name of 1–200 characters is required.");
            var max = capacity ?? config.MaxViewers;
            if (max < 1 || max > config.MaxViewers) throw new PartyException(400, "Invalid viewer limit.");
            var group = _inner.NewGroup(session, request, token);
            _store.RemoveSession(session.Id);
            _store.Create(group.GroupId, session.UserId, session.Id, effective, max);
            return group;
        }
    }

    public void JoinGroup(SessionInfo session, JoinGroupRequest request, CancellationToken cancellationToken)
    {
        lock (_store.Gate)
        {
            Status.EnsureAvailable();
            var party = _store.Require(request.GroupId); // Unknown groups are never assumed public.
            if (party.Mode == GroupMode.Private && !party.Members.Contains(session.Id))
                throw new PartyException(404, "Party not found. Use an invitation.");
            JoinAuthorized(session, party, cancellationToken);
        }
    }

    public void JoinCode(SessionInfo session, string code, CancellationToken token)
    {
        lock (_store.Gate)
        {
            Status.EnsureAvailable();
            _store.RateLimit("code:" + session.UserId);
            _store.RateLimit("code-global", 300);
            JoinAuthorized(session, _store.ByCode(code), token);
        }
    }

    public void JoinInvite(SessionInfo session, string secret, CancellationToken token)
    {
        lock (_store.Gate)
        {
            Status.EnsureAvailable();
            _store.RateLimit("invite:" + session.UserId);
            // An existing member need not redeem again when refreshing a full/used invite.
            var invite = _store.ValidateInvite(secret, Config, session.Id);
            var party = _store.Require(invite.PartyId);
            var existing = party.Members.Contains(session.Id);
            JoinAuthorized(session, party, token);
            if (!existing) invite.Uses++;
        }
    }

    private void JoinAuthorized(SessionInfo session, Party party, CancellationToken token)
    {
        if (party.BlockedUsers.Contains(session.UserId)) throw new PartyException(403, "You have been removed from this party.");
        if (!party.Policy.AllowJoins && !party.Members.Contains(session.Id) && session.UserId != party.Owner)
            throw new PartyException(403, "This party is not accepting new viewers.");
        _store.CheckSeat(party, session.Id, Config);
        if (_inner.GetGroup(session, party.Id) is null) throw new PartyException(403, "You cannot access this party's media.");
        _inner.JoinGroup(session, new JoinGroupRequest(party.Id), token);
        if (!party.Members.Contains(session.Id))
        {
            _store.RemoveSession(session.Id);
            party.Members.Add(session.Id);
        }
        party.Account(session.UserId);
        ApplyWaitPermissions(party);
    }

    public void LeaveGroup(SessionInfo session, LeaveGroupRequest request, CancellationToken cancellationToken)
    {
        lock (_store.Gate)
        {
            try { _inner.LeaveGroup(session, request, cancellationToken); }
            finally { _store.RemoveSession(session.Id); }
        }
    }
    public List<GroupInfoDto> ListGroups(SessionInfo session, ListGroupsRequest request)
    {
        lock (_store.Gate) { Status.EnsureAvailable(); return _inner.ListGroups(session, request)
              .Where(g => _store.Parties.TryGetValue(g.GroupId, out var p) && p.Mode == GroupMode.Public && !p.BlockedUsers.Contains(session.UserId)).ToList();
        }
    }
    public GroupInfoDto? GetGroup(SessionInfo session, Guid groupId)
    {
        lock (_store.Gate)
        {
            Status.EnsureAvailable();
            if (!_store.Parties.TryGetValue(groupId, out var p) || p.BlockedUsers.Contains(session.UserId) || (p.Mode == GroupMode.Private && !p.Members.Contains(session.Id))) return null;
            return _inner.GetGroup(session, groupId);
        }
    }
    public void HandleRequest(SessionInfo session, IGroupPlaybackRequest request, CancellationToken cancellationToken)
    {
        lock (_store.Gate)
        {
            Status.EnsureAvailable();
            var party = _store.ForSession(session.Id) ?? throw new PartyException(404, "Party not found.");
            if (party.BlockedUsers.Contains(session.UserId)) throw new PartyException(403, "You have been removed from this party.");
            if (request is null) throw new PartyException(400, "A playback request is required.");
            var control = party.CanControl(session.UserId);
            var permitted = request.Action switch
            {
                PlaybackRequestType.Ping or PlaybackRequestType.Buffer or PlaybackRequestType.Ready or PlaybackRequestType.IgnoreWait => true,
                PlaybackRequestType.Pause or PlaybackRequestType.Unpause or PlaybackRequestType.Stop or PlaybackRequestType.Seek
                    or PlaybackRequestType.NextItem or PlaybackRequestType.PreviousItem or PlaybackRequestType.SetPlaylistItem => control,
                PlaybackRequestType.Play or PlaybackRequestType.Queue or PlaybackRequestType.RemoveFromPlaylist or PlaybackRequestType.MovePlaylistItem
                    or PlaybackRequestType.SetRepeatMode or PlaybackRequestType.SetShuffleMode => party.CanEditQueue(session.UserId),
                _ => false
            };
            if (!permitted) throw new PartyException(403, "The owner has not allowed this party action.");
            ApplyWaitPermissions(party);
            // A read-only viewer must not pause/seek the group indirectly through buffering/readiness reports.
            if (!control && request.Action is PlaybackRequestType.Buffer or PlaybackRequestType.Ready or PlaybackRequestType.IgnoreWait) return;
            _inner.HandleRequest(session, request, cancellationToken);
        }
    }
    public bool IsUserActive(Guid userId) => _inner.IsUserActive(userId);
    public Party OwnedParty(SessionInfo actor, Guid id)
    {
        Status.EnsureAvailable();
        _store.Sweep(Config);
        var party = _store.ForSession(actor.Id);
        if (party?.Id != id) throw new PartyException(404, "Party not found for this session.");
        if (party.Owner != actor.UserId) throw new PartyException(403, "Only the party owner may manage participants.");
        return party;
    }
    public void UpdatePolicy(SessionInfo actor, Guid id, PartyPolicy policy, int capacity)
    {
        lock (_store.Gate)
        {
            var party = OwnedParty(actor, id);
            ValidateFeatures(policy.GuestFeatures);
            if (capacity < party.Members.Count + (policy.AllowGuests ? _store.GuestCount(id) : 0) || capacity < 1 || capacity > Config.MaxViewers)
                throw new PartyException(400, "The viewer limit must cover current viewers and stay within the server limit.");
            party.Policy = policy with { GuestFeatures = policy.GuestFeatures?.ToArray() }; party.Capacity = capacity;
            if (!policy.AllowGuests) _store.RevokeGuestInvitations(party);
            ApplyWaitPermissions(party);
        }
    }
    public void SetAccountPermissions(SessionInfo actor, Guid partyId, Guid participant, bool control, bool queue)
    {
        lock (_store.Gate)
        {
            var party = OwnedParty(actor, partyId);
            var account = party.Accounts.Values.FirstOrDefault(a => a.Id == participant) ?? throw new PartyException(404, "Participant not found.");
            if (account.UserId == party.Owner) throw new PartyException(400, "The owner retains management and playback control.");
            account.ControlPlayback = control; account.EditQueue = queue; ApplyWaitPermissions(party);
        }
    }
    public void SetGuestPermissions(SessionInfo actor, Guid partyId, Guid participant, string[]? features)
    {
        lock (_store.Gate)
        {
            var party = OwnedParty(actor, partyId); ValidateFeatures(features);
            var guest = _store.Guests(party.Id).FirstOrDefault(g => g.Id == participant) ?? throw new PartyException(404, "Guest not found.");
            if (features?.Any(f => party.Policy.GuestFeatures is not null && !party.Policy.GuestFeatures.Contains(f, StringComparer.Ordinal)) == true)
                throw new PartyException(403, "Guest features cannot exceed the party's allowed features.");
            guest.Features = features?.ToArray();
        }
    }
    private void ValidateFeatures(string[]? features)
    {
        if (features is not null && (features.Length > GuestPlayerPolicy.Features.Length || features.Distinct(StringComparer.Ordinal).Count() != features.Length
            || features.Any(f => !Config.GuestPlayer.Allows(f)))) throw new PartyException(403, "Guest features cannot exceed the administrator's allowlist.");
    }
    public void Kick(SessionInfo actor, Guid partyId, Guid participant)
    {
        lock (_store.Gate)
        {
            var party = OwnedParty(actor, partyId);
            var guest = _store.Guests(partyId).FirstOrDefault(g => g.Id == participant);
            if (guest is not null) { _store.Revoke(party, guest.InviteId); return; }
            var account = party.Accounts.Values.FirstOrDefault(a => a.Id == participant) ?? throw new PartyException(404, "Participant not found.");
            if (account.UserId == party.Owner) throw new PartyException(400, "The owner cannot be kicked. Leave or end the party instead.");
            var targets = _sessions.Sessions.Where(s => s.UserId == account.UserId && party.Members.Contains(s.Id)).ToArray();
            party.BlockedUsers.Add(account.UserId);
            foreach (var session in targets) _store.RemoveSession(session.Id); // Revoke plugin access before native cleanup.
            try { foreach (var session in targets) _inner.LeaveGroup(session, new LeaveGroupRequest(), CancellationToken.None); }
            catch (Exception error)
            {
                _logger.LogWarning("Native participant removal failed ({ErrorType}); ending the party.", error.GetType().Name);
                EndParty(partyId); throw new PartyException(503, "Removal could not be confirmed; the party was ended for safety.");
            }
        }
    }
    public void Unblock(SessionInfo actor, Guid partyId, Guid participant)
    {
        lock (_store.Gate)
        {
            var party = OwnedParty(actor, partyId);
            var account = party.Accounts.Values.FirstOrDefault(a => a.Id == participant) ?? throw new PartyException(404, "Participant not found.");
            party.BlockedUsers.Remove(account.UserId); // Does not rejoin, restore permissions, or redeem an invitation.
        }
    }
    private void ApplyWaitPermissions(Party party)
    {
        try
        {
            foreach (var session in _sessions.Sessions.Where(s => party.Members.Contains(s.Id)))
            {
                var readOnly = !party.CanControl(session.UserId);
                if (readOnly == party.ReadOnlySessions.Contains(session.Id)) continue;
                _inner.HandleRequest(session, new IgnoreWaitGroupRequest(readOnly), CancellationToken.None);
                if (readOnly) party.ReadOnlySessions.Add(session.Id); else party.ReadOnlySessions.Remove(session.Id);
            }
        }
        catch (Exception error)
        {
            _logger.LogWarning("Native wait policy update failed ({ErrorType}); ending the party.", error.GetType().Name);
            EndParty(party.Id); throw new PartyException(503, "Permissions could not be applied safely; the party was ended.");
        }
    }
    public void EndParty(Guid id)
    {
        lock (_store.Gate)
        {
            var party = _store.Require(id);
            // Revoke before contacting the native engine: failed cleanup must never leave a valid lease.
            _store.Close(id);
            foreach (var session in _sessions.Sessions.Where(s => party.Members.Contains(s.Id)).ToArray())
            {
                try { _inner.LeaveGroup(session, new LeaveGroupRequest(), CancellationToken.None); }
                catch (Exception error) { _logger.LogWarning("Native SyncPlay group cleanup failed ({ErrorType}).", error.GetType().Name); }
            }
        }
    }
    private void SessionEnded(object? sender, SessionEventArgs e) { lock (_store.Gate) _store.RemoveSession(e.SessionInfo.Id); }
    public void Dispose() => _sessions.SessionEnded -= SessionEnded;
}

// Retains the actual registered core instance without a second SyncPlay engine or circular DI.
public sealed record CoreEngine(ISyncPlayManager Value) : IDisposable
{
    public void Dispose() => (Value as IDisposable)?.Dispose();
}
