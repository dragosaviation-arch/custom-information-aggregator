using CIA.Contracts.Sources;
using CIA.Core.Diagnostics;
using CIA.Desktop.Hosting;
using CIA.Desktop.Sources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CIA.Desktop.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ProcessingHostExecutableBoundaryTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    [TestMethod]
    public async Task AssembledRuntimeLaunchesCompanionAndLoadsXmlAcrossNamedPipe()
    {
        using var runtime = new TemporaryRuntimeDirectory();
        using var timeout = new CancellationTokenSource(TestTimeout);
        using var host = DesktopApplicationHost.Create(
            [$"--{ApplicationLogPaths.DirectoryConfigurationKey}={runtime.LogDirectory}"]);

        var expectedExecutablePath = Path.Combine(
            AppContext.BaseDirectory,
            "CIA.ProcessingHost.exe");
        var executablePath = ProcessingHostSupervisorOptions.ResolveCompanionExecutablePath();

        Assert.AreEqual(expectedExecutablePath, executablePath);
        Assert.IsTrue(
            File.Exists(executablePath),
            $"Processing Host companion executable not found at '{executablePath}'.");
        AssertRequiredCompanionFile("CIA.ProcessingHost.dll");
        AssertRequiredCompanionFile("CIA.ProcessingHost.deps.json");
        AssertRequiredCompanionFile("CIA.ProcessingHost.runtimeconfig.json");
        AssertRequiredCompanionFile("SharpCompress.dll");

        await host.StartAsync(timeout.Token);

        try
        {
            var client = host.Services.GetRequiredService<ISourceIntakeClient>();
            Assert.IsInstanceOfType<ProcessingHostSourceIntakeClient>(client);

            var result = await client.LoadAsync(
                SourceSelectionKind.XmlFile,
                runtime.XmlPath,
                SourceLoadSettings.Default,
                timeout.Token);

            Assert.IsTrue(
                result.Accepted,
                $"Real Processing Host source load failed: {result.FailureCode} - {result.FailureDescription}");
            Assert.HasCount(1, result.Sources);

            var source = result.Sources[0];
            Assert.AreEqual(Path.GetFullPath(runtime.XmlPath), source.Path);
            Assert.AreNotEqual(Guid.Empty, source.SourceId.Value);
            Assert.AreEqual(LoadedSourceKind.XmlFile, source.Kind);
            Assert.AreEqual(LoadedSourceStatus.Ready, source.Status);
            Assert.IsTrue(source.IsIncluded);

            var supervisor = host.Services.GetRequiredService<IProcessingHostSupervisor>();
            Assert.AreEqual(ProcessingHostLifecycleState.Ready, supervisor.Current.State);
            Assert.IsNotNull(supervisor.Current.ProcessId);
        }
        finally
        {
            await host.StopAsync(timeout.Token);
        }
    }

    private static void AssertRequiredCompanionFile(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, fileName);
        Assert.IsTrue(File.Exists(path), $"Required Processing Host file not found at '{path}'.");
    }

    private sealed class TemporaryRuntimeDirectory : IDisposable
    {
        private readonly string _testRoot = Path.Combine(
            Path.GetTempPath(),
            "CIA.RuntimeIntegration.Tests");

        public TemporaryRuntimeDirectory()
        {
            Root = Path.Combine(_testRoot, Guid.NewGuid().ToString("N"));
            LogDirectory = Path.Combine(Root, "Logs");
            XmlPath = Path.Combine(Root, "source.xml");

            Directory.CreateDirectory(LogDirectory);
            File.WriteAllText(
                XmlPath,
                "<source><item code=\"runtime-boundary\">preserved</item></source>");
        }

        public string Root { get; }

        public string LogDirectory { get; }

        public string XmlPath { get; }

        public void Dispose()
        {
            if (!Directory.Exists(Root))
            {
                return;
            }

            var resolvedRoot = Path.GetFullPath(_testRoot)
                .TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var resolvedTarget = Path.GetFullPath(Root);

            if (!resolvedTarget.StartsWith(resolvedRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Refusing to delete a runtime-integration test directory outside the test root.");
            }

            Directory.Delete(resolvedTarget, recursive: true);
        }
    }
}
