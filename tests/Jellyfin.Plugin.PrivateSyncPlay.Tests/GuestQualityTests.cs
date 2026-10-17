using Xunit;

namespace Jellyfin.Plugin.PrivateSyncPlay.Tests;

public sealed class GuestQualityTests
{
    private static readonly Guid Fast = Guid.NewGuid(), Slow = Guid.NewGuid();
    private static readonly DateTimeOffset Start = DateTimeOffset.UtcNow;

    [Fact] public void WorstGuestLimitsTheSharedBudgetWithHeadroomWithoutExceedingTheAdministratorCap()
    {
        var quality = new GuestQuality();
        Assert.Equal(2000, quality.Select(2000, [Fast, Slow], "stream", Start));
        quality.Report(Fast, "stream", 50000, 10, false, Start);
        quality.Report(Slow, "stream", 1000, 1, false, Start);
        Assert.Equal(2000, quality.Select(2000, [Fast, Slow], "stream", Start.AddSeconds(3))); // No encoder churn.
        Assert.Equal(512, quality.Select(2000, [Fast, Slow], "stream", Start.AddSeconds(5)));
        Assert.Equal(256, quality.Select(256, [Fast, Slow], "stream", Start.AddSeconds(6)));
    }

    [Fact] public void RaisingQualityRequiresSustainedHealthyMeasurementsFromEveryRemainingGuest()
    {
        var quality = new GuestQuality(); quality.Select(2000, [Fast, Slow], "one", Start);
        quality.Report(Slow, "one", 1000, 1, false, Start);
        Assert.Equal(512, quality.Select(2000, [Fast, Slow], "one", Start.AddSeconds(5)));
        // The slow guest left. A new generation needs fresh measurements; recovery is not immediate.
        for (var seconds = 10; seconds <= 70; seconds += 5)
        {
            quality.Report(Fast, "two", 10000, 10, false, Start.AddSeconds(seconds));
            var rate = quality.Select(2000, [Fast], "two", Start.AddSeconds(seconds));
            Assert.Equal(seconds == 70 ? 768 : 512, rate);
        }
    }

    [Fact] public void OldGenerationsAndStaleSamplesDoNotForceChangesAndUnknownGuestsCannotTriggerRecovery()
    {
        var quality = new GuestQuality(); quality.Select(2000, [Fast, Slow], "one", Start);
        quality.Report(Slow, "wrong", 100, 0, true, Start);
        Assert.Equal(2000, quality.Select(2000, [Fast, Slow], "one", Start.AddSeconds(5)));
        quality.Report(Slow, "one", 1000, 0, false, Start.AddSeconds(5));
        Assert.Equal(2000, quality.Select(2000, [Fast, Slow], "one", Start.AddSeconds(35)));
    }

    [Fact] public void BufferingCanLowerQualityWhenNativeHlsHasNoThroughputEstimateButReportsCannotFloodEncoderChanges()
    {
        var quality = new GuestQuality(); quality.Select(2000, [Slow], "one", Start);
        quality.Report(Slow, "one", null, 0, true, Start);
        quality.Report(Slow, "one", 10000, 10, false, Start.AddSeconds(1)); // Too early: ignored.
        Assert.Equal(1500, quality.Select(2000, [Slow], "one", Start.AddSeconds(5)));
        Assert.Equal(1500, quality.Select(2000, [Slow], "one", Start.AddSeconds(10))); // A single stall is not counted repeatedly.
        Assert.Equal(1500, quality.Select(2000, [Slow], "two", Start.AddSeconds(120))); // No data is not proof of improvement.
    }

    [Fact] public void EditingTheAdministratorCeilingDoesNotUndoANetworkDrivenReductionWithoutFreshRecoveryEvidence()
    {
        var quality = new GuestQuality(); quality.Select(2000, [Slow], "one", Start);
        quality.Report(Slow, "one", 1000, 1, false, Start);
        Assert.Equal(512, quality.Select(2000, [Slow], "one", Start.AddSeconds(5)));
        Assert.Equal(512, quality.Select(1500, [Slow], "two", Start.AddSeconds(6)));
        Assert.Equal(512, quality.Select(4000, [Slow], "two", Start.AddSeconds(7)));
        Assert.Equal(256, quality.Select(256, [Slow], "two", Start.AddSeconds(8)));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(200001, 1)]
    [InlineData(1000, -1)]
    [InlineData(1000, 121)]
    [InlineData(1000, double.NaN)]
    [InlineData(1000, double.PositiveInfinity)]
    public void InvalidReportsAreRejectedWithoutChangingTheBudget(int bandwidth, double buffer)
    {
        var quality = new GuestQuality();
        Assert.Throws<PartyException>(() => quality.Report(Slow, "stream", bandwidth, buffer, false, Start));
        Assert.Equal(2000, quality.Select(2000, [Slow], "stream", Start.AddSeconds(60)));
    }
}
