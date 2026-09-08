using System.Globalization;
using System.Text.Json;
using CIA.Contracts.Operations;
using CIA.Core;
using CIA.Core.Diagnostics;
using CIA.Desktop;
using CIA.Desktop.Hosting;
using CIA.Desktop.Presentation;
using CIA.Desktop.Workflow;
using CIA.Desktop.Database;
using CIA.ProcessingHost.Database;
using CIA.ProcessingHost.Repository;
using CIA.ProcessingHost.Hosting;
using CIA.ProcessingHost.SourceInterpretation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CIA.ProcessingHost.Tests;

[TestClass]
public sealed class ApplicationHostTests
{
    private const string VerificationConfigurationKey = "CIA:Verification:Value";

    [TestMethod]
    public void DesktopUsesGenericHostCompositionForExistingRuntimeServices()
    {
        using var logs = new TemporaryLogDirectory();
        using var host = DesktopApplicationHost.Create(CreateArguments(logs.Path));

        Assert.IsInstanceOfType<IHost>(host);
        Assert.IsNotNull(host.Services.GetRequiredService<IConfiguration>());
        Assert.IsNotNull(host.Services.GetRequiredService<IHostApplicationLifetime>());
        Assert.IsNotNull(host.Services.GetRequiredService<ILogger<App>>());

        var firstSession = host.Services.GetRequiredService<ApplicationSession>();
        var secondSession = host.Services.GetRequiredService<ApplicationSession>();
        var viewModel = host.Services.GetRequiredService<MainWindowViewModel>();
        var workflowCoordinator = host.Services.GetRequiredService<IApplicationWorkflowCoordinator>();
        var globalStatus = host.Services.GetRequiredService<GlobalStatusViewModel>();
        var serviceProbe = host.Services.GetRequiredService<IServiceProviderIsService>();

        Assert.AreSame(firstSession, secondSession);
        Assert.AreSame(firstSession, viewModel.Session);
        Assert.IsInstanceOfType<ApplicationWorkflowCoordinator>(workflowCoordinator);
        Assert.IsNotNull(host.Services.GetRequiredService<IDatabaseClient>());
        Assert.IsNotNull(host.Services.GetRequiredService<DatabaseBuildCoordinator>());
        Assert.AreEqual("Stopped", globalStatus.HostStatusText);
        Assert.AreEqual("No operation", globalStatus.OperationStatusText);
        Assert.IsTrue(serviceProbe.IsService(typeof(MainWindow)));
    }

    [TestMethod]
    public void ProcessingHostUsesGenericHostCompositionWithoutResidentServices()
    {
        using var logs = new TemporaryLogDirectory();
        using var host = ProcessingHostApplicationHost.Create(CreateArguments(logs.Path));

        Assert.IsInstanceOfType<IHost>(host);
        Assert.IsNotNull(host.Services.GetRequiredService<IConfiguration>());
        Assert.IsNotNull(host.Services.GetRequiredService<IHostApplicationLifetime>());
        Assert.IsNotNull(host.Services.GetRequiredService<ILogger<Program>>());
        Assert.IsNotNull(host.Services.GetRequiredService<ISourceInterpreter>());
        Assert.IsNotNull(host.Services.GetRequiredService<ISourceValueBatchReader>());
        Assert.IsNotNull(host.Services.GetRequiredService<StructuredInformationRepository>());
        Assert.IsNotNull(host.Services.GetRequiredService<DatabaseGenerationService>());
        Assert.IsNotNull(host.Services.GetRequiredService<DatabaseReviewService>());
        Assert.IsEmpty(host.Services.GetServices<IHostedService>());
    }

    [TestMethod]
    public void GenericHostConfigurationLoadsProcessArguments()
    {
        using var logs = new TemporaryLogDirectory();
        const string expectedValue = "configured-through-generic-host";
        var arguments = CreateArguments(
            logs.Path,
            $"--{VerificationConfigurationKey}={expectedValue}");

        using var desktopHost = DesktopApplicationHost.Create(arguments);
        using var processingHost = ProcessingHostApplicationHost.Create(arguments);

        Assert.AreEqual(
            expectedValue,
            desktopHost.Services.GetRequiredService<IConfiguration>()[VerificationConfigurationKey]);
        Assert.AreEqual(
            expectedValue,
            processingHost.Services.GetRequiredService<IConfiguration>()[VerificationConfigurationKey]);
    }

