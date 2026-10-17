using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.PrivateSyncPlay;

public static class GuestSubtitlePolicy
{
    public static bool Allowed(MediaSourceInfo source, MediaStream stream)
    {
        try { return AllowedFile(source, stream); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }
    private static bool AllowedFile(MediaSourceInfo source, MediaStream stream)
    {
        if (stream.Type != MediaStreamType.Subtitle || stream.Index < 0) return false;
        if (!stream.IsExternal) return true;
        // Never fetch a remote subtitle URL or follow an arbitrary filesystem reference for a guest.
        if (string.IsNullOrEmpty(stream.Path) || !Path.IsPathFullyQualified(stream.Path) || !File.Exists(stream.Path)) return false;
        if (stream.IsTextSubtitleStream && new FileInfo(stream.Path).Length > 2 * 1024 * 1024) return false;
        var root = Path.GetDirectoryName(Path.GetFullPath(source.Path));
        if (root is null) return false;
        var full = Path.GetFullPath(stream.Path);
        var relative = Path.GetRelativePath(root, full);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return false;
        // A neighboring symlink/junction must not turn this into a read outside the authorized media directory.
        if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) return false;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        for (var directory = Path.GetDirectoryName(full); !string.Equals(directory, root, comparison); directory = Path.GetDirectoryName(directory))
            if (directory is null || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }
    public static bool NeedsBurnIn(MediaStream stream) => !stream.IsTextSubtitleStream
        || string.Equals(stream.Codec, "ass", StringComparison.OrdinalIgnoreCase) || string.Equals(stream.Codec, "ssa", StringComparison.OrdinalIgnoreCase);
}
