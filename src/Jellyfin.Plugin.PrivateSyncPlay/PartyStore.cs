using System.Security.Cryptography;

namespace Jellyfin.Plugin.PrivateSyncPlay;

public sealed class PartyException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

// All methods are called under Gate, including the corresponding core engine operation.
// Guest credentials deliberately do NOT implement any Jellyfin authentication scheme.
public sealed class PartyStore(TimeProvider clock)
{
    public object Gate { get; } = new();
    public Dictionary<Guid, Party> Parties { get; } = [];
    private readonly Dictionary<string, Invite> _invites = [];
    private readonly Dictionary<string, Guest> _guests = [];
    private readonly Dictionary<string, (DateTimeOffset Start, int Count)> _attempts = [];
    // Raised after revocation, under Gate. GuestMedia owns the one stream for this party.
    internal event Action<Guid>? GuestAccessEnded;
    public DateTimeOffset Now => clock.GetUtcNow();
    public static string Secret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private static string Digest(string secret) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret)));

    public Party Create(Guid id, Guid owner, string session, GroupMode mode, int capacity)
    {
        string code;
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        do { code = new string(Enumerable.Range(0, 8).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray()); }
        while (Parties.Values.Any(p => p.Code == code));
        var party = new Party(id, owner, session, mode, code, capacity);
        party.Members.Add(session, owner);
        Parties.Add(id, party);
        return party;
    }

    public Party ByCode(string code) => !string.IsNullOrWhiteSpace(code) && code.Length <= 16 ? Parties.Values.FirstOrDefault(p => p.Mode == GroupMode.Private
        && p.Code == code.Trim().ToUpperInvariant()) ?? throw new PartyException(404, "Invalid or expired invitation.")
        : throw new PartyException(404, "Invalid or expired invitation.");
    public Party Require(Guid id) => Parties.GetValueOrDefault(id) ?? throw new PartyException(404, "Party not found.");
    public Party? ForSession(string session, Guid user) => Parties.Values.FirstOrDefault(p => p.IsMember(session, user));

    public void RateLimit(string key, int limit = 20)
    {
        foreach (var old in _attempts.Where(x => Now - x.Value.Start > TimeSpan.FromMinutes(1)).Select(x => x.Key).ToArray()) _attempts.Remove(old);
        if (!_attempts.TryGetValue(key, out var value)) value = (Now, 0);
        if (value.Count >= limit || _attempts.Count >= 10000) throw new PartyException(429, "Too many attempts. Try again later.");
        _attempts[key] = (value.Start, value.Count + 1);
    }
    public void RateLimitGuest(string credential, string operation, int limit)
        => RateLimit("guest-" + operation + ":" + Digest(credential), limit);

    public void CheckSeat(Party party, string? existingSession, Configuration config)
    {
        Sweep(config);
        if (!Parties.TryGetValue(party.Id, out var current) || !ReferenceEquals(party, current)) throw new PartyException(404, "Party not found.");
        if (existingSession is not null && party.Members.ContainsKey(existingSession)) return;
        var count = party.Members.Count + _guests.Values.Count(g => g.PartyId == party.Id);
        if (count >= Math.Min(party.Capacity, config.MaxViewers)) throw new PartyException(409, "This party is full.");
    }

    public (string Token, Invite Value) Issue(Party party, Guid user, bool guests, int uses, DateTimeOffset? expiry, Configuration config)
    {
        Sweep(config);
        if (!Parties.TryGetValue(party.Id, out var current) || !ReferenceEquals(party, current)) throw new PartyException(404, "Party not found.");
        if (party.Owner != user) throw new PartyException(403, "Only the party owner may create invitations.");
        if (guests && !party.Policy.AllowGuests) throw new PartyException(403, "Guest access is disabled for this party.");
        if (guests && !config.CanInviteGuests(user)) throw new PartyException(403, "Guest invitations are disabled by your administrator.");
        if (uses < 1 || uses > config.MaxInviteUses || expiry <= Now) throw new PartyException(400, "Invalid invitation limits.");
        if (_invites.Values.Count(x => x.PartyId == party.Id) >= 100) throw new PartyException(409, "Too many active invitations.");
        var token = Secret();
        var invite = new Invite(Guid.NewGuid(), party.Id, guests, uses, expiry);
        _invites.Add(Digest(token), invite);
        return (token, invite);
    }

    public Invite ValidateInvite(string token, Configuration config, string? existingSession = null)
    {
        Sweep(config);
        if (token is null || token.Length != 64 || !_invites.TryGetValue(Digest(token), out var invite)
            || invite.ExpiresAt <= Now || !Parties.TryGetValue(invite.PartyId, out var party)
            || (invite.Uses >= Math.Min(invite.MaxUses, config.MaxInviteUses) && (existingSession is null || !party.Members.ContainsKey(existingSession))))
            throw new PartyException(404, "Invalid or expired invitation.");
        return invite;
    }

    public string RedeemGuest(string token, string? existing, Configuration config)
    {
        Sweep(config);
        if (token is null || token.Length != 64) throw new PartyException(404, "Invalid or expired invitation.");
        var prior = ExistingGuest(token, existing);
        if (prior is not null)
        { prior.LastSeen = Now; return existing!; }
        var invite = ValidateInvite(token, config);
        var party = Require(invite.PartyId);
        if (!party.Policy.AllowJoins) throw new PartyException(403, "This party is not accepting new viewers.");
        if (!invite.AllowGuests || !party.Policy.AllowGuests || !config.CanInviteGuests(party.Owner)) throw new PartyException(403, "Guest access is not permitted.");
        CheckSeat(party, null, config);
        var secret = Secret();
        // A browser gets one party-scoped lease; switching must not strand its old seat.
        Guest? replaced = null;
        if (existing is not null) _guests.Remove(Digest(existing), out replaced);
        _guests.Add(Digest(secret), new Guest(party.Id, invite.Id, Now));
        invite.Uses++;
        if (replaced is not null) StopIfNoGuests(replaced.PartyId);
        return secret;
    }

    // Invitation landing preflight only: no use/seat is consumed and no lease heartbeat is sent.
    public bool GuestAdmissionAllowed(string token, string? existing, Configuration config)
    {
        Sweep(config);
        var prior = token?.Length == 64 ? ExistingGuest(token, existing) : null;
        var invite = prior is null ? ValidateInvite(token!, config) : _invites[Digest(token!)];
        var party = Require(invite.PartyId);
        return invite.AllowGuests && party.Policy.AllowGuests && config.CanInviteGuests(party.Owner)
            && (prior is not null || (party.Policy.AllowJoins && party.Members.Count + GuestCount(party.Id) < Math.Min(party.Capacity, config.MaxViewers)));
    }
    private Guest? ExistingGuest(string token, string? credential)
        => credential?.Length == 64 && _guests.TryGetValue(Digest(credential), out var guest)
            && _invites.TryGetValue(Digest(token), out var invite) && guest.InviteId == invite.Id ? guest : null;

    public Party GuestParty(string secret, Configuration config, bool heartbeat = false)
        => Require(GuestSession(secret, config, heartbeat).PartyId);
    public Guest GuestSession(string secret, Configuration config, bool heartbeat = false)
    {
        Sweep(config);
        if (secret is null || secret.Length != 64 || !_guests.TryGetValue(Digest(secret), out var guest)) throw new PartyException(401, "Guest session ended.");
        if (heartbeat) guest.LastSeen = Now;
        return guest;
    }
    public GuestPlayerOptions GuestOptions(string secret, Configuration config)
    {
        GuestSession(secret, config);
        // Guest player options belong exclusively to administrator configuration, never party owners.
        return new() { Features = GuestPlayerPolicy.Features.Where(config.GuestPlayer.Allows).ToArray(), Controls = config.GuestPlayer.Controls.ToArray() };
    }
    public Guest[] Guests(Guid party) => _guests.Values.Where(g => g.PartyId == party).ToArray();
    public void RevokeGuestInvitations(Party party)
    {
        foreach (var invite in Invites(party.Id).Where(i => i.AllowGuests).ToArray()) Revoke(party, invite.Id);
    }

    public void Revoke(Party party, Guid inviteId)
    {
        foreach (var key in _invites.Where(x => x.Value.PartyId == party.Id && x.Value.Id == inviteId).Select(x => x.Key).ToArray()) _invites.Remove(key);
        foreach (var key in _guests.Where(x => x.Value.InviteId == inviteId && x.Value.PartyId == party.Id).Select(x => x.Key).ToArray()) _guests.Remove(key);
        StopIfNoGuests(party.Id);
    }

    public void LeaveGuest(string secret)
    {
        if (_guests.Remove(Digest(secret), out var guest)) StopIfNoGuests(guest.PartyId);
    }
    private void StopIfNoGuests(Guid party)
    {
        if (GuestCount(party) == 0) GuestAccessEnded?.Invoke(party);
    }
    public int GuestCount(Guid party) => _guests.Values.Count(g => g.PartyId == party);
    public IEnumerable<Invite> Invites(Guid party) => _invites.Values.Where(i => i.PartyId == party).ToArray();
    public void RemoveSession(string session)
    {
        foreach (var party in Parties.Values.ToArray())
        {
            party.ReadOnlySessions.Remove(session);
            if (party.Members.Remove(session) && party.Members.Count == 0) Close(party.Id);
        }
    }
    public void Close(Guid id)
    {
        Parties.Remove(id);
        foreach (var key in _guests.Where(x => x.Value.PartyId == id).Select(x => x.Key).ToArray()) _guests.Remove(key);
        foreach (var key in _invites.Where(x => x.Value.PartyId == id).Select(x => x.Key).ToArray()) _invites.Remove(key);
        StopIfNoGuests(id);
    }
    public void Sweep(Configuration config)
    {
        // Administrative revocation is permanent for existing guest links, not merely a UI toggle.
        // Account-only invitations are independent and remain usable.
        foreach (var party in Parties.Values.Where(p => !p.Policy.AllowGuests || !config.CanInviteGuests(p.Owner)).ToArray())
            RevokeGuestInvitations(party);
        var expired = _guests.Where(x => !Parties.TryGetValue(x.Value.PartyId, out var party)
            || party.Members.Count == 0 || !party.Policy.AllowGuests || !config.CanInviteGuests(party.Owner)
            || Now - x.Value.LastSeen > TimeSpan.FromSeconds(config.GuestLeaseSeconds)).ToArray();
        foreach (var pair in expired) _guests.Remove(pair.Key);
        foreach (var id in expired.Select(x => x.Value.PartyId).Distinct()) StopIfNoGuests(id);
        foreach (var pair in _invites.Where(x => x.Value.ExpiresAt <= Now).ToArray())
        { if (Parties.TryGetValue(pair.Value.PartyId, out var party)) Revoke(party, pair.Value.Id); }
    }
}

