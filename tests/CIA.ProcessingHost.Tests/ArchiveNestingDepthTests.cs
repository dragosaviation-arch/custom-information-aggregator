using CIA.Contracts.Sources;

namespace CIA.ProcessingHost.Tests;

[TestClass]
public sealed class ArchiveNestingDepthTests
{
    [TestMethod]
    public void DefaultAllowsLevelsOneThroughThreeAndStopsAtFour()
    {
        var depth = ArchiveNestingDepth.Default;

        Assert.AreEqual(3, depth.Value);
        Assert.IsTrue(depth.AllowsLevel(1));
        Assert.IsTrue(depth.AllowsLevel(2));
        Assert.IsTrue(depth.AllowsLevel(3));
        Assert.IsFalse(depth.AllowsLevel(4));
    }

    [TestMethod]
    public void ConfiguredMaximumControlsTheDeterministicLevelDecision()
    {
        var depth = ArchiveNestingDepth.From(5);

        Assert.IsTrue(depth.AllowsLevel(5));
        Assert.IsFalse(depth.AllowsLevel(6));
    }

    [TestMethod]
    public void InvalidMaximumAndArchiveLevelsAreRejected()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ArchiveNestingDepth.From(0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => ArchiveNestingDepth.Default.AllowsLevel(0));
        Assert.ThrowsExactly<InvalidOperationException>(
            () => default(ArchiveNestingDepth).AllowsLevel(1));
    }
}
