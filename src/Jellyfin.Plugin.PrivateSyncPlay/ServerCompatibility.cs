using System.Reflection;

namespace Jellyfin.Plugin.PrivateSyncPlay;

public static class ServerCompatibility
{
    public static string TargetAbi { get; } = typeof(Plugin).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "JellyfinTargetAbi")?.Value ?? "0.0.0.0";
    public static bool Supports(Version? version)
    {
        return Version.TryParse(TargetAbi, out var target) && target.Major > 0 && version is not null && version.Major == target.Major && version.Minor == target.Minor
            && Math.Max(0, version.Build) == target.Build;
    }
}
