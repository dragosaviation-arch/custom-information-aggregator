using System.Windows;
using System.Windows.Shell;
using CIA.Core.Diagnostics;
using CIA.Core.ManagedStorage;
using CIA.Desktop.Hosting;
using CIA.Desktop.Presentation;
using CIA.Desktop.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CIA.Desktop.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ApplicationStartupSmokeTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(20);

    [TestMethod]
    public async Task DesktopResourcesAndPersistentShellComposeOnStaThread()
    {
        using var logs = new TemporaryStartupLogDirectory();
        Exception? failure = null;

        await WpfTestApplication.RunAsync(
            () => failure = RunDesktopComposition(logs.Path)).WaitAsync(TestTimeout);
        Assert.IsNull(failure, failure?.ToString());
    }

    private static Exception? RunDesktopComposition(string logDirectory)
    {
        IHost? host = null;
        MainWindow? window = null;
        Exception? failure = null;

        try
        {
            host = DesktopApplicationHost.Create(
                [$"--{ApplicationLogPaths.DirectoryConfigurationKey}={logDirectory}"]);
            host.StartAsync().GetAwaiter().GetResult();

            var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
            window = host.Services.GetRequiredService<MainWindow>();
            var viewModel = host.Services.GetRequiredService<MainWindowViewModel>();
            var globalStatus = host.Services.GetRequiredService<GlobalStatusViewModel>();
            var loadWorkspace = host.Services.GetRequiredService<LoadWorkspaceViewModel>();
            var discoveryWorkspace = host.Services.GetRequiredService<DiscoveryWorkspaceViewModel>();
            var databaseWorkspace = host.Services.GetRequiredService<DatabaseWorkspaceViewModel>();
            var settingsWorkspace = host.Services.GetRequiredService<SettingsWorkspaceViewModel>();
            var cleanupService = host.Services.GetRequiredService<IManagedStorageCleanupService>();
            Application.Current.MainWindow = window;
            window.ShowInTaskbar = false;
            window.ShowActivated = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -10000;
            window.Top = -10000;
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            window.Show();
            window.UpdateLayout();

            Assert.IsTrue(lifetime.ApplicationStarted.IsCancellationRequested);
            Assert.IsNotNull(window.Content);
            Assert.AreEqual(WindowStyle.None, window.WindowStyle);
            var windowChrome = WindowChrome.GetWindowChrome(window);
            Assert.IsNotNull(windowChrome);
            Assert.AreEqual(38d, windowChrome.CaptionHeight);
            Assert.AreEqual(new Thickness(6), windowChrome.ResizeBorderThickness);
            Assert.IsFalse(windowChrome.UseAeroCaptionButtons);
            Assert.AreSame(viewModel, window.DataContext);
            Assert.AreSame(globalStatus, window.GlobalStatus);
            Assert.AreSame(loadWorkspace, window.LoadWorkspace);
            Assert.AreSame(discoveryWorkspace, window.DiscoveryWorkspace);
            Assert.AreSame(databaseWorkspace, window.DatabaseWorkspace);
            Assert.AreSame(settingsWorkspace, window.SettingsWorkspace);
            Assert.IsNotNull(cleanupService);
            Assert.IsTrue(settingsWorkspace.CleanupManagedStorageCommand.CanExecute(null));
            Assert.HasCount(4, viewModel.Workspaces);
            Assert.AreEqual(WorkspaceArea.Load, viewModel.SelectedWorkspace.Area);
            var attributionSlot = (System.Windows.Controls.Grid)window.FindName(
                "AttributionProgressSlot");
            var attributionButton = (System.Windows.Controls.Button)window.FindName(
                "OriginalAuthorAttributionButton");
            var authorIdentityText = (System.Windows.Controls.TextBlock)window.FindName(
                "OriginalAuthorIdentityText");
            var authorUrlText = (System.Windows.Controls.TextBlock)window.FindName(
                "OriginalAuthorUrlText");
            var progressPanel = (System.Windows.Controls.Grid)window.FindName(
                "GlobalProgressPanel");
            Assert.AreEqual(200d, attributionSlot.Width);
            Assert.AreEqual(200d, attributionSlot.ActualWidth);
            Assert.IsTrue(globalStatus.IsOriginalAttributionVisible);
            Assert.IsTrue(attributionButton.IsTabStop);
            Assert.AreEqual(Visibility.Visible, attributionButton.Visibility);
            Assert.AreEqual(Visibility.Collapsed, progressPanel.Visibility);
            Assert.AreEqual(System.Windows.TextTrimming.None, authorIdentityText.TextTrimming);
            Assert.AreEqual(System.Windows.TextTrimming.None, authorUrlText.TextTrimming);
            Assert.AreEqual(12d, authorIdentityText.FontSize);
            Assert.AreEqual(11d, authorUrlText.FontSize);
            Assert.IsGreaterThan(0, authorIdentityText.ActualWidth);
            Assert.IsLessThanOrEqualTo(attributionSlot.ActualWidth, authorIdentityText.ActualWidth);
            Assert.IsGreaterThan(0, authorUrlText.ActualWidth);
            Assert.IsLessThanOrEqualTo(attributionSlot.ActualWidth, authorUrlText.ActualWidth);
            StringAssert.Contains(
                System.Windows.Automation.AutomationProperties.GetName(attributionButton),
                "Dragos Tone · Project Manager");

            var databaseView = (DatabaseWorkspaceView)window.FindName("DatabaseWorkspaceView");
            var placeholder = (System.Windows.Controls.Grid)window.FindName(
                "WorkspacePlaceholder");
            var settingsView = (SettingsWorkspaceView)window.FindName("SettingsWorkspaceView");
            viewModel.SelectedWorkspace = viewModel.Workspaces.Single(
                workspace => workspace.Area == WorkspaceArea.Database);
            window.UpdateLayout();
            Assert.AreEqual(Visibility.Visible, databaseView.Visibility);
            Assert.AreEqual(Visibility.Collapsed, placeholder.Visibility);

            viewModel.SelectedWorkspace = viewModel.Workspaces.Single(
                workspace => workspace.Area == WorkspaceArea.Settings);
            window.UpdateLayout();
            Assert.AreEqual(Visibility.Collapsed, databaseView.Visibility);
            Assert.AreEqual(Visibility.Visible, settingsView.Visibility);
            Assert.AreEqual(Visibility.Collapsed, placeholder.Visibility);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            failure = CaptureCleanupFailure(failure, () => window?.Close());
            failure = CaptureCleanupFailure(
                failure,
                () =>
                {
                    try
                    {
                        host?.StopAsync().GetAwaiter().GetResult();
                    }
                    finally
                    {
                        host?.Dispose();
                    }
                });
        }

        return failure;
    }

    private static Exception? CaptureCleanupFailure(Exception? currentFailure, Action cleanup)
    {
        try
        {
            cleanup();
            return currentFailure;
        }
        catch (Exception exception)
        {
            return currentFailure is null
                ? exception
                : new AggregateException(currentFailure, exception);
        }
    }

    private sealed class TemporaryStartupLogDirectory : IDisposable
    {
        private readonly string _testRoot = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "CIA.SPR60.Tests");

        public TemporaryStartupLogDirectory()
        {
            Path = System.IO.Path.Combine(_testRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (!Directory.Exists(Path))
            {
                return;
            }

            var resolvedRoot = System.IO.Path.GetFullPath(_testRoot)
                .TrimEnd(System.IO.Path.DirectorySeparatorChar)
                + System.IO.Path.DirectorySeparatorChar;
            var resolvedTarget = System.IO.Path.GetFullPath(Path);

            if (!resolvedTarget.StartsWith(resolvedRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Refusing to delete a startup test directory outside the test root.");
            }

            Directory.Delete(resolvedTarget, recursive: true);
        }
    }
}