    [TestMethod]
    public void DefaultLogPathsUseWritableLocalApplicationDataAndSeparateStreams()
    {
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        var expectedDirectory = Path.Combine(
            localApplicationData,
            "Custom Information Aggregator",
            "Logs");
        var actualDirectory = ApplicationLogPaths.ResolveDirectory(configuredDirectory: null);

        Assert.AreEqual(Path.GetFullPath(expectedDirectory), Path.GetFullPath(actualDirectory));
        Assert.AreNotEqual(
            ApplicationLogPaths.GetUiFilePath(actualDirectory),
            ApplicationLogPaths.GetProcessingHostFilePath(actualDirectory));
        Assert.IsFalse(
            Path.GetFullPath(actualDirectory).StartsWith(
                Path.GetFullPath(AppContext.BaseDirectory),
                StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task HostsWriteSeparateStructuredClefStreamsWithSharedCorrelation()
    {
        using var logs = new TemporaryLogDirectory();
        var correlation = OperationCorrelation.CreateNew(
            new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero));
        var arguments = CreateArguments(logs.Path);

        await WriteCorrelatedEventAsync(
            DesktopApplicationHost.Create(arguments),
            DesktopApplicationHost.ProcessRole,
            correlation);
        await WriteCorrelatedEventAsync(
            ProcessingHostApplicationHost.Create(arguments),
            ProcessingHostApplicationHost.ProcessRole,
            correlation);

        var uiFile = AssertSingleLogFile(logs.Path, "cia-ui-*.clef");
        var processingHostFile = AssertSingleLogFile(logs.Path, "cia-processing-host-*.clef");
        Assert.AreNotEqual(uiFile, processingHostFile);

        var uiEvent = FindEvent(uiFile, "Operation reached stage {Stage}");
        var processingHostEvent = FindEvent(processingHostFile, "Operation reached stage {Stage}");

        AssertClefEvent(uiEvent, DesktopApplicationHost.ProcessRole, correlation);
        AssertClefEvent(processingHostEvent, ProcessingHostApplicationHost.ProcessRole, correlation);
    }

    [TestMethod]
    public async Task DefaultLoggingDoesNotDumpConfigurationOrRawSourcePayloads()
    {
        using var logs = new TemporaryLogDirectory();
        const string proprietarySourcePayload = "PROPRIETARY-SOURCE-CONTENT-DO-NOT-LOG";
        var arguments = CreateArguments(
            logs.Path,
            $"--CIA:Verification:RawSourcePayload={proprietarySourcePayload}");
        var host = DesktopApplicationHost.Create(arguments);

        try
        {
            await host.StartAsync();
            var configuration = host.Services.GetRequiredService<IConfiguration>();
            Assert.AreEqual(
                proprietarySourcePayload,
                configuration["CIA:Verification:RawSourcePayload"]);

            var logger = host.Services.GetRequiredService<ILogger<ApplicationHostTests>>();
            logger.LogInformation(
                "Source processing infrastructure available for source {SourceId}",
                "verification-source");
            await host.StopAsync();
        }
        finally
        {
            host.Dispose();
        }

        var logText = File.ReadAllText(AssertSingleLogFile(logs.Path, "cia-ui-*.clef"));
        Assert.IsFalse(logText.Contains(proprietarySourcePayload, StringComparison.Ordinal));
        StringAssert.Contains(logText, "verification-source");
    }

    [TestMethod]
    public void ConfiguredLogDirectoryMustBeAbsolute()
    {
        Assert.ThrowsExactly<InvalidOperationException>(
            () => ApplicationLogPaths.ResolveDirectory("relative-log-directory"));
    }

    [TestMethod]
    public void ConfiguredLogDirectoryCannotUseInstalledBinaryDirectory()
    {
        Assert.ThrowsExactly<InvalidOperationException>(
            () => ApplicationLogPaths.ResolveDirectory(AppContext.BaseDirectory));
    }

    private static string[] CreateArguments(string logDirectory, params string[] additionalArguments)
    {
        return [$"--{ApplicationLogPaths.DirectoryConfigurationKey}={logDirectory}", .. additionalArguments];
    }

    private static async Task WriteCorrelatedEventAsync(
        IHost host,
        string processRole,
        OperationCorrelation correlation)
    {
        try
        {
            await host.StartAsync();
            var logger = host.Services.GetRequiredService<ILogger<ApplicationHostTests>>();
            var scopeProperties = new Dictionary<string, object>
            {
                ["OperationId"] = correlation.OperationId.ToString(),
                ["OperationInitiatedAtUtc"] = correlation.InitiatedAtUtc
            };

            using (logger.BeginScope(scopeProperties))
            {
                logger.LogInformation(
                    "Operation reached stage {Stage}",
                    $"{processRole}-verification");
            }

            await host.StopAsync();
        }
        finally
        {
            host.Dispose();
        }
    }

    private static string AssertSingleLogFile(string directory, string searchPattern)
    {
        var files = Directory.GetFiles(directory, searchPattern, SearchOption.TopDirectoryOnly);
        Assert.HasCount(1, files);
        return files[0];
    }

    private static JsonElement FindEvent(string logFile, string messageTemplate)
    {
        foreach (var line in File.ReadLines(logFile))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            if (root.TryGetProperty("@mt", out var template)
                && template.GetString() == messageTemplate)
            {
                return root.Clone();
            }
        }

        Assert.Fail($"CLEF event with template '{messageTemplate}' was not found in '{logFile}'.");
        return default;
    }

    private static void AssertClefEvent(
        JsonElement logEvent,
        string expectedProcessRole,
        OperationCorrelation expectedCorrelation)
    {
        Assert.AreEqual(expectedProcessRole, logEvent.GetProperty("ProcessRole").GetString());
        Assert.AreEqual(
            expectedCorrelation.OperationId.ToString(),
            logEvent.GetProperty("OperationId").GetString());
        Assert.AreEqual(
            expectedCorrelation.InitiatedAtUtc,
            logEvent.GetProperty("OperationInitiatedAtUtc").GetDateTimeOffset());
        Assert.AreEqual(
            TimeSpan.Zero,
            DateTimeOffset.Parse(
                logEvent.GetProperty("@t").GetString()!,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind).Offset);
        Assert.AreEqual(
            $"{expectedProcessRole}-verification",
            logEvent.GetProperty("Stage").GetString());
    }

    private sealed class TemporaryLogDirectory : IDisposable
    {
        private readonly string _testRoot;

        public TemporaryLogDirectory()
        {
            _testRoot = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "CIA.SPR59.Tests");
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
                throw new InvalidOperationException("Refusing to delete a test directory outside the test root.");
            }

            Directory.Delete(resolvedTarget, recursive: true);
        }
    }
}
