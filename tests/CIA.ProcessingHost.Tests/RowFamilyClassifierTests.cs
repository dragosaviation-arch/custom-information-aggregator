using System.Text.Json;
using CIA.Contracts.Discovery;
using CIA.Contracts.Hierarchy;
using CIA.Contracts.Sources;
using CIA.Core.Hierarchy;

namespace CIA.ProcessingHost.Tests;

[TestClass]
public sealed class RowFamilyClassifierTests
{
    private static readonly SourceSetId SourceSetId = CIA.Contracts.Sources.SourceSetId.From(
        Guid.Parse("0199c9f4-0d9b-7dcb-8d2f-8b8edcc5c201"));

    [TestMethod]
    public void RowFamilyIdentityIsStableOrderedNamespaceAwareAndSerializable()
    {
        var root = new RowFamilyId(SourceSetId, RowFamilyKind.Root, "/{urn:catalog/v1}book");
        var repeated = new RowFamilyId(
            SourceSetId,
            RowFamilyKind.Repeated,
            "/{urn:catalog/v1}book/{urn:catalog/v1}authors/{urn:catalog/v1}author");
        var same = new RowFamilyId(SourceSetId, RowFamilyKind.Root, "/{urn:catalog/v1}book");
        var otherNamespace = new RowFamilyId(
            SourceSetId,
            RowFamilyKind.Root,
            "/{urn:catalog/v2}book");

        Assert.AreEqual(root, same);
        Assert.AreEqual(root.GetHashCode(), same.GetHashCode());
        Assert.IsLessThan(0, root.CompareTo(repeated));
        Assert.AreNotEqual(root, otherNamespace);
        Assert.AreEqual(root, JsonSerializer.Deserialize<RowFamilyId>(JsonSerializer.Serialize(root)));
        Assert.AreEqual(
            "/{urn:catalog/v1}book",
            JsonSerializer.Deserialize<RowFamilyId>(JsonSerializer.Serialize(root)).AnchorPath);
    }

    [TestMethod]
    public void SingletonContainersAndSameLeafDifferentAncestryRemainInRootFamily()
    {
        var identities = new[]
        {
            Identity("/aircraft/buyer/name", "name"),
            Identity("/aircraft/seller/name", "name"),
            Identity("/aircraft/details/wrapper/code", "code")
        };
        var result = Classify(
            ["/aircraft"],
            [
                Statistic("/aircraft", "/aircraft", "/aircraft/buyer", 1),
                Statistic("/aircraft", "/aircraft", "/aircraft/seller", 1),
                Statistic("/aircraft", "/aircraft/details", "/aircraft/details/wrapper", 1)
            ],
            identities);

        Assert.HasCount(1, result.Families);
        Assert.AreEqual(RowFamilyKind.Root, result.Families[0].Id.Kind);
        Assert.AreEqual("/aircraft", result.Families[0].Id.AnchorPath);
        Assert.HasCount(3, result.FieldAssignments);
        Assert.IsTrue(result.FieldAssignments.All(
            assignment => assignment.RowFamilyId == result.Families[0].Id));
        Assert.AreNotEqual(
            result.FieldAssignments[0].Identity,
            result.FieldAssignments[1].Identity);
    }

