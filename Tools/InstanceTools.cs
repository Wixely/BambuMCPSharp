using System.ComponentModel;
using BambuMCPSharp.Services;
using ModelContextProtocol.Server;

namespace BambuMCPSharp.Tools;

/// <summary>Which printers this server knows and how the connections are doing.</summary>
[McpServerToolType]
public static class InstanceTools
{
    [McpServerTool(Name = "bambu_list_printers"),
     Description("List the configured Bambu printers: alias, host, model, masked serial, and current MQTT connection state. Access codes are never shown. Call this first to learn the aliases other tools accept.")]
    public static string ListPrinters(PrinterRegistry registry, SafetyGate gate)
    {
        var printers = registry.Printers.Values.Select(p =>
        {
            // Get() only materializes the connection object; nothing touches the network
            // until a tool calls EnsureConnectedAsync.
            var connection = registry.Get(p.Alias);
            return new
            {
                alias = p.Alias,
                isDefault = string.Equals(p.Alias, registry.DefaultAlias, StringComparison.OrdinalIgnoreCase),
                host = p.Host,
                model = p.Model,
                serial = p.MaskedSerial,
                description = string.IsNullOrWhiteSpace(p.Description) ? null : p.Description,
                mqttConnected = connection?.IsConnected ?? false,
                lastReportUtc = connection?.LastReportUtc,
            };
        }).ToList();

        return ToolHelpers.Json(gate, new
        {
            printers,
            readOnly = gate.Options.ReadOnly,
            count = printers.Count,
        });
    }

    [McpServerTool(Name = "bambu_printer_health"),
     Description("Connect to one printer's MQTT channel (if not already connected) and report link health: connected, last report age, total reports received. Use it to diagnose an unreachable printer before blaming a tool.")]
    public static async Task<string> PrinterHealth(
        PrinterRegistry registry,
        SafetyGate gate,
        [Description("Printer alias. Omit to use the default printer.")] string? alias = null,
        CancellationToken ct = default)
    {
        var connection = registry.Get(alias);

        string? connectError = null;
        try
        {
            await connection.EnsureConnectedAsync(ct);
        }
        catch (Exception ex)
        {
            connectError = ex.Message;
        }

        var lastReport = connection.LastReportUtc;
        return ToolHelpers.Json(gate, new
        {
            alias = connection.Printer.Alias,
            host = connection.Printer.Host,
            model = connection.Printer.Model,
            serial = connection.Printer.MaskedSerial,
            mqttConnected = connection.IsConnected,
            connectError,
            lastReportUtc = lastReport,
            lastReportAgeSeconds = lastReport is null ? null : (double?)(DateTimeOffset.UtcNow - lastReport.Value).TotalSeconds,
            reportsReceived = connection.ReportCount,
        });
    }
}
