using System.Text.Json;

namespace CIA.Desktop.Tests;

[TestClass]
public sealed class UiReferenceReconciliationTests
{
    [TestMethod]
    public void LoadReferenceUsesCheckedEntriesForRemovalInsteadOfHighlightedRows()
    {
        var root = FindRepositoryRoot();
        var specText = File.ReadAllText(Path.Combine(
            root,
            "design",
            "ui-reference",
            "load",
            "CIA_Load_UI_Spec.json"));
        var html = File.ReadAllText(Path.Combine(
            root,
            "design",
            "ui-reference",
            "load",
            "CIA_Load_UI_Preview.html"));
        using var spec = JsonDocument.Parse(specText);
        var sourceList = spec.RootElement.GetProperty("sourceList");

        StringAssert.Contains(
            sourceList.GetProperty("removeBehavior").GetProperty("target").GetString()!,
            "checkbox is checked");
        StringAssert.Contains(
            sourceList.GetProperty("rowSelection").GetProperty("purpose").GetString()!,
            "Remove uses checked/included rows");
        StringAssert.Contains(html, "data.filter(x=>x.included)");
        StringAssert.Contains(html, "disabled=!data.some(v=>v.included)");
    }

    [TestMethod]
    public void DiscoveryReferenceAgreesOnSourceInspectionAndVisibleSortingState()
    {
        var root = FindRepositoryRoot();
        var specText = File.ReadAllText(Path.Combine(
            root,
            "design",
            "ui-reference",
            "discovery",
            "CIA_Discovery_UI_Spec.json"));
        var html = File.ReadAllText(Path.Combine(
            root,
            "design",
            "ui-reference",
            "discovery",
            "CIA_Discovery_UI_Preview.html"));
        using var spec = JsonDocument.Parse(specText);
        var tagList = spec.RootElement.GetProperty("leftDiscoveryArea");

        Assert.IsTrue(tagList.TryGetProperty("sourceInspection", out var sourceInspection));
        StringAssert.Contains(
            sourceInspection.GetProperty("presentation").GetString()!,
            "per-source occurrence count");
        Assert.IsTrue(tagList.TryGetProperty("sorting", out var sorting));
        StringAssert.Contains(sorting.GetProperty("visibleState").GetString()!, "arrow");
        StringAssert.Contains(html, "class=\"sourceinspect\"");
        StringAssert.Contains(html, "function sourceBreakdown");
        StringAssert.Contains(html, "class=\"sorthead\"");
        StringAssert.Contains(html, "aria-sort");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CIA.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("The repository root containing CIA.slnx was not found.");
    }
}
