using Xunit;
using KoLibri.Core.Spiel;

namespace KoLibri.Core.Tests;

public sealed class JsonFileAdventureRunCheckpointStoreTests
{
    [Fact]
    public async Task PersistsAcrossStoreInstancesAndRejectsStaleCas()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kolibri-checkpoints-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = new JsonFileAdventureRunCheckpointStore(directory);
            var checkpoint = new AdventureCheckpoint
            {
                RunId = "resume-1",
                AdventureId = "skeleton",
                Revision = 1
            };
            Assert.True((await first.CompareAndSwapAsync("resume-1", null, checkpoint)).Succeeded);

            var second = new JsonFileAdventureRunCheckpointStore(directory);
            var loaded = await second.LoadAsync("resume-1");
            Assert.Equal(checkpoint.RunId, loaded?.RunId);
            Assert.Equal(1, loaded?.Revision);

            var stale = checkpoint with { Revision = 2 };
            var conflict = await second.CompareAndSwapAsync("resume-1", checkpoint with { Revision = 0 }, stale);
            Assert.True(conflict.Conflict);
            Assert.Equal(1, conflict.Current?.Revision);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
