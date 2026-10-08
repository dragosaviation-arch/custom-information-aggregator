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
    public void ReleaseCompatibilityDeclarationMatchesTheDeploymentTarget()
    {
        var compatibilityDeclaration = ReadRepositoryFile(
            "docs",
            "architecture",
            "STEP-004-architecture-baseline.md");
        var publishProject = ReadRepositoryFile("CIA.Publish.proj");
        var installerProject = ReadRepositoryFile("CIA.Installer.proj");

        StringAssert.Contains(
            compatibilityDeclaration,
            "Windows 11 x64 is the primary supported baseline.");
        StringAssert.Contains(
            compatibilityDeclaration,
            "Windows 10 x64 remains an explicit compatibility-tested target");
        StringAssert.Contains(
            compatibilityDeclaration,
            "Publish target: self-contained **win-x64**");
        StringAssert.Contains(publishProject, "RuntimeIdentifier=win-x64");
        StringAssert.Contains(publishProject, "SelfContained=true");
        StringAssert.Contains(
            installerProject,
            "artifacts\\publish\\win-x64");
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
            "!define CIA_START_MENU_DIRECTORY \"$SMPROGRAMS\\Custom Information Aggregator\"");
        StringAssert.Contains(
            installer,
            "CreateShortcut \"${CIA_START_MENU_DIRECTORY}\\Custom Information Aggregator.lnk\" \"$INSTDIR\\CIA.exe\"");
        StringAssert.Contains(
            installer,
            "Section /o \"Desktop shortcut\" SecDesktopShortcut");
        StringAssert.Contains(
            installer,
            "!define CIA_DESKTOP_SHORTCUT \"$DESKTOP\\Custom Information Aggregator.lnk\"");
        StringAssert.Contains(
            installer,
            "CreateShortcut \"${CIA_DESKTOP_SHORTCUT}\" \"$INSTDIR\\CIA.exe\"");
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
            "!define CIA_CLASSES_KEY \"Software\\Classes\"");
        StringAssert.Contains(
            installer,
            "WriteRegStr HKCU \"${CIA_CLASSES_KEY}\\.cia\" \"\" \"CIA.WorkingState\"");
        StringAssert.Contains(
            installer,
            "WriteRegStr HKCU \"${CIA_CLASSES_KEY}\\CIA.WorkingState\\shell\\open\\command\"");
        StringAssert.Contains(installer, "$INSTDIR\\CIA.exe");
        StringAssert.Contains(installer, "$\\\"%1$\\\"");
        Assert.IsTrue(registryLines.All(
            line => line.Contains("HKCU", StringComparison.Ordinal)));
        Assert.IsFalse(registryLines.Any(
            line => line.Contains("CIA.ProcessingHost", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void InstallerSupportsUpgradeAndPerUserWindowsUninstallRegistration()
    {
        var installer = ReadRepositoryFile("deployment", "nsis", "CIA.Installer.nsi");
        var replacementIndex = installer.IndexOf(
            "RMDir /r \"$INSTDIR\"",
            StringComparison.Ordinal);
        var publicationIndex = installer.IndexOf(
            "File /r \"${CIA_PUBLISH_DIR}\\*\"",
            StringComparison.Ordinal);

        Assert.IsGreaterThanOrEqualTo(0, replacementIndex);
        Assert.IsGreaterThan(replacementIndex, publicationIndex);
        StringAssert.Contains(installer, "WriteUninstaller \"$INSTDIR\\Uninstall CIA.exe\"");
        StringAssert.Contains(
            installer,
            "!define CIA_UNINSTALL_KEY \"Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Custom Information Aggregator\"");
        StringAssert.Contains(installer, "WriteRegStr HKCU \"${CIA_UNINSTALL_KEY}\" \"DisplayName\"");
        StringAssert.Contains(installer, "WriteRegStr HKCU \"${CIA_UNINSTALL_KEY}\" \"DisplayVersion\"");
        StringAssert.Contains(installer, "WriteRegStr HKCU \"${CIA_UNINSTALL_KEY}\" \"UninstallString\"");
        StringAssert.Contains(installer, "WriteRegStr HKCU \"${CIA_UNINSTALL_KEY}\" \"InstallLocation\"");
        StringAssert.Contains(installer, "WriteRegDWORD HKCU \"${CIA_UNINSTALL_KEY}\" \"NoModify\" 1");
        StringAssert.Contains(installer, "WriteRegDWORD HKCU \"${CIA_UNINSTALL_KEY}\" \"NoRepair\" 1");
    }

    [TestMethod]
    public void UninstallerRemovesInstalledApplicationAndShellIntegration()
    {
        var installer = ReadRepositoryFile("deployment", "nsis", "CIA.Installer.nsi");

        StringAssert.Contains(installer, "Section \"Uninstall\"");
        StringAssert.Contains(
            installer,
            "Delete \"${CIA_START_MENU_DIRECTORY}\\Custom Information Aggregator.lnk\"");
        StringAssert.Contains(installer, "RMDir \"${CIA_START_MENU_DIRECTORY}\"");
        StringAssert.Contains(installer, "Delete \"${CIA_DESKTOP_SHORTCUT}\"");
        StringAssert.Contains(installer, "DeleteRegKey HKCU \"${CIA_CLASSES_KEY}\\CIA.WorkingState\"");
        StringAssert.Contains(installer, "DeleteRegKey HKCU \"${CIA_UNINSTALL_KEY}\"");
        Assert.HasCount(
            2,
            installer
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.Contains(
                    "RMDir /r \"$INSTDIR\"",
                    StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ManagedDataRemovalIsExplicitDefaultOffAndBoundedToApprovedRoot()
    {
        var installer = ReadRepositoryFile("deployment", "nsis", "CIA.Installer.nsi");
        var managedDataRemovalLines = installer
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains(
                "RMDir /r \"${CIA_MANAGED_DATA_DIRECTORY}\"",
                StringComparison.Ordinal))
            .ToArray();

        StringAssert.Contains(
            installer,
            "!define CIA_MANAGED_DATA_DIRECTORY \"$LOCALAPPDATA\\Custom Information Aggregator\"");
        StringAssert.Contains(installer, "StrCpy $RemoveManagedData ${BST_UNCHECKED}");
        StringAssert.Contains(installer, "Remove CIA managed application data");
        StringAssert.Contains(installer, "${GetOptions} $0 \"/RemoveManagedData\" $1");
        StringAssert.Contains(installer, "${If} $RemoveManagedData == ${BST_CHECKED}");
        Assert.HasCount(1, managedDataRemovalLines);
        Assert.IsFalse(installer.Contains("$PROFILE", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(installer.Contains("$DOCUMENTS", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(installer.Contains("*.cia", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(installer.Contains("PersistentArchiveExtraction", StringComparison.Ordinal));
        Assert.IsFalse(installer.Contains("LastUsedOutputDirectory", StringComparison.Ordinal));
    }

    [TestMethod]
    public void InstallerIntroducesNoAdminServiceStartupOrBackgroundBehavior()
    {
        var installer = ReadRepositoryFile("deployment", "nsis", "CIA.Installer.nsi");
        var desktopProject = ReadRepositoryFile("src", "CIA.Desktop", "CIA.Desktop.csproj");
        var hostProject = ReadRepositoryFile(
            "src",
            "CIA.ProcessingHost",
            "CIA.ProcessingHost.csproj");

        Assert.IsFalse(installer.Contains("$APPDATA", StringComparison.OrdinalIgnoreCase));
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
