using MediaBrowser.Common.Configuration;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.PrivateSyncPlay.Tests;

public sealed class NativeWebStylesTests
{
    [Fact] public void UsesInstalledStylesAndLazyControlChunksWithoutExternalOrScriptImports()
    {
        var root = Path.Combine(Path.GetTempPath(), "opencode", "private-syncplay-css-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "themes", "light"));
        try
        {
            File.WriteAllText(Path.Combine(root,"index.html"), "<link href=\"main.abc.css?123\" rel=\"stylesheet\"><link href=\"https://evil.example/a.css\" rel=\"stylesheet\"><script src=\"app.js\"></script>");
            File.WriteAllText(Path.Combine(root,"main.abc.css"), ".inputContainer{margin:1em}");
            File.WriteAllText(Path.Combine(root,"checkbox.123.css"), ".checkboxContainer{display:flex}");
            File.WriteAllText(Path.Combine(root,"unrelated.css"), ".unrelated{display:none}");
            File.WriteAllText(Path.Combine(root,"themes","light","theme.css"), "html{color:inherit}");
            File.WriteAllText(Path.Combine(root,"config.json"), "{\"themes\":[{\"id\":\"light\",\"default\":true}]}");
            var paths = new Mock<IApplicationPaths>(); paths.SetupGet(p => p.WebPath).Returns(root);
            var styles = new NativeWebStyles(paths.Object);
            Assert.Contains("main.abc.css", styles.Css); Assert.Contains("checkbox.123.css", styles.Css);
            Assert.DoesNotContain("evil", styles.Css); Assert.DoesNotContain("app.js", styles.Css); Assert.DoesNotContain("unrelated", styles.Css);
            Assert.Equal("light", styles.Theme); Assert.Contains("themes/light/theme.css", styles.ThemeLink);
        }
        finally { Directory.Delete(root,true); }
    }
    [Fact] public void MissingWebClientDoesNotExposeFilesystemPaths()
    {
        var styles = new NativeWebStyles(Mock.Of<IApplicationPaths>());
        Assert.Empty(styles.Css); Assert.Equal("dark",styles.Theme);
    }
    [Theory]
    [InlineData("[]")]
    [InlineData("{\"themes\":{}}")]
    [InlineData("{\"themes\":[null,1,{\"id\":17,\"default\":true}]}")]
    [InlineData("not json")]
    public void ChangedOrMalformedWebConfigurationFallsBackSafely(string json)
    {
        var root = Path.Combine(Path.GetTempPath(), "opencode", "private-syncplay-css-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "config.json"), json);
            var paths = new Mock<IApplicationPaths>(); paths.SetupGet(p => p.WebPath).Returns(root);
            var styles = new NativeWebStyles(paths.Object);
            Assert.Equal("dark", styles.Theme);
        }
        finally { Directory.Delete(root, true); }
    }
}
