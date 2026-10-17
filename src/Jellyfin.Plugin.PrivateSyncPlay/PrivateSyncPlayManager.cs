using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.SyncPlay;
using MediaBrowser.Controller.SyncPlay.Requests;
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
        _store.CheckSeat(party, session.Id, Config);
        if (_inner.GetGroup(session, party.Id) is null) throw new PartyException(403, "You cannot access this party's media.");
        _inner.JoinGroup(session, new JoinGroupRequest(party.Id), token);
        if (!party.Members.Contains(session.Id))
        {
            _store.RemoveSession(session.Id);
            party.Members.Add(session.Id);
        }
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
             .Where(g => _store.Parties.TryGetValue(g.GroupId, out var p) && p.Mode == GroupMode.Public).ToList();
        }
    }
    public GroupInfoDto? GetGroup(SessionInfo session, Guid groupId)
    {
        lock (_store.Gate)
        {
            Status.EnsureAvailable();
            if (!_store.Parties.TryGetValue(groupId, out var p) || (p.Mode == GroupMode.Private && !p.Members.Contains(session.Id))) return null;
            return _inner.GetGroup(session, groupId);
        }
    }
    public void HandleRequest(SessionInfo session, IGroupPlaybackRequest request, CancellationToken cancellationToken)
    {
        lock (_store.Gate)
        {
            Status.EnsureAvailable();
            if (_store.ForSession(session.Id) is null) throw new PartyException(404, "Party not found.");
            _inner.HandleRequest(session, request, cancellationToken);
        }
    }
    public bool IsUserActive(Guid userId) => _inner.IsUserActive(userId);
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
