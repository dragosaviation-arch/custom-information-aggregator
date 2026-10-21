using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace CIA.Desktop.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ProgressBarAnimationTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(20);

    [TestMethod]
    public async Task IndeterminateAnimationStopsResetsAndRestoresDeterminateFill()
    {
        await WpfTestApplication.RunAsync(
            async () =>
            {
                var progress = new ProgressBar
                {
                    Width = 200,
                    Height = 5,
                    Minimum = 0,
                    Maximum = 100,
                    Value = 25,
                    Style = (Style)Application.Current.FindResource(
                        "CiaDeterminateProgressBarStyle")
                };
                var window = new Window
                {
                    Width = 240,
                    Height = 80,
                    Content = progress,
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -10000,
                    Top = -10000
                };

                try
                {
                    window.Show();
                    window.UpdateLayout();
                    await Dispatcher.Yield(DispatcherPriority.Render);

                    var track = (FrameworkElement)progress.Template.FindName(
                        "PART_Track",
                        progress);
                    var indicator = (FrameworkElement)progress.Template.FindName(
                        "PART_Indicator",
                        progress);
                    var transform = (TranslateTransform)progress.Template.FindName(
                        "IndeterminateTransform",
                        progress);

                    Assert.IsFalse(transform.HasAnimatedProperties);
                    Assert.AreEqual(0d, transform.X);
                    Assert.AreEqual(
                        track.ActualWidth * 0.25,
                        indicator.ActualWidth,
                        0.5,
                        "The determinate indicator must retain percentage-based width.");

                    progress.IsIndeterminate = true;
                    await Dispatcher.Yield(DispatcherPriority.Render);

                    Assert.IsTrue(transform.HasAnimatedProperties);

                    progress.IsIndeterminate = false;
                    progress.Value = 60;
                    window.UpdateLayout();
                    await Dispatcher.Yield(DispatcherPriority.Render);

                    Assert.IsFalse(
                        transform.HasAnimatedProperties,
                        "The indeterminate animation clock must be removed in determinate mode.");
                    Assert.AreEqual(
                        0d,
                        transform.X,
                        "Removing the storyboard must restore the translation base value.");
                    Assert.AreEqual(
                        track.ActualWidth * 0.60,
                        indicator.ActualWidth,
                        0.5,
                        "Determinate progress must resume with the current percentage.");
                }
                finally
                {
                    window.Close();
                }
            }).WaitAsync(TestTimeout);
    }
}
