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
        StringAssert.Contains(installer, "!insertmacro MUI_PAGE_COMPONENTS");
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
    public void InstallerCreatesRequiredStartMenuAndOptionalDesktopShortcuts()
    {
        var installer = ReadRepositoryFile("deployment", "nsis", "CIA.Installer.nsi");
        var shortcutLines = installer
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains("CreateShortcut", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        StringAssert.Contains(
            installer,
            "CreateDirectory \"$SMPROGRAMS\\Custom Information Aggregator\"");
        StringAssert.Contains(
            installer,
            "CreateShortcut \"$SMPROGRAMS\\Custom Information Aggregator\\Custom Information Aggregator.lnk\" \"$INSTDIR\\CIA.exe\"");
        StringAssert.Contains(
            installer,
            "Section /o \"Desktop shortcut\" SecDesktopShortcut");
        StringAssert.Contains(
            installer,
            "CreateShortcut \"$DESKTOP\\Custom Information Aggregator.lnk\" \"$INSTDIR\\CIA.exe\"");
        Assert.HasCount(2, shortcutLines);
        Assert.IsFalse(shortcutLines.Any(
            line => line.Contains("CIA.ProcessingHost", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void InstallerRegistersCiaAssociationPerUserThroughDesktopExecutable()
    {
        var installer = ReadRepositoryFile("deployment", "nsis", "CIA.Installer.nsi");
        var registryLines = installer
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains("WriteReg", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        StringAssert.Contains(installer, "SetRegView 64");
        StringAssert.Contains(
            installer,
            "WriteRegStr HKCU \"Software\\Classes\\.cia\" \"\" \"CIA.WorkingState\"");
        StringAssert.Contains(
            installer,
            "WriteRegStr HKCU \"Software\\Classes\\CIA.WorkingState\\shell\\open\\command\"");
        StringAssert.Contains(installer, "$INSTDIR\\CIA.exe");
        StringAssert.Contains(installer, "$\\\"%1$\\\"");
        Assert.IsTrue(registryLines.All(
            line => line.Contains("HKCU", StringComparison.Ordinal)));
        Assert.IsFalse(registryLines.Any(
            line => line.Contains("CIA.ProcessingHost", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void InstallerIntroducesNoAdminServiceStartupOrMutableDataHandling()
    {
        var installer = ReadRepositoryFile("deployment", "nsis", "CIA.Installer.nsi");
        var desktopProject = ReadRepositoryFile("src", "CIA.Desktop", "CIA.Desktop.csproj");
        var hostProject = ReadRepositoryFile(
            "src",
            "CIA.ProcessingHost",
            "CIA.ProcessingHost.csproj");

        Assert.IsFalse(installer.Contains("WriteUninstaller", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(installer.Contains("$APPDATA", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(installer.Contains(
            "$LOCALAPPDATA\\Custom Information Aggregator",
            StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(installer.Contains("ExecWait", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(installer.Contains("nsExec", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(installer.Contains("HKLM", StringComparison.Ordinal));
        Assert.IsFalse(installer.Contains("CreateService", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(installer.Contains("$SMSTARTUP", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(installer.Contains("CurrentVersion\\Run", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(installer.Contains("RequestExecutionLevel admin", StringComparison.Ordinal));
        Assert.IsFalse(installer.Contains("RequestExecutionLevel highest", StringComparison.Ordinal));
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