    [TestMethod]
    public void IndependentAndNestedRepeatedBranchesUseNearestRepeatedParent()
    {
        var result = Classify(
            ["/catalog"],
            [
                Statistic("/catalog", "/catalog/groups", "/catalog/groups/group", 3),
                Statistic("/catalog", "/catalog/parts", "/catalog/parts/part", 2),
                Statistic(
                    "/catalog",
                    "/catalog/groups/group/components",
                    "/catalog/groups/group/components/component",
                    4),
                Statistic(
                    "/catalog",
                    "/catalog/groups/group/components/component",
                    "/catalog/groups/group/components/component/wrapper",
                    1)
            ],
            [
                Identity("/catalog/groups/group/name", "name"),
                Identity("/catalog/parts/part/name", "name"),
                Identity("/catalog/groups/group/components/component/wrapper/code", "code")
            ]);

        var root = Family(result, "/catalog");
        var groups = Family(result, "/catalog/groups/group");
        var parts = Family(result, "/catalog/parts/part");
        var components = Family(result, "/catalog/groups/group/components/component");

        Assert.AreNotEqual(groups.Id, parts.Id);
        Assert.AreEqual(root.Id, groups.ParentRowFamilyId);
        Assert.AreEqual(root.Id, parts.ParentRowFamilyId);
        Assert.AreEqual(groups.Id, components.ParentRowFamilyId);
        Assert.AreEqual(groups.Id, Assignment(result, "/catalog/groups/group/name"));
        Assert.AreEqual(parts.Id, Assignment(result, "/catalog/parts/part/name"));
        Assert.AreEqual(
            components.Id,
            Assignment(result, "/catalog/groups/group/components/component/wrapper/code"));
        Assert.IsFalse(result.Families.Any(
            family => family.Id.AnchorPath.EndsWith("/wrapper", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void NamespaceAwareRootsAndRepeatedPathsNeverCollide()
    {
        const string alphaRoot = "/{urn:alpha}root";
        const string betaRoot = "/{urn:beta}root";
        const string alphaItem = "/{urn:alpha}root/{urn:alpha}items/{urn:alpha}item";
        const string betaItem = "/{urn:beta}root/{urn:beta}items/{urn:beta}item";
        var result = Classify(
            [betaRoot, alphaRoot],
            [
                Statistic(alphaRoot, "/{urn:alpha}root/{urn:alpha}items", alphaItem, 2),
                Statistic(betaRoot, "/{urn:beta}root/{urn:beta}items", betaItem, 3)
            ],
            [
                Identity($"{alphaItem}/{{urn:alpha}}name", "a:name"),
                Identity($"{betaItem}/{{urn:beta}}name", "b:name")
            ]);

        Assert.HasCount(4, result.Families);
        Assert.AreNotEqual(Family(result, alphaRoot).Id, Family(result, betaRoot).Id);
        Assert.AreNotEqual(Family(result, alphaItem).Id, Family(result, betaItem).Id);
        Assert.AreEqual(Family(result, alphaItem).Id, Assignment(result, $"{alphaItem}/{{urn:alpha}}name"));
        Assert.AreEqual(Family(result, betaItem).Id, Assignment(result, $"{betaItem}/{{urn:beta}}name"));
    }

    [TestMethod]
    public void ClassificationAndCanonicalOrderDoNotDependOnInputOrder()
    {
        var roots = new[] { "/z-root", "/a-root" };
        var statistics = new[]
        {
            Statistic("/z-root", "/z-root/groups", "/z-root/groups/group", 2),
            Statistic("/z-root", "/z-root/groups/group/items", "/z-root/groups/group/items/item", 2),
            Statistic("/a-root", "/a-root/lines", "/a-root/lines/line", 2)
        };
        var identities = new[]
        {
            Identity("/z-root/groups/group/name", "name"),
            Identity("/z-root/groups/group/items/item/code", "code"),
            Identity("/a-root/lines/line/value", "value")
        };

        var first = Classify(roots, statistics, identities);
        var second = Classify(
            roots.Reverse(),
            statistics.Reverse(),
            identities.Reverse());

        CollectionAssert.AreEqual(FamilySignatures(first), FamilySignatures(second));
        CollectionAssert.AreEqual(AssignmentSignatures(first), AssignmentSignatures(second));
        CollectionAssert.AreEqual(
            new[]
            {
                "/a-root|Root",
                "/a-root/lines/line|Repeated",
                "/z-root|Root",
                "/z-root/groups/group|Repeated",
                "/z-root/groups/group/items/item|Repeated"
            },
            first.Families.Select(family => $"{family.Id.AnchorPath}|{family.Id.Kind}").ToArray());
    }

    [TestMethod]
    public void GoodbooksLikeCatalogUsesOnlyProvenRepeatedAnchors()
    {
        var statistics = GoodbooksStatistics(includeNestedSimilarBookAuthors: false);
        var identities = GoodbooksIdentities();

        var result = Classify(["/book"], statistics, identities);

        CollectionAssert.AreEqual(
            new[]
            {
                "/book",
                "/book/authors/author",
                "/book/buy_links/buy_link",
                "/book/series_works/series_work",
                "/book/similar_books/book"
            },
            result.Families.Select(family => family.Id.AnchorPath).ToArray());
        Assert.AreEqual(Family(result, "/book").Id, Assignment(result, "/book/title"));
        Assert.AreEqual(Family(result, "/book").Id, Assignment(result, "/book/num_pages"));
        Assert.AreEqual(
            Family(result, "/book").Id,
            Assignment(result, "/book/work/original_title"));
        Assert.AreEqual(
            Family(result, "/book/authors/author").Id,
            Assignment(result, "/book/authors/author/name"));
        Assert.AreEqual(
            Family(result, "/book/buy_links/buy_link").Id,
            Assignment(result, "/book/buy_links/buy_link/name"));
        Assert.AreEqual(
            Family(result, "/book/series_works/series_work").Id,
            Assignment(result, "/book/series_works/series_work/series/title"));
        Assert.AreEqual(
            Family(result, "/book/similar_books/book").Id,
            Assignment(result, "/book/similar_books/book/title"));
        Assert.AreEqual(
            Family(result, "/book/similar_books/book").Id,
            Assignment(result, "/book/similar_books/book/num_pages"));
        Assert.AreEqual(
            Family(result, "/book/similar_books/book").Id,
            Assignment(result, "/book/similar_books/book/title_without_series"));
        Assert.AreEqual(
            Family(result, "/book/similar_books/book").Id,
            Assignment(result, "/book/similar_books/book/authors/author/name"));
    }

    [TestMethod]
    public void GoodbooksLikeNestedAuthorBecomesFamilyOnlyWhenCardinalityProvesRepetition()
    {
        var result = Classify(
            ["/book"],
            GoodbooksStatistics(includeNestedSimilarBookAuthors: true),
            GoodbooksIdentities());

        var similarBooks = Family(result, "/book/similar_books/book");
        var nestedAuthors = Family(
            result,
            "/book/similar_books/book/authors/author");

        Assert.AreEqual(similarBooks.Id, nestedAuthors.ParentRowFamilyId);
        Assert.AreEqual(
            nestedAuthors.Id,
            Assignment(result, "/book/similar_books/book/authors/author/name"));
    }

    [TestMethod]
    public void GlobalOccurrencesAndSourceCountsDoNotProveRepetition()
    {
        var statistic = new StructuralCardinalityStatistic(
            SourceSetId,
            "/root",
            "/root/items",
            "/root/items/item",
            maximumChildInstancesPerParent: 1,
            observedParentInstanceCount: 50_000,
            repeatingParentInstanceCount: 0,
            observedSourceCount: 10_000);

        var result = Classify(
            ["/root"],
            [statistic],
            [Identity("/root/items/item/value", "value")]);

        Assert.HasCount(1, result.Families);
        Assert.AreEqual(RowFamilyKind.Root, result.Families[0].Id.Kind);
        Assert.AreEqual(result.Families[0].Id, result.FieldAssignments[0].RowFamilyId);
    }

    [TestMethod]
    public void EveryDetailedIdentityReceivesExactlyOneAssignment()
    {
        var identities = new[]
        {
            Identity("/root/value", "value"),
            Identity("/root/records/record/code", "code"),
            new DiscoveryInformationIdentity(
                SourceSetId,
                "/root/records/record",
                "id",
                SourceValueCandidateKind.Attribute,
                "/root/records/record/@id")
        };

        var result = Classify(
            ["/root"],
            [Statistic("/root", "/root/records", "/root/records/record", 2)],
            identities);

        Assert.HasCount(identities.Length, result.FieldAssignments);
        foreach (var identity in identities)
        {
            Assert.HasCount(
                1,
                result.FieldAssignments.Where(assignment => assignment.Identity == identity));
        }
    }

    private static RowFamilyClassificationResult Classify(
        IEnumerable<string> roots,
        IEnumerable<StructuralCardinalityStatistic> statistics,
        IEnumerable<DiscoveryInformationIdentity> identities)
    {
        return new RowFamilyClassifier().Classify(
            SourceSetId,
            roots,
            statistics,
            identities);
    }

    private static StructuralCardinalityStatistic Statistic(
        string root,
        string parent,
        string child,
        int maximumChildren)
    {
        return new StructuralCardinalityStatistic(
            SourceSetId,
            root,
            parent,
            child,
            maximumChildren,
            observedParentInstanceCount: 8,
            repeatingParentInstanceCount: maximumChildren > 1 ? 2 : 0,
            observedSourceCount: 3);
    }

    private static DiscoveryInformationIdentity Identity(string path, string informationType)
    {
        return new DiscoveryInformationIdentity(SourceSetId, path, informationType);
    }

    private static RowFamilyDefinition Family(
        RowFamilyClassificationResult result,
        string anchorPath)
    {
        return result.Families.Single(family => family.Id.AnchorPath == anchorPath);
    }

    private static RowFamilyId Assignment(
        RowFamilyClassificationResult result,
        string structuralPath)
    {
        return result.FieldAssignments.Single(
            assignment => assignment.Identity.StructuralPath == structuralPath).RowFamilyId;
    }

    private static string[] FamilySignatures(RowFamilyClassificationResult result)
    {
        return result.Families.Select(family =>
            $"{family.Id}|{family.ParentRowFamilyId?.ToString() ?? "root"}").ToArray();
    }

    private static string[] AssignmentSignatures(RowFamilyClassificationResult result)
    {
        return result.FieldAssignments.Select(assignment =>
            $"{assignment.Identity.StructuralPath}|{assignment.RowFamilyId}").ToArray();
    }

    private static IReadOnlyList<StructuralCardinalityStatistic> GoodbooksStatistics(
        bool includeNestedSimilarBookAuthors)
    {
        var statistics = new List<StructuralCardinalityStatistic>
        {
            Statistic("/book", "/book/authors", "/book/authors/author", 2),
            Statistic("/book", "/book/buy_links", "/book/buy_links/buy_link", 2),
            Statistic("/book", "/book/series_works", "/book/series_works/series_work", 2),
            Statistic("/book", "/book/similar_books", "/book/similar_books/book", 2),
            Statistic(
                "/book",
                "/book/similar_books/book/authors",
                "/book/similar_books/book/authors/author",
                includeNestedSimilarBookAuthors ? 2 : 1)
        };
        return statistics;
    }

    private static IReadOnlyList<DiscoveryInformationIdentity> GoodbooksIdentities()
    {
        return
        [
            Identity("/book/title", "title"),
            Identity("/book/num_pages", "num_pages"),
            Identity("/book/work/original_title", "original_title"),
            Identity("/book/authors/author/name", "name"),
            Identity("/book/buy_links/buy_link/name", "name"),
            Identity("/book/series_works/series_work/series/title", "title"),
            Identity("/book/similar_books/book/title", "title"),
            Identity("/book/similar_books/book/num_pages", "num_pages"),
            Identity(
                "/book/similar_books/book/title_without_series",
                "title_without_series"),
            Identity("/book/similar_books/book/authors/author/name", "name")
        ];
    }
}