public sealed class Party(Guid id, Guid owner, string hostSession, GroupMode mode, string code, int capacity)
{
    public Guid Id { get; } = id;
    public Guid Owner { get; } = owner;
    public string HostSession { get; } = hostSession;
    public GroupMode Mode { get; } = mode;
    public string Code { get; } = code;
    public int Capacity { get; set; } = capacity;
    // Session IDs can survive an account switch. Never let a new user inherit the old admission.
    public Dictionary<string, Guid> Members { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool IsMember(string session, Guid user) => user != Guid.Empty && Members.TryGetValue(session, out var admitted) && admitted == user;
    public Dictionary<Guid, PartyAccount> Accounts { get; } = new() { [owner] = new(owner) };
    public HashSet<Guid> BlockedUsers { get; } = [];
    public HashSet<string> ReadOnlySessions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public PartyPolicy Policy { get; set; } = new();
    public PartyAccount Account(Guid user) => Accounts.TryGetValue(user, out var account) ? account : Accounts[user] = new(user);
    public bool CanControl(Guid user) => user == Owner || (Policy.ParticipantsControlPlayback && Account(user).ControlPlayback);
    public bool CanEditQueue(Guid user) => user == Owner || (CanControl(user) && Policy.ParticipantsEditQueue && Account(user).EditQueue);
}
public sealed record PartyPolicy(bool AllowJoins = true, bool AllowGuests = true, bool ParticipantsControlPlayback = true,
    bool ParticipantsEditQueue = true);
public sealed class PartyAccount(Guid userId)
{
    public Guid Id { get; } = Guid.NewGuid();
    public Guid UserId { get; } = userId;
    public bool ControlPlayback { get; set; } = true;
    public bool EditQueue { get; set; } = true;
}
public sealed class Invite(Guid id, Guid partyId, bool guests, int maxUses, DateTimeOffset? expiresAt)
{
    public Guid Id { get; } = id;
    public Guid PartyId { get; } = partyId;
    public bool AllowGuests { get; } = guests;
    public int MaxUses { get; } = maxUses;
    public int Uses { get; set; }
    public DateTimeOffset? ExpiresAt { get; } = expiresAt;
}
public sealed class Guest(Guid partyId, Guid inviteId, DateTimeOffset lastSeen)
{
    public Guid Id { get; } = Guid.NewGuid();
    public Guid PartyId { get; } = partyId;
    public Guid InviteId { get; } = inviteId;
    public DateTimeOffset LastSeen { get; set; } = lastSeen;
}
