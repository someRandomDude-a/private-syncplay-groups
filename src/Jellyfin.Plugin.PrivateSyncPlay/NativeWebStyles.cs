using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.PrivateSyncPlay;

// Reuse installed CSS, never bundle a stale copy of Jellyfin or load its authenticated app JS.
// Only server-selected, existing local stylesheet names are exposed; no client path/proxy input.
public sealed class NativeWebStyles(IApplicationPaths paths)
{
    private readonly Lazy<(string[] Files, string Theme)> _styles = new(() =>
    {
        try { return Discover(paths.WebPath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        { return ([], "dark"); } // A missing, unreadable or changing web installation must not break the player.
    });
    public string Theme => _styles.Value.Theme;
    public string Css => string.Join("\n", _styles.Value.Files.Select(f => $"@import url(\"../../web/{Uri.EscapeDataString(f)}\");"));
    public string ThemeLink => $"<link rel=\"stylesheet\" href=\"../web/themes/{WebUtility.HtmlEncode(Theme)}/theme.css\">";

    private static (string[], string) Discover(string root)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return ([], "dark");
        var selected = new List<string>();
        var index = Path.Combine(root, "index.html");
        if (File.Exists(index) && new FileInfo(index).Length <= 1024 * 1024)
        {
            foreach (Match tag in Regex.Matches(File.ReadAllText(index), @"<link\b[^>]*>", RegexOptions.IgnoreCase))
            {
                if (!Regex.IsMatch(tag.Value, "rel=[\"']stylesheet[\"']", RegexOptions.IgnoreCase)) continue;
                var href = Regex.Match(tag.Value, "href=[\"'](?<url>[^\"']+)[\"']", RegexOptions.IgnoreCase).Groups["url"].Value;
                var file = WebUtility.HtmlDecode(href).Split('?')[0];
                if (file == Path.GetFileName(file) && file.EndsWith(".css", StringComparison.OrdinalIgnoreCase)
                    && !file.Contains(':') && !file.Contains('\\') && File.Exists(Path.Combine(root, file))) selected.Add(file);
            }
        }
        // Some controls are lazy CSS chunks and are not listed in index.html.
        var markers = new HashSet<string> { "emby-button", "inputContainer", "checkboxContainer", "selectContainer", "formDialog", "dialog", "listItem", "material-icons", "mdl-slider", "videoOsdBottom", "osdControls", "paper-icon-button-light", "htmlvideoplayer", "detailTable" };
        var files = Directory.EnumerateFiles(root, "*.css").OrderBy(f => selected.Contains(Path.GetFileName(f)) ? 0 : 1).ThenBy(f => f).ToArray();
        foreach (var file in files)
        {
            if (new FileInfo(file).Length > 8 * 1024 * 1024) continue;
            var css = File.ReadAllText(file);
            var found = markers.Where(m => css.Contains("." + m + "{", StringComparison.Ordinal)
                || css.Contains("." + m + " {", StringComparison.Ordinal)).ToArray();
            if (found.Length == 0) continue;
            if (!selected.Contains(Path.GetFileName(file))) selected.Add(Path.GetFileName(file));
            foreach (var marker in found) markers.Remove(marker);
            if (markers.Count == 0) break;
        }
        var theme = "dark";
        var config = Path.Combine(root, "config.json");
        if (File.Exists(config) && new FileInfo(config).Length <= 1024 * 1024)
        {
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(config));
                if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("themes", out var themes)
                    && themes.ValueKind == JsonValueKind.Array)
                    foreach (var item in themes.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("default", out var isDefault) && isDefault.ValueKind == JsonValueKind.True
                            && item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() is { } name
                            && Regex.IsMatch(name, @"\A[a-z0-9_-]+\z") && File.Exists(Path.Combine(root, "themes", name, "theme.css"))) theme = name;
            }
            catch (JsonException) { /* Installed CSS still works with the native dark default. */ }
        }
        return (selected.Distinct().ToArray(), theme);
    }
}
