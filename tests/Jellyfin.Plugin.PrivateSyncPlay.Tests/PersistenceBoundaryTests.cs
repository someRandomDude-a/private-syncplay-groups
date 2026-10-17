using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace Jellyfin.Plugin.PrivateSyncPlay.Tests;

public sealed class PersistenceBoundaryTests
{
    [Fact]
    public void PluginHasNoDatabaseWriterOrPersistedEntityMutationDependencies()
    {
        // Inspect the compiled code, including compiler-generated workflows; no source regex or extra package.
        using var input = File.OpenRead(typeof(Plugin).Assembly.Location);
        using var assembly = new PEReader(input);
        var metadata = assembly.GetMetadataReader();
        foreach (var handle in metadata.TypeReferences)
        {
            var type = metadata.GetTypeReference(handle);
            var ns = metadata.GetString(type.Namespace);
            Assert.False(ns.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
                || ns.StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal)
                || ns.StartsWith("System.Data", StringComparison.Ordinal)
                || ns.StartsWith("MediaBrowser.Controller.Persistence", StringComparison.Ordinal), ns);
        }

        foreach (var handle in metadata.MemberReferences)
        {
            var member = metadata.GetMemberReference(handle);
            if (member.Parent.Kind != HandleKind.TypeReference) continue;
            var type = metadata.GetTypeReference((TypeReferenceHandle)member.Parent);
            var ns = metadata.GetString(type.Namespace);
            var typeName = metadata.GetString(type.Name);
            var method = metadata.GetString(member.Name);
            if (ns == "Jellyfin.Database.Implementations.Entities")
                Assert.StartsWith("get_", method); // Never mutate a shared Jellyfin User/Permission entity.
            if (ns == "MediaBrowser.Controller.Library")
            {
                if (typeName == "ILibraryManager") Assert.Equal("GetItemById", method);
                if (typeName == "IUserManager") Assert.Equal("GetUserById", method);
                Assert.NotEqual("IUserDataManager", typeName); // Guests must not record progress/watched state.
            }
            if (ns == "MediaBrowser.Controller.MediaEncoding")
            {
                if (typeName == "ITranscodeManager") Assert.Equal("GetTranscodingJob", method);
                if (typeName == "TranscodingJob") Assert.StartsWith("get_", method); // Borrowing never mutates/stops/pings native jobs.
            }
        }
    }
}
