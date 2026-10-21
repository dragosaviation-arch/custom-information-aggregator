using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CIA.Desktop.Tests;

[TestClass]
public sealed class LicensingIntegrationTests
{
    [TestMethod]
    public void ForkingGuideTechnicalPathsAndReleaseDocumentsResolve()
    {
        var root = FindRepositoryRoot();
        var forkingPath = Path.Combine(root, "FORKING.md");
        var forking = File.ReadAllText(forkingPath);
        var checkedPaths = 0;

        foreach (Match match in Regex.Matches(forking, @"!?(?:\[[^\]]*\])\((?<target>[^)]+)\)"))
        {
            var target = match.Groups["target"].Value;
            if (target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || target.StartsWith('#'))
            {
                continue;
            }

            var path = target.Split('#', 2)[0].Replace('/', Path.DirectorySeparatorChar);
            Assert.IsTrue(
                File.Exists(Path.Combine(root, path)),
                $"FORKING.md references missing repository path '{target}'.");
            checkedPaths++;
        }

        Assert.IsGreaterThanOrEqualTo(12, checkedPaths);
        Assert.IsTrue(File.Exists(Path.Combine(root, "LICENSE")));
        Assert.IsTrue(File.Exists(Path.Combine(root, "THIRD_PARTY_NOTICES.md")));
        Assert.IsTrue(File.Exists(Path.Combine(root, "docs", "licensing", "README.md")));
    }

    [TestMethod]
    public void AallIdentityAndReadmeEndorsementArePopulatedWithoutSignaturePlaceholders()
    {
        var root = FindRepositoryRoot();
        var license = File.ReadAllText(Path.Combine(root, "LICENSE"));
        var readme = File.ReadAllText(Path.Combine(root, "README.md"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        StringAssert.StartsWith(license, "Copyright (c) 2026 by");
        StringAssert.Contains(license, "Dragos Tone Project Manager * https://github.com/dragosaviation-arch");
        StringAssert.Contains(license, "Name (“Dragos Tone”)");
        StringAssert.Contains(license, "Professional identification (“Project Manager”)");
        StringAssert.Contains(license, "URL (“https://github.com/dragosaviation-arch”)");
        Assert.IsFalse(license.Contains("PROMOTIONAL SLOGAN", StringComparison.Ordinal));
        Assert.IsFalse(license.Contains("(“AUTHOR”)", StringComparison.Ordinal));
        Assert.IsFalse(license.Contains("(“PROFESSIONAL IDENTIFICATION”)", StringComparison.Ordinal));
        StringAssert.Contains(
            readme,
            "## Fork endorsement\n\n"
            + "I welcome and endorse independent CIA forks that comply with applicable laws and ethical standards. "
            + "You have my permission to reference this endorsement without contacting me. "
            + "It does not imply that I've personally reviewed, certified or contributed to your fork.\n\n"
            + "— Dragos Tone\n\n"
            + "[How to fork and personalize CIA](FORKING.md)");
    }

    [TestMethod]
    public void EveryDirectRuntimePackageIsRepresentedInThirdPartyNotices()
    {
        var root = FindRepositoryRoot();
        var notices = File.ReadAllText(Path.Combine(root, "THIRD_PARTY_NOTICES.md"));
        var packageNames = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Select(XDocument.Load)
            .SelectMany(document => document.Descendants("PackageReference"))
            .Select(reference => reference.Attribute("Include")?.Value)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.IsNotEmpty(packageNames);
        foreach (var packageName in packageNames)
        {
            StringAssert.Contains(
                notices,
                packageName!,
                $"Third-party notices do not identify direct package '{packageName}'.");
        }
    }

    [TestMethod]
    public void PublicationRequiresAndCopiesSignedLicensePackage()
    {
        var root = FindRepositoryRoot();
        var publishProject = File.ReadAllText(Path.Combine(root, "CIA.Publish.proj"));
        var installerProject = File.ReadAllText(Path.Combine(root, "CIA.Installer.proj"));
        var nsis = File.ReadAllText(Path.Combine(root, "deployment", "nsis", "CIA.Installer.nsi"));

        StringAssert.Contains(publishProject, "_ValidatePublicationLicensing");
        StringAssert.Contains(publishProject, "LICENSE.asc");
        StringAssert.Contains(publishProject, "AAL-PUBLIC-KEY.asc");
        StringAssert.Contains(publishProject, "THIRD_PARTY_NOTICES.md");
        StringAssert.Contains(publishProject, "licenses\\third-party");
        StringAssert.Contains(
            publishProject,
            "DependsOnTargets=\"_ValidatePublicationLicensing;_PreparePublishDirectory;_RestoreApplications\"");
        StringAssert.Contains(installerProject, "CIA.Publish.proj");
        StringAssert.Contains(nsis, "File /r \"${CIA_PUBLISH_DIR}\\*\"");
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "CIA.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}
