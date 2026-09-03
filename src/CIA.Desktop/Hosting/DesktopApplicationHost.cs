using System.IO;
using CIA.Core;
using CIA.Core.Diagnostics;
using CIA.Desktop.Presentation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Formatting.Compact;

namespace CIA.Desktop.Hosting;

public static class DesktopApplicationHost
{
    public const string ProcessRole = "UI";

    public static IHost Create(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var builder = Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings
            {
                Args = args,
                ApplicationName = typeof(DesktopApplicationHost).Assembly.GetName().Name,
                ContentRootPath = AppContext.BaseDirectory
            });

        builder.Services.AddSingleton<ApplicationSession>();
        builder.Services.AddSingleton<MainWindowViewModel>();
        builder.Services.AddSingleton<MainWindow>();

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
                ApplicationLogPaths.GetUiFilePath(logDirectory),
                rollingInterval: RollingInterval.Day,
                shared: false)
            .CreateLogger();

        builder.Services.AddSerilog(serilogLogger, dispose: true);
    }
}
