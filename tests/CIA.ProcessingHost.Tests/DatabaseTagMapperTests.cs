using CIA.Contracts.Database;
using CIA.Contracts.Discovery;
using CIA.Contracts.Sources;
using CIA.Core.Database;
using CIA.ProcessingHost.Repository;

namespace CIA.ProcessingHost.Tests;

[TestClass]
public sealed class DatabaseTagMapperTests
{
    [TestMethod]
    public void SelectedTagsProduceDeterministicDefaultOverrideAndSharedNameColumns()
    {
        var firstConfiguration = CreateConfiguration(
            ("qty", DiscoveryInformationDisposition.Neutral),
            ("toolnbr", DiscoveryInformationDisposition.Selected),
            ("descr", DiscoveryInformationDisposition.Selected),
            ("partname", DiscoveryInformationDisposition.Selected));
        var secondConfiguration = CreateConfiguration(
            ("partname", DiscoveryInformationDisposition.Selected),
            ("descr", DiscoveryInformationDisposition.Selected),
            ("toolnbr", DiscoveryInformationDisposition.Selected),
            ("qty", DiscoveryInformationDisposition.Neutral));
        var overrides = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["descr"] = "Description",
            ["partname"] = "Description"
        };

        var first = DatabaseTagMapper.CreateMapping(firstConfiguration, overrides);
        var second = DatabaseTagMapper.CreateMapping(secondConfiguration, overrides);

        Assert.HasCount(2, first.Columns);
        Assert.AreEqual("Description", first.Columns[0].DatabaseTagName);
        CollectionAssert.AreEqual(
            new[] { "descr", "partname" },
            first.Columns[0].SourceInformationTypes.ToArray());
        Assert.AreEqual("toolnbr", first.Columns[1].DatabaseTagName);
        CollectionAssert.AreEqual(
            new[] { "toolnbr" },
            first.Columns[1].SourceInformationTypes.ToArray());
        Assert.IsFalse(first.Columns
            .SelectMany(column => column.SourceInformationTypes)
            .Contains("qty", StringComparer.Ordinal));
        CollectionAssert.AreEqual(
            first.Columns.Select(DescribeColumn).ToArray(),
            second.Columns.Select(DescribeColumn).ToArray());
    }

    [TestMethod]
    public void MappingPreservesExactValueSourceIdentityAndProvenanceWithoutRowSemantics()
    {
        const string exactValue = "  Mixed CASE <content> Åß中  ";
        var sourceId = SourceId.CreateNew();
        var occurrence = new IndexedOccurrence("descr", exactValue, sourceId);
        var mapping = DatabaseTagMapper.CreateMapping(
            CreateConfiguration(("descr", DiscoveryInformationDisposition.Selected)),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["descr"] = "Description"
            });

        var mapped = DatabaseTagMapper.MapValue(
            mapping,
            occurrence.Tag,
            occurrence.Value,
            occurrence.SourceId);

        Assert.IsNotNull(mapped);
        Assert.AreEqual("Description", mapped.DatabaseTagName);
        Assert.AreEqual(occurrence.Tag, mapped.SourceInformationType);
        Assert.AreEqual(exactValue, mapped.Value);
        Assert.AreEqual(sourceId, mapped.SourceId);
        Assert.IsNull(typeof(MappedDatabaseValue).GetProperty("RowId"));
        Assert.IsNull(typeof(MappedDatabaseValue).GetProperty("Relationship"));
        Assert.IsNull(typeof(MappedDatabaseValue).GetProperty("JoinKey"));

        var unselected = DatabaseTagMapper.MapValue(
            mapping,
            "qty",
            "7",
            SourceId.CreateNew());
        Assert.IsNull(unselected);
    }

    private static DiscoveryConfigurationSnapshot CreateConfiguration(
        params (string InformationType, DiscoveryInformationDisposition Disposition)[] items)
    {
        return new DiscoveryConfigurationSnapshot(
            items
                .Select(item => new DiscoveryConfigurationItem(
                    item.InformationType,
                    item.Disposition))
                .ToArray());
    }

    private static string DescribeColumn(DatabaseColumnMapping column)
    {
        return $"{column.DatabaseTagName}:{string.Join(',', column.SourceInformationTypes)}";
    }
}
