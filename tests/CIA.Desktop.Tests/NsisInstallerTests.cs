using System.Xml.Linq;

namespace CIA.Desktop.Tests;

[TestClass]
public sealed class NsisInstallerTests
{
    [TestMethod]
    public void InstallerIsGraphicalPerUserAndUsesTheApprovedBinaryLocation()
    {
        var installer = ReadRepositoryFile("deployment", "nsis", "CIA.Installer.nsi");

        StringAssert.Contains(installer, "!include \"MUI2.nsh\"");
        StringAssert.Contains(installer, "!insertmacro MUI_PAGE_WELCOME");
        StringAssert.Contains(installer, "!insertmacro MUI_PAGE_INSTFILES");
        StringAssert.Contains(installer, "!insertmacro MUI_PAGE_FINISH");
        StringAssert.Contains(installer, "RequestExecutionLevel user");
        StringAssert.Contains(
            installer,
            "InstallDir \"$LOCALAPPDATA\\Programs\\Custom Information Aggregator\"");
        StringAssert.Contains(installer, "SetShellVarContext current");
        StringAssert.Contains(installer, "File /r \"${CIA_PUBLISH_DIR}\\*\"");
        StringAssert.Contains(installer, "MUI_FINISHPAGE_RUN \"$INSTDIR\\CIA.exe\"");
        Assert.IsFalse(installer.Contains("RequestExecutionLevel admin", StringComparison.Ordinal));
        Assert.IsFalse(installer.Contains("RequestExecutionLevel highest", StringComparison.Ordinal));
        Assert.IsFalse(installer.Contains("SetShellVarContext all", StringComparison.Ordinal));
        Assert.IsFalse(installer.Contains("HKLM", StringComparison.Ordinal));
    }

    [TestMethod]
    public void InstallerConsumesTheExistingSelfContainedPublication()
    {
        var installerProject = XDocument.Parse(ReadRepositoryFile("CIA.Installer.proj"));
        var projectText = installerProject.ToString(SaveOptions.DisableFormatting);
        var publishProject = ReadRepositoryFile("CIA.Publish.proj");

        StringAssert.Contains(projectText, "CIA.Publish.proj");
        StringAssert.Contains(projectText, "Targets=\"Publish\"");
        StringAssert.Contains(projectText, "CIA.exe");
        StringAssert.Contains(projectText, "CIA.ProcessingHost.exe");
        StringAssert.Contains(projectText, "makensis.exe");
        StringAssert.Contains(publishProject, "RuntimeIdentifier=win-x64");
        StringAssert.Contains(publishProject, "SelfContained=true");
        StringAssert.Contains(publishProject, "PublishSingleFile=false");
        StringAssert.Contains(publishProject, "CIA.ProcessingHost.exe");
    }

    [TestMethod]
    public void InstallerIntroducesNoShellIntegrationOrMutableDataHandling()
    {
        var installer = ReadRepositoryFile("deployment", "nsis", "CIA.Installer.nsi");
        var desktopProject = ReadRepositoryFile("src", "CIA.Desktop", "CIA.Desktop.csproj");
        var hostProject = ReadRepositoryFile(
            "src",
            "CIA.ProcessingHost",
            "CIA.ProcessingHost.csproj");

        Assert.IsFalse(installer.Contains("CreateShortCut", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(installer.Contains("WriteReg", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(installer.Contains("WriteUninstaller", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(installer.Contains("$APPDATA", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(installer.Contains(
            "$LOCALAPPDATA\\Custom Information Aggregator",
            StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(installer.Contains("ExecWait", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(installer.Contains("nsExec", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(desktopProject, "<OutputType>WinExe</OutputType>");
        StringAssert.Contains(hostProject, "<OutputType>WinExe</OutputType>");
    }

    private static string ReadRepositoryFile(params string[] segments) =>
        File.ReadAllText(Path.Combine([FindRepositoryRoot(), .. segments]));

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
