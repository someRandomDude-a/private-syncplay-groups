using System.Text;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.PrivateSyncPlay.Tests;

public sealed class GuestNativeStreamsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "opencode", "native-guest-stream-" + Guid.NewGuid().ToString("N"));
    private readonly PartyStore _store = new(TimeProvider.System);
    private readonly Configuration _config = new() { EnableGuestInvites = true, DefaultGuestPermission = true };
    private readonly Mock<ISessionManager> _sessions = new();
    private readonly Mock<ITranscodeManager> _transcodes = new();
    private readonly Mock<IConfigurationManager> _configuration = new();
    private readonly Mock<IApplicationPaths> _paths = new();
    private readonly User _owner = new("owner", "auth", "reset") { SyncPlayAccess = SyncPlayUserAccessType.CreateAndJoinGroups };
    private readonly SessionInfo _session;
    private readonly Video _video;
    private readonly MediaSourceInfo _source;
    private readonly TranscodingJob _job;
    private readonly GuestNativeStreams _native;
    private readonly Party _party;
    private readonly string _guest, _prefix = Guid.NewGuid().ToString("N");

    public GuestNativeStreamsTests()
    {
        Directory.CreateDirectory(_root);
        _video = new Video { Id = Guid.NewGuid(), Path = Path.Combine(_root, "authorized.mp4"), RunTimeTicks = TimeSpan.FromMinutes(3).Ticks };
        File.WriteAllText(_video.Path, "fixture");
        _source = new() { Id = "source", Path = _video.Path, Protocol = MediaProtocol.File, VideoType = VideoType.VideoFile, DefaultAudioStreamIndex = 1,
            MediaStreams = [new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = "h264", BitDepth = 8, BitRate = 400000, PixelFormat = "yuv420p", ColorTransfer = "bt709" },
                new MediaStream { Index = 1, Type = MediaStreamType.Audio, Codec = "aac", Channels = 2, BitRate = 96000 }] };
        _session = new(_sessions.Object, NullLogger.Instance) { Id = "member", UserId = _owner.Id, DeviceId = "member-device", NowPlayingItem = new BaseItemDto { Id = _video.Id },
            TranscodingInfo = new TranscodingInfo { Container = "ts", VideoCodec = "h264", AudioCodec = "aac", AudioChannels = 2, Bitrate = 500000, IsVideoDirect = true, IsAudioDirect = true } };
        _session.PlayState.SubtitleStreamIndex = -1;
        _session.PlayState.AudioStreamIndex = 1;
        _sessions.SetupGet(s => s.Sessions).Returns([_session]);
        _configuration.Setup(c => c.GetConfiguration("encoding")).Returns(new EncodingOptions { TranscodingTempPath = _root });
        _paths.SetupGet(p => p.CachePath).Returns(_root);
        _job = new(NullLogger<TranscodingJob>.Instance) { Path = Path.Combine(_root, _prefix + ".m3u8"), DeviceId = _session.DeviceId,
            Type = TranscodingJobType.Hls, MediaSource = _source, PlaySessionId = "private-play-session" };
        _transcodes.Setup(t => t.GetTranscodingJob(_job.PlaySessionId)).Returns(_job);
        WritePlaylist("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-MEDIA-SEQUENCE:0\n#EXTINF:2,\n" + _prefix + "0.ts\n#EXTINF:2,\n" + _prefix + "1.ts\n");
        File.WriteAllText(Path.Combine(_root, _prefix + "0.ts"), "native segment zero");
        File.WriteAllText(Path.Combine(_root, _prefix + "1.ts"), "native segment one");
        _party = _store.Create(Guid.NewGuid(), _owner.Id, _session.Id, GroupMode.Private, 4);
        _guest = _store.RedeemGuest(_store.Issue(_party, _owner.Id, true, 2, null, _config).Token, null, _config);
        _native = new(_store, _sessions.Object, _transcodes.Object, _configuration.Object, _paths.Object);
        _sessions.Raise(s => s.PlaybackStart += null, new PlaybackStartEventArgs { Session = _session, Item = _video, PlaySessionId = _job.PlaySessionId });
    }

    private void WritePlaylist(string text) => File.WriteAllText(_job.Path!, text);
    private NativeGuestStream? Capture() => _native.Capture(_party, [_session], _video, 2000, 0, null, 1);

    [Fact] public void NativeOutputIsReusedReadOnlyWithARebuiltCredentialFreeManifestAndExplicitSegmentMapping()
    {
        var stream = Assert.IsType<NativeGuestStream>(Capture());
        var manifest = Encoding.UTF8.GetString(GuestNativeStreams.Read(stream, "index.m3u8").Bytes);
        Assert.Contains("segment000000000.ts", manifest); Assert.DoesNotContain(_prefix, manifest);
        Assert.DoesNotContain(_job.PlaySessionId!, manifest); Assert.DoesNotContain(_root, manifest);
        Assert.Equal("native segment zero", Encoding.UTF8.GetString(GuestNativeStreams.Read(stream, "segment000000000.ts").Bytes));
        Assert.Throws<PartyException>(() => GuestNativeStreams.Read(stream, "../authorized.mp4"));
        _transcodes.Verify(t => t.GetTranscodingJob(_job.PlaySessionId!), Times.AtLeastOnce); _transcodes.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("#EXT-X-KEY:METHOD=AES-128,URI=\"https://evil.example/private\"\n")]
    [InlineData("#EXT-X-MAP:URI=\"private.mp4\"\n")]
    [InlineData("#EXTINF:2,\n../private.ts\n")]
    [InlineData("#EXTINF:2,\nhttps://evil.example/media.ts\n")]
    [InlineData("#EXT-X-MEDIA-SEQUENCE:20\n")]
    public void AmbiguousOrUnsafeNativeManifestsAreNotForwarded(string extra)
    {
        WritePlaylist("#EXTM3U\n" + extra + "#EXTINF:2,\n" + _prefix + "0.ts\n"); Assert.Null(Capture());
    }

    [Fact] public void DifferentItemsDevicesAccountsCodecsBitratesAndSubtitleBurnInCannotBeBorrowed()
    {
        Assert.Null(_native.Capture(_party, [_session], _video, 2000, 0, 2));
        _source.Path += "other"; Assert.Null(Capture()); _source.Path = _video.Path;
        _job.DeviceId = "other-device"; Assert.Null(Capture()); _job.DeviceId = _session.DeviceId;
        _session.TranscodingInfo!.VideoCodec = "hevc"; Assert.Null(Capture()); _session.TranscodingInfo.VideoCodec = "h264";
        _source.MediaStreams[0].BitRate = 5000000; Assert.Null(Capture()); _source.MediaStreams[0].BitRate = 400000;
        _session.PlayState.SubtitleStreamIndex = 2; Assert.Null(Capture()); _session.PlayState.SubtitleStreamIndex = -1;
        _source.MediaStreams[0].BitDepth = 10; Assert.Null(Capture()); _source.MediaStreams[0].BitDepth = 8;
        _session.UserId = Guid.NewGuid(); Assert.Null(Capture());
    }

    private GuestMedia Media()
    {
        foreach (var kind in new[] { PermissionKind.EnableMediaPlayback, PermissionKind.EnablePlaybackRemuxing }) _owner.Permissions.Add(new Permission(kind, true));
        var users = new Mock<IUserManager>(); users.Setup(u => u.GetUserById(_owner.Id)).Returns(_owner);
        var library = new Mock<ILibraryManager>(); library.Setup(l => l.GetItemById<Video>(_video.Id, _owner)).Returns(_video);
        var sources = new Mock<IMediaSourceManager>(); sources.Setup(s => s.GetStaticMediaSources(_video, false, _owner)).Returns([_source]);
        return new GuestMedia(_store, _sessions.Object, library.Object, Mock.Of<IMediaEncoder>(), _paths.Object, users.Object,
            new GuestEncoding(null!, _configuration.Object, sources.Object), () => _config, native: _native);
    }

    [Fact] public void LastGuestDetachesWithoutStoppingOrDeletingTheMembersNativeStream()
    {
        using var media = Media();
        var state = media.State(_party); Assert.Equal("Shared", state.PlaybackMethod); Assert.True(state.Ready);
        Assert.Contains("#EXTM3U", Encoding.UTF8.GetString(media.Read(_party, state.Generation!, "index.m3u8").Bytes));
        _store.LeaveGuest(_guest);
        Assert.Throws<PartyException>(() => media.State(_party)); Assert.True(File.Exists(_job.Path)); Assert.False(_job.HasExited);
        _transcodes.Verify(t => t.GetTranscodingJob(_job.PlaySessionId!), Times.AtLeastOnce); _transcodes.VerifyNoOtherCalls();
    }

    [Fact] public void NativeReuseCannotBypassTheServerWideGuestStreamLimit()
    {
        _config.MaxGuestStreams = 1;
        using var media = Media(); Assert.Equal("Shared", media.State(_party).PlaybackMethod);
        var other = new SessionInfo(_sessions.Object, NullLogger.Instance) { Id = "second-member", UserId = _owner.Id, DeviceId = "second-device",
            NowPlayingItem = new BaseItemDto { Id = _video.Id }, TranscodingInfo = _session.TranscodingInfo };
        other.PlayState.AudioStreamIndex = 1; other.PlayState.SubtitleStreamIndex = -1;
        _sessions.SetupGet(s => s.Sessions).Returns([_session, other]);
        var party = _store.Create(Guid.NewGuid(), _owner.Id, other.Id, GroupMode.Private, 3);
        _store.RedeemGuest(_store.Issue(party, _owner.Id, true, 1, null, _config).Token, null, _config);
        using var job = new TranscodingJob(NullLogger<TranscodingJob>.Instance) { Path = _job.Path, MediaSource = _source, DeviceId = other.DeviceId,
            PlaySessionId = "second-play", Type = TranscodingJobType.Hls };
        _transcodes.Setup(t => t.GetTranscodingJob(job.PlaySessionId)).Returns(job);
        _sessions.Raise(s => s.PlaybackStart += null, new PlaybackStartEventArgs { Session = other, Item = _video, PlaySessionId = job.PlaySessionId });
        Assert.Equal(503, Assert.Throws<PartyException>(() => media.State(party)).Status);
        Assert.Equal("Shared", media.State(_party).PlaybackMethod); Assert.True(File.Exists(_job.Path));
    }

    [Fact] public void EveryBorrowedReadRequiresTheSameCurrentNativeJobAndUserBinding()
    {
        var stream = Assert.IsType<NativeGuestStream>(Capture());
        Assert.True(_native.IsCurrent(_party, [_session], stream, 2000));
        Assert.False(_native.IsCurrent(_party, [_session], stream, 256));
        _session.UserId = Guid.NewGuid(); Assert.False(_native.IsCurrent(_party, [_session], stream, 2000));
    }

    [Fact] public void SingleAudioSourceDoesNotRequireANativeDefaultIndexAndCopiedRatesUseTheSourceNotTheRequestedBudget()
    {
        _source.DefaultAudioStreamIndex = null; _session.PlayState.AudioStreamIndex = null;
        _session.TranscodingInfo!.Bitrate = 2128000; // Native reports its requested 2M + audio, despite actually copying 400k + 96k.
        Assert.NotNull(_native.Capture(_party, [_session], _video, 512, 0, null, 1));
        _source.MediaStreams = [.._source.MediaStreams, new MediaStream { Index = 2, Type = MediaStreamType.Audio, Codec = "aac", BitRate = 96000 }];
        Assert.Null(Capture()); // Ambiguous audio selection must not be guessed.
    }

    public void Dispose() { _native.Dispose(); _job.Dispose(); Directory.Delete(_root, true); }
}
