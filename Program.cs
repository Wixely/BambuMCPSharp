using System.Net;
using BambuMCPSharp.Configuration;
using BambuMCPSharp.Hosting;
using BambuMCPSharp.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Serilog;

namespace BambuMCPSharp;

public static class Program
{
    public static int Main(string[] args)
    {
        // When running as a Windows Service the working directory is
        // C:\Windows\System32, so resolve config and logs relative to the exe.
        var contentRoot = GetContentRoot();
        var isService = WindowsServiceHelpers.IsWindowsService();
        if (!isService)
        {
            McpSharpIcon.ApplyConsoleWindowIcon();
        }

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(contentRoot, "logs", "bambumcp-bootstrap-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                shared: true)
            .CreateBootstrapLogger();

        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = args,
                ContentRootPath = contentRoot,
            });

            builder.Configuration
                .SetBasePath(contentRoot)
                .AddJsonFile(ResolveConfigFile(contentRoot, "appsettings.json"), optional: true, reloadOnChange: true)
                .AddJsonFile(ResolveConfigFile(contentRoot, $"appsettings.{builder.Environment.EnvironmentName}.json"), optional: true, reloadOnChange: true)
                .AddJsonFile(ResolveConfigFile(contentRoot, "appsettings.Local.json"), optional: true, reloadOnChange: true)
                .AddJsonFile(ResolveConfigFile(contentRoot, "BambuMCPSharp.json"), optional: true, reloadOnChange: true)
                .AddJsonFile(ResolveConfigFile(contentRoot, $"BambuMCPSharp.{builder.Environment.EnvironmentName}.json"), optional: true, reloadOnChange: true)
                .AddJsonFile(ResolveConfigFile(contentRoot, "BambuMCPSharp.Local.json"), optional: true, reloadOnChange: true)
                .AddEnvironmentVariables()
                .AddEnvironmentVariables(prefix: "BAMBUMCP_")
                .AddCommandLine(args);

            if (isService)
            {
                var svcOptions = builder.Configuration.GetSection(ServerOptions.SectionName).Get<ServerOptions>() ?? new ServerOptions();
                builder.Host.UseWindowsService(o => o.ServiceName = svcOptions.WindowsServiceName);
            }

            builder.Host.UseSerilog((ctx, services, cfg) => cfg
                .ReadFrom.Configuration(ctx.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext());

            builder.Services.Configure<BambuOptions>(
                builder.Configuration.GetSection(BambuOptions.SectionName));
            builder.Services.Configure<ServerOptions>(
                builder.Configuration.GetSection(ServerOptions.SectionName));

            builder.Services.AddSingleton<PrinterRegistry>();
            builder.Services.AddSingleton<SafetyGate>();
            builder.Services.AddSingleton<BambuFtp>();
            builder.Services.AddSingleton<CameraService>();

            builder.Services
                .AddMcpServer()
                .WithHttpTransport()
                .WithToolsFromAssembly();

            var server = builder.Configuration.GetSection(ServerOptions.SectionName).Get<ServerOptions>() ?? new ServerOptions();
            builder.WebHost.ConfigureKestrel(k =>
            {
                if (string.Equals(server.Host, "localhost", StringComparison.OrdinalIgnoreCase))
                {
                    k.ListenLocalhost(server.Port);
                }
                else if (IPAddress.TryParse(server.Host, out var ip))
                {
                    k.Listen(ip, server.Port);
                }
                else
                {
                    k.ListenAnyIP(server.Port);
                }
            });

            var app = builder.Build();

            app.UseSerilogRequestLogging();

            // Surface any swallowed exceptions from the host as fatal log entries.
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception in AppDomain");
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Log.Error(e.Exception, "Unobserved task exception");
                e.SetObserved();
            };

            var registry = app.Services.GetRequiredService<PrinterRegistry>();
            var gate = app.Services.GetRequiredService<SafetyGate>();

            LogStartup(
                "BambuMCPSharp",
                $"http://{server.Host}:{server.Port}{server.Path}",
                "HTTP (Streamable)",
                isService ? "WindowsService" : "Console",
                contentRoot,
                registry,
                gate.Options);

            app.UseMiddleware<McpPasswordMiddleware>();

