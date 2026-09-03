using CIA.Core;
using CIA.Desktop.Presentation;

namespace CIA.Desktop.Tests;

[TestClass]
public sealed class WorkspaceShellTests
{
    [TestMethod]
    public void ShellExposesEveryApprovedPrincipalWorkspace()
    {
        var shell = CreateShell();

        string[] expectedWorkspaceTitles =
        [
            "Load",
            "Discover",
            "Database",
            "Extraction / Review / Export",
            "Activity / Diagnostics",
            "Settings / Maintenance"
        ];

        CollectionAssert.AreEqual(
            expectedWorkspaceTitles,
            shell.Workspaces.Select(workspace => workspace.Title).ToArray());
    }

    [TestMethod]
    public void SelectingPrincipalWorkspacesPreservesTheApplicationSession()
    {
        var session = new ApplicationSession();
        var shell = new MainWindowViewModel(session);

        foreach (var workspace in shell.Workspaces)
        {
            shell.SelectedWorkspace = workspace;
            Assert.AreSame(session, shell.Session);
        }
    }

    [TestMethod]
    public void PrincipalWorkspacesCanBeSelectedInAnyOrder()
    {
        var shell = CreateShell();
        var settings = shell.Workspaces.Single(workspace => workspace.Area == WorkspaceArea.SettingsMaintenance);
        var discover = shell.Workspaces.Single(workspace => workspace.Area == WorkspaceArea.Discover);
        var load = shell.Workspaces.Single(workspace => workspace.Area == WorkspaceArea.Load);

        shell.SelectedWorkspace = settings;
        Assert.AreSame(settings, shell.SelectedWorkspace);

        shell.SelectedWorkspace = discover;
        Assert.AreSame(discover, shell.SelectedWorkspace);

        shell.SelectedWorkspace = load;
        Assert.AreSame(load, shell.SelectedWorkspace);
    }

    private static MainWindowViewModel CreateShell()
    {
        return new MainWindowViewModel(new ApplicationSession());
    }
}
