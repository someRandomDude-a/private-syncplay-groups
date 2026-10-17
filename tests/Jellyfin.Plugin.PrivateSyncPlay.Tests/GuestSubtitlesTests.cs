using System.Text;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.PrivateSyncPlay.Tests;

public sealed class GuestSubtitlesTests
{
    [Fact] public async Task NativeTextDeliveryIsBoundedCachedAndPreservesMovieTimestamps()
    {
        var encoder = new Mock<ISubtitleEncoder>();
        encoder.Setup(x => x.GetSubtitles(It.IsAny<BaseItem>(), "source", 2, "vtt", 0, 0, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes("WEBVTT\n\n00:01:00.000 --> 00:01:05.000\nHello\n")));
        using var service = new GuestSubtitles(encoder.Object, TimeProvider.System);
        var party = Guid.NewGuid(); var request = new GuestSubtitleRequest(new Video(), "source", 2, 60);
        var first = await service.Read(party, "generation", request, default);
        Assert.Contains("00:01:00.000", Encoding.UTF8.GetString(first));
        Assert.Same(first, await service.Read(party, "generation", request, default));
        encoder.Verify(x => x.GetSubtitles(It.IsAny<BaseItem>(), "source", 2, "vtt", 0, 0, true, It.IsAny<CancellationToken>()), Times.Once);
    }
    [Fact] public async Task OversizedSubtitlesFailRatherThanAllocatingWithoutABound()
    {
        var encoder = new Mock<ISubtitleEncoder>();
        encoder.Setup(x => x.GetSubtitles(It.IsAny<BaseItem>(), It.IsAny<string>(), It.IsAny<int>(), "vtt", 0, 0, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(new byte[2 * 1024 * 1024 + 1]));
        using var service = new GuestSubtitles(encoder.Object, TimeProvider.System);
        Assert.Equal(413, (await Assert.ThrowsAsync<PartyException>(() => service.Read(Guid.NewGuid(), "generation", new GuestSubtitleRequest(new Video(), "source", 2, 0), default))).Status);
    }
    [Theory]
    [InlineData("https://evil.example/subtitles.srt")]
    [InlineData("../secret.srt")]
    public void RemoteOrRelativeExternalSubtitleReferencesAreNeverExposed(string path)
    {
        Assert.False(GuestSubtitlePolicy.Allowed(new MediaSourceInfo { Path = Path.Combine(Path.GetTempPath(), "video.mp4") },
            new MediaStream { Index = 2, Type = MediaStreamType.Subtitle, Codec = "srt", IsExternal = true, Path = path }));
    }
    [Fact] public void OnlyAdjacentLocalSubtitleFilesAreAllowed()
    {
        var root = Path.Combine(Path.GetTempPath(), "opencode", "guest-subtitles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "media", "subs"));
        try
        {
            var allowed = Path.Combine(root, "media", "subs", "movie.en.srt");
            var outside = Path.Combine(root, "private.srt");
            File.WriteAllText(allowed, "1\n00:00:00,000 --> 00:00:01,000\nHello\n"); File.WriteAllText(outside, "Private");
            var source = new MediaSourceInfo { Path = Path.Combine(root, "media", "movie.mp4") };
            var stream = new MediaStream { Index = 2, Type = MediaStreamType.Subtitle, Codec = "srt", IsExternal = true, Path = allowed };
            Assert.True(GuestSubtitlePolicy.Allowed(source, stream));
            stream.Path = outside; Assert.False(GuestSubtitlePolicy.Allowed(source, stream));
        }
        finally { Directory.Delete(root, true); }
    }
}