            app.MapFavicon();
            app.MapGet("/healthz", () => new
            {
                status = "ok",
                server = "BambuMCPSharp",
                path = server.Path,
                readOnly = gate.Options.ReadOnly,
                printers = registry.Printers.Values.Select(p => new
                {
                    alias = p.Alias,
                    host = p.Host,
                    model = p.Model,
                    serial = p.MaskedSerial,
                }),
                timeUtc = DateTimeOffset.UtcNow,
            });
            app.MapMcp(server.Path);

            app.Run();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Server terminated unexpectedly");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    /// <summary>
    /// Emits the operating posture so a misconfigured deployment is obvious from the first
    /// screen of logs rather than from a surprising tool refusal mid-print.
    /// </summary>
    private static void LogStartup(
        string serviceName,
        string endpoint,
        string transport,
        string mode,
        string contentRoot,
        PrinterRegistry registry,
        BambuOptions options)
    {
        var log = Log.ForContext("SourceContext", serviceName + ".Startup");
        log.Information("{ServiceName} startup", serviceName);
        log.Information("  Endpoint: {Endpoint}", endpoint);
        log.Information("  Transport: {Transport}", transport);
        log.Information("  Mode: {Mode}", mode);
        log.Information("  Read-only: {ReadOnly}", options.ReadOnly);

        if (!registry.HasPrinters)
        {
            log.Warning("  Printers: NONE CONFIGURED — add Bambu:Printers entries with Host + SerialNumber + AccessCode");
        }
        else
        {
            foreach (var printer in registry.Printers.Values)
            {
                var isDefault = string.Equals(printer.Alias, registry.DefaultAlias, StringComparison.OrdinalIgnoreCase);
                log.Information(
                    "  Printer: {Alias}{Default} {Host} ({Model}, serial {Serial}, access code {CodeState})",
                    printer.Alias,
                    isDefault ? " (default)" : string.Empty,
                    printer.Host,
                    printer.Model,
                    printer.MaskedSerial,
                    string.IsNullOrEmpty(printer.AccessCode) ? "MISSING" : $"{printer.AccessCode.Length} chars");

                if (string.IsNullOrWhiteSpace(printer.SerialNumber))
                {
                    log.Warning("    No serial number for '{Alias}' — MQTT topics cannot be formed without Bambu:Printers:<n>:SerialNumber", printer.Alias);
                }
                if (string.IsNullOrWhiteSpace(printer.AccessCode))
                {
                    log.Warning("    No access code for '{Alias}' — set Bambu:Printers:<n>:AccessCode from the printer's network screen", printer.Alias);
                }
            }
        }

        log.Information(
            "  Print control: {PrintControl}   stop: {Stop}   start: {Start}   speed: {Speed}",
            options.AllowPrintControl, options.AllowStopPrint, options.AllowStartPrint, options.AllowSpeedControl);
        log.Information(
            "  Temps: {Temps} (max nozzle {Nozzle}C, bed {Bed}C)   fans: {Fans}   light: {Light}",
            options.AllowTemperatureControl, options.MaxNozzleTempC, options.MaxBedTempC,
            options.AllowFanControl, options.AllowLightControl);
        log.Information(
            "  Motion: {Motion}   raw G-code: {Gcode}   calibration: {Calibration}",
            options.AllowMotionControl, options.AllowRawGcode, options.AllowCalibration);
        log.Information(
            "  File upload: {Upload}   delete: {Delete}",
            options.AllowFileUpload, options.AllowFileDelete);
        log.Information(
            "  Transfers: {Transfers}   snapshots: {Snapshots}",
            options.FileTransferDirectory, options.SnapshotDirectory);
        log.Information("  Content root: {ContentRoot}", contentRoot);
    }

    private static string GetContentRoot() =>
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    private static string ResolveConfigFile(string contentRoot, string fileName)
    {
        if (File.Exists(Path.Combine(contentRoot, fileName)))
        {
            return fileName;
        }

        try
        {
            var match = Directory.EnumerateFiles(contentRoot, "*", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(path => string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase));

            return match is null ? fileName : Path.GetFileName(match);
        }
        catch (DirectoryNotFoundException)
        {
            return fileName;
        }
    }
}
