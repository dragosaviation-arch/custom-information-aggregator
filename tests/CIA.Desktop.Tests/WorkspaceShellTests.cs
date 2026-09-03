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
            "Discovery",
            "Database",
            "Settings"
        ];

        CollectionAssert.AreEqual(
            expectedWorkspaceTitles,
            shell.Workspaces.Select(workspace => workspace.Title).ToArray());
    }

    [TestMethod]
    public void WorkspaceAreaContainsOnlyApprovedTopLevelAreas()
    {
        string[] expectedWorkspaceAreas =
        [
            nameof(WorkspaceArea.Load),
            nameof(WorkspaceArea.Discovery),
            nameof(WorkspaceArea.Database),
            nameof(WorkspaceArea.Settings)
        ];

        CollectionAssert.AreEqual(expectedWorkspaceAreas, Enum.GetNames<WorkspaceArea>());
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
        var settings = shell.Workspaces.Single(workspace => workspace.Area == WorkspaceArea.Settings);
        var discovery = shell.Workspaces.Single(workspace => workspace.Area == WorkspaceArea.Discovery);
        var load = shell.Workspaces.Single(workspace => workspace.Area == WorkspaceArea.Load);
        var database = shell.Workspaces.Single(workspace => workspace.Area == WorkspaceArea.Database);

        shell.SelectedWorkspace = settings;
        Assert.AreSame(settings, shell.SelectedWorkspace);

        shell.SelectedWorkspace = discovery;
        Assert.AreSame(discovery, shell.SelectedWorkspace);

        shell.SelectedWorkspace = load;
        Assert.AreSame(load, shell.SelectedWorkspace);

        shell.SelectedWorkspace = database;
        Assert.AreSame(database, shell.SelectedWorkspace);
    }

    private static MainWindowViewModel CreateShell()
    {
        return new MainWindowViewModel(new ApplicationSession());
    }
}
