using CIA.Core.Diagnostics;
using CIA.Core.Runtime;
using CIA.ProcessingHost.Discovery;
using CIA.ProcessingHost.Operations;
using CIA.ProcessingHost.Repository;
using CIA.ProcessingHost.SourceIntake;
using CIA.ProcessingHost.SourceInterpretation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Formatting.Compact;

namespace CIA.ProcessingHost.Hosting;

public static class ProcessingHostApplicationHost
{
    public const string ProcessRole = "ProcessingHost";

    public static IHost Create(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var builder = Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings
            {
                Args = args,
                ApplicationName = typeof(ProcessingHostApplicationHost).Assembly.GetName().Name,
                ContentRootPath = AppContext.BaseDirectory
            });

        var runtimeOptions = ProcessingHostRuntimeOptions.FromConfiguration(builder.Configuration);

        builder.Services.AddSingleton<IProcessingHistoryRecorder, ClefProcessingHistoryRecorder>();
        builder.Services.AddSingleton<CooperativeOperationCancellation>();
        builder.Services.AddSingleton(_ => ApplicationPaths.ForCurrentUser());
        builder.Services.AddSingleton<ArchiveExtractionService>();
        builder.Services.AddSingleton<SourceIntakeService>();
        builder.Services.AddSingleton<ISourceInterpreter, SourceInterpreter>();
        builder.Services.AddSingleton<SourceRefreshService>();
        builder.Services.AddSingleton<DiscoveryService>();
        builder.Services.AddSingleton<StructuredInformationRepository>();

        if (runtimeOptions is not null)
        {
            builder.Services.AddSingleton(runtimeOptions);
            builder.Services.AddHostedService<ProcessingHostLifetimeService>();
        }

        ConfigureLogging(builder);
        return builder.Build();
    }

    private static void ConfigureLogging(HostApplicationBuilder builder)
    {
        builder.Logging.ClearProviders();

        var logDirectory = ApplicationLogPaths.ResolveDirectory(
            builder.Configuration[ApplicationLogPaths.DirectoryConfigurationKey]);
        Directory.CreateDirectory(logDirectory);

        var serilogLogger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .Enrich.WithProperty("ProcessRole", ProcessRole)
            .WriteTo.File(
                new CompactJsonFormatter(),
                ApplicationLogPaths.GetProcessingHostFilePath(logDirectory),
                rollingInterval: RollingInterval.Day,
                shared: false)
            .CreateLogger();

        builder.Services.AddSerilog(serilogLogger, dispose: true);
    }
}
