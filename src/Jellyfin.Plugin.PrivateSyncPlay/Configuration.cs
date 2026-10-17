using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.PrivateSyncPlay;

public enum GroupMode { Public, Private }
public enum PermissionOverride { Inherit, Allow, Deny }

public sealed class UserSettings
{
    public Guid UserId { get; set; }
    public GroupMode? DefaultMode { get; set; }
    public PermissionOverride CreatePublic { get; set; }
    public PermissionOverride CreatePrivate { get; set; }
    public PermissionOverride GuestInvites { get; set; }
}

public sealed class Configuration : BasePluginConfiguration
{
    public bool EnablePrivateGroups { get; set; } = true;
    public bool AllowPublicGroups { get; set; } = true;
    public GroupMode DefaultMode { get; set; }
    public bool EnableGuestInvites { get; set; }
    public bool DefaultGuestPermission { get; set; }
    public int MaxViewers { get; set; } = 10;
    public int MaxInviteUses { get; set; } = 10;
    public int GuestLeaseSeconds { get; set; } = 30;
    public int GuestVideoBitrateKbps { get; set; } = 2000;
    public int MaxGuestStreams { get; set; } = 2;
    public List<UserSettings> Users { get; set; } = [];

    public void Validate()
    {
        if (!Enum.IsDefined(DefaultMode) || MaxViewers is < 1 or > 1000 || MaxInviteUses is < 1 or > 10000
            || GuestLeaseSeconds is < 10 or > 120 || GuestVideoBitrateKbps is < 256 or > 10000 || MaxGuestStreams is < 1 or > 32
            || Users is null || Users.Any(x => x is null) || Users.Select(x => x.UserId).Distinct().Count() != Users.Count
            || Users.Any(x => x.UserId == Guid.Empty || !Enum.IsDefined(x.CreatePublic)
                || !Enum.IsDefined(x.CreatePrivate) || !Enum.IsDefined(x.GuestInvites)
                || (x.DefaultMode.HasValue && !Enum.IsDefined(x.DefaultMode.Value))))
            throw new ArgumentException("Invalid Private SyncPlay settings.");
        // A default must be permitted; explicit per-user defaults are checked as well.
        if (DefaultMode == GroupMode.Public ? !AllowPublicGroups : !EnablePrivateGroups)
            throw new ArgumentException("The server default mode must be enabled.");
        foreach (var user in Users.Where(x => x.DefaultMode.HasValue))
            if (!CanCreate(user.UserId, user.DefaultMode!.Value))
                throw new ArgumentException("A user default must be an allowed creation mode.");
    }

    public bool CanCreate(Guid userId, GroupMode mode)
    {
        var user = Users.FirstOrDefault(x => x.UserId == userId);
        return mode switch
        {
            GroupMode.Public => AllowPublicGroups && user?.CreatePublic != PermissionOverride.Deny,
            GroupMode.Private => EnablePrivateGroups && user?.CreatePrivate != PermissionOverride.Deny,
            _ => false
        };
    }

    public GroupMode DefaultFor(Guid userId)
    {
        var mode = Users.FirstOrDefault(x => x.UserId == userId)?.DefaultMode ?? DefaultMode;
        if (CanCreate(userId, mode)) return mode;
        if (CanCreate(userId, GroupMode.Private)) return GroupMode.Private;
        if (CanCreate(userId, GroupMode.Public)) return GroupMode.Public;
        throw new PartyException(403, "Group creation is not permitted.");
    }

    public bool CanInviteGuests(Guid userId)
    {
        var value = Users.FirstOrDefault(x => x.UserId == userId)?.GuestInvites ?? PermissionOverride.Inherit;
        return EnableGuestInvites && (value == PermissionOverride.Allow
            || (value == PermissionOverride.Inherit && DefaultGuestPermission));
    }
}
