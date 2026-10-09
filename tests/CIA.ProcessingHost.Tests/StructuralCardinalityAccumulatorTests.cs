using CIA.Contracts.Sources;
using CIA.Core.Sources;
using CIA.ProcessingHost.Discovery;

namespace CIA.ProcessingHost.Tests;

[TestClass]
public sealed class StructuralCardinalityAccumulatorTests
{
    [TestMethod]
    public void RepetitionIsMeasuredBeneathOneParentRatherThanAcrossSources()
    {
        var sourceSetId = SourceSetId.CreateNew();
        var accumulator = new StructuralCardinalityAccumulator();

        accumulator.Add(sourceSetId, CreateRepeatedRecords(2));
        accumulator.Add(sourceSetId, CreateRepeatedRecords(1));

        var repeated = accumulator.GetStatistics(sourceSetId).Single(statistic =>
            statistic.ChildStructuralPath == "/root/records/record");
        Assert.AreEqual(2, repeated.MaximumChildInstancesPerParent);
        Assert.AreEqual(2, repeated.ObservedParentInstanceCount);
        Assert.AreEqual(1, repeated.RepeatingParentInstanceCount);
        Assert.AreEqual(2, repeated.ObservedSourceCount);

        var scalar = accumulator.GetStatistics(sourceSetId).Single(statistic =>
            statistic.ChildStructuralPath == "/root/records/record/value");
        Assert.AreEqual(1, scalar.MaximumChildInstancesPerParent);
        Assert.AreEqual(3, scalar.ObservedParentInstanceCount);
        Assert.AreEqual(0, scalar.RepeatingParentInstanceCount);
        Assert.AreEqual(2, scalar.ObservedSourceCount);
    }

    [TestMethod]
    public void RetainedMetadataIsBoundedByStructuralPathsNotOccurrenceVolume()
    {
        var small = CreateAccumulator(10);
        var large = CreateAccumulator(10_000);

        Assert.AreEqual(small.RetainedRootPathCount, large.RetainedRootPathCount);
        Assert.AreEqual(
            small.RetainedCardinalityStatisticCount,
            large.RetainedCardinalityStatisticCount);
        Assert.AreEqual(1, large.RetainedRootPathCount);
        Assert.AreEqual(3, large.RetainedCardinalityStatisticCount);
        Assert.AreEqual(
            10_000,
            large.GetStatistics(SourceSetIdForScale).Single(statistic =>
                statistic.ChildStructuralPath == "/root/records/record/value")
                .ObservedParentInstanceCount);
    }

    private static readonly SourceSetId SourceSetIdForScale = SourceSetId.CreateNew();

    private static StructuralCardinalityAccumulator CreateAccumulator(int recordCount)
    {
        var accumulator = new StructuralCardinalityAccumulator();
        accumulator.Add(SourceSetIdForScale, CreateRepeatedRecords(recordCount));
        return accumulator;
    }

    private static InterpretedSourceDocument CreateRepeatedRecords(int recordCount)
    {
        var sourceId = SourceId.CreateNew();
        var values = new InterpretedSourceValue[recordCount];
        for (var index = 0; index < recordCount; index++)
        {
            var recordInstance = checked(3L + index * 2L);
            var valueInstance = recordInstance + 1;
            values[index] = new InterpretedSourceValue(
                "value",
                index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                new SourceValueLineage(
                    sourceId,
                    [
                        new SourceElementInstance("root", "", "root", 1),
                        new SourceElementInstance("records", "", "records", 2),
                        new SourceElementInstance("record", "", "record", recordInstance),
                        new SourceElementInstance("value", "", "value", valueInstance)
                    ],
                    traversalOrder: index + 1));
        }

        return new InterpretedSourceDocument(sourceId, "synthetic.cardinality.v1", values);
    }
}
