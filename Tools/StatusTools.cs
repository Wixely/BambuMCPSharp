using System.ComponentModel;
using System.Text.Json.Nodes;
using BambuMCPSharp.Services;
using ModelContextProtocol.Server;

namespace BambuMCPSharp.Tools;

/// <summary>
/// Monitoring: the parsed digest a watch-loop calls, the raw report for everything else,
/// firmware versions, HMS errors, and AMS state.
/// </summary>
[McpServerToolType]
public static class StatusTools
{
    [McpServerTool(Name = "bambu_status"),
     Description("Get a parsed status digest of the printer: job state, progress, layers, remaining time, skipped part IDs, stage, speed, temperatures, fans, light, wifi, AMS summary, and active errors. This is the tool to poll in a monitoring loop; use bambu_status_raw for fields it omits.")]
    public static async Task<string> Status(
        PrinterRegistry registry,
        SafetyGate gate,
        [Description("Printer alias. Omit to use the default printer.")] string? alias = null,
        CancellationToken ct = default)
    {
        gate.EnsureFeature(gate.Options.EnableStatus, "bambu_status", "EnableStatus");
        var connection = registry.Get(alias);
        var (state, reportedUtc) = await connection.GetStateAsync(ct);
        var print = state["print"] as JsonObject;

        var hms = (print?["hms"] as JsonArray)?
            .OfType<JsonObject>()
            .Select(HmsCode.Decode)
            .ToList();

        var lights = (print?["lights_report"] as JsonArray)?
            .OfType<JsonObject>()
            .ToDictionary(
                l => ToolHelpers.Str(l["node"]) ?? "?",
                l => ToolHelpers.Str(l["mode"]));

        var amsUnits = (print?["ams"]?["ams"] as JsonArray)?.OfType<JsonObject>().ToList();
        var speedLevel = ToolHelpers.Num(print?["spd_lvl"]);
        var stage = ToolHelpers.Num(print?["stg_cur"]);

        return ToolHelpers.Json(gate, new
        {
            alias = connection.Printer.Alias,
            reportedUtc,
            stale = reportedUtc is null || DateTimeOffset.UtcNow - reportedUtc.Value > TimeSpan.FromSeconds(gate.Options.StateFreshSeconds * 3),
            job = new
            {
                gcodeState = ToolHelpers.Str(print?["gcode_state"]),
                file = ToolHelpers.Str(print?["subtask_name"]) ?? ToolHelpers.Str(print?["gcode_file"]),
                progressPercent = ToolHelpers.Num(print?["mc_percent"]),
                currentLayer = ToolHelpers.Num(print?["layer_num"]),
                totalLayers = ToolHelpers.Num(print?["total_layer_num"]),
                remainingMinutes = ToolHelpers.Num(print?["mc_remaining_time"]),
                stage = ToolHelpers.StageName(stage),
                stageCode = stage,
                speedLevel,
                speedName = ToolHelpers.SpeedLevelName(speedLevel),
                printError = ToolHelpers.Num(print?["print_error"]),
                skippedObjectIds = SkipPartsWorkflow.ReadSkippedObjectIds(state),
            },
            temperaturesC = new
            {
                nozzle = ToolHelpers.Num(print?["nozzle_temper"]),
                nozzleTarget = ToolHelpers.Num(print?["nozzle_target_temper"]),
                bed = ToolHelpers.Num(print?["bed_temper"]),
                bedTarget = ToolHelpers.Num(print?["bed_target_temper"]),
                chamber = ToolHelpers.Num(print?["chamber_temper"]),
            },
            fansPercent = new
            {
                part = ToolHelpers.FanGearToPercent(print?["cooling_fan_speed"]),
                auxiliary = ToolHelpers.FanGearToPercent(print?["big_fan1_speed"]),
                chamber = ToolHelpers.FanGearToPercent(print?["big_fan2_speed"]),
                heatbreak = ToolHelpers.FanGearToPercent(print?["heatbreak_fan_speed"]),
            },
            lights,
            wifiSignal = ToolHelpers.Str(print?["wifi_signal"]),
            sdCard = ToolHelpers.Str(print?["sdcard"]),
            ams = amsUnits is null ? null : new
            {
                units = amsUnits.Count,
                activeTray = ToolHelpers.Str(print?["ams"]?["tray_now"]),
            },
            activeErrors = hms,
        });
    }

    [McpServerTool(Name = "bambu_status_raw"),
     Description("Get the full merged MQTT report state as raw JSON (size-capped). Field names follow Bambu's firmware (mc_percent, stg_cur, spd_lvl...). Use when bambu_status doesn't surface what you need.")]
    public static async Task<string> StatusRaw(
        PrinterRegistry registry,
        SafetyGate gate,
        [Description("Printer alias. Omit to use the default printer.")] string? alias = null,
        CancellationToken ct = default)
    {
        gate.EnsureFeature(gate.Options.EnableStatus, "bambu_status_raw", "EnableStatus");
        var connection = registry.Get(alias);
        var (state, _) = await connection.GetStateAsync(ct);
        return ToolHelpers.Json(gate, state);
    }

    [McpServerTool(Name = "bambu_version"),
     Description("Get firmware and hardware versions of the printer and its modules (mainboard, toolhead, AMS...) via info.get_version.")]
    public static async Task<string> Version(
        PrinterRegistry registry,
        SafetyGate gate,
        [Description("Printer alias. Omit to use the default printer.")] string? alias = null,
        CancellationToken ct = default)
    {
        gate.EnsureFeature(gate.Options.EnableStatus, "bambu_version", "EnableStatus");
        var connection = registry.Get(alias);
        var result = await connection.SendAsync("info", new JsonObject { ["command"] = "get_version" }, ct);

        if (!result.Acknowledged)
        {
            return ToolHelpers.CommandJson(gate, "get_version", result);
        }
        return ToolHelpers.Json(gate, result.Ack);
    }

    [McpServerTool(Name = "bambu_hms_errors"),
     Description("List the printer's active HMS (Health Management System) errors, decoded to their HMS_xxxx code, severity (fatal/serious/common/info), module, and the Bambu wiki URL explaining the fix. Empty list means no active errors.")]
    public static async Task<string> HmsErrors(
        PrinterRegistry registry,
        SafetyGate gate,
        [Description("Printer alias. Omit to use the default printer.")] string? alias = null,
        CancellationToken ct = default)
    {
        gate.EnsureFeature(gate.Options.EnableStatus, "bambu_hms_errors", "EnableStatus");
        var connection = registry.Get(alias);
        var (state, reportedUtc) = await connection.GetStateAsync(ct);

        var entries = (state["print"]?["hms"] as JsonArray)?
            .OfType<JsonObject>()
            .Select(HmsCode.Decode)
            .ToList() ?? new List<JsonObject>();

        return ToolHelpers.Json(gate, new
        {
            alias = connection.Printer.Alias,
            reportedUtc,
            count = entries.Count,
            errors = entries,
            printError = ToolHelpers.Num(state["print"]?["print_error"]),
        });
    }

    [McpServerTool(Name = "bambu_diagnostics"),
     Description("Get a focused diagnostic report: active HMS alerts, the current clearable print_error in decimal and hex, job/stage context, temperatures, heatbreak fan, Wi-Fi, SD card, camera state, and safe next-action guidance. Read-only.")]
    public static async Task<string> Diagnostics(
        PrinterRegistry registry,
        SafetyGate gate,
        [Description("Printer alias. Omit to use the default printer.")] string? alias = null,
        CancellationToken ct = default)
    {
        gate.EnsureFeature(gate.Options.EnableStatus, "bambu_diagnostics", "EnableStatus");
        var connection = registry.Get(alias);
        var (state, reportedUtc) = await connection.GetStateAsync(ct);
        var report = PrinterDiagnostics.CreateReport(
            state,
            reportedUtc,
            connection.Printer.Alias,
            gate.Options.StateFreshSeconds * 3);
        return ToolHelpers.Json(gate, report);
    }

    [McpServerTool(Name = "bambu_ams_status"),
     Description("Get AMS (filament system) detail: each unit's humidity and temperature, and each tray's filament type, colour, and remaining percentage. Reports 'no AMS' when none is fitted.")]
    public static async Task<string> AmsStatus(
        PrinterRegistry registry,
        SafetyGate gate,
        [Description("Printer alias. Omit to use the default printer.")] string? alias = null,
        CancellationToken ct = default)
    {
        gate.EnsureFeature(gate.Options.EnableStatus, "bambu_ams_status", "EnableStatus");
        var connection = registry.Get(alias);
        var (state, reportedUtc) = await connection.GetStateAsync(ct);

        var ams = state["print"]?["ams"] as JsonObject;
        var units = (ams?["ams"] as JsonArray)?.OfType<JsonObject>().Select(unit => new
        {
            id = ToolHelpers.Str(unit["id"]),
            humidity = ToolHelpers.Str(unit["humidity"]),
            temperatureC = ToolHelpers.Num(unit["temp"]),
            trays = (unit["tray"] as JsonArray)?.OfType<JsonObject>().Select(tray => new
            {
                id = ToolHelpers.Str(tray["id"]),
                type = ToolHelpers.Str(tray["tray_type"]),
                color = ToolHelpers.Str(tray["tray_color"]),
                remainPercent = ToolHelpers.Num(tray["remain"]),
                name = ToolHelpers.Str(tray["tray_id_name"]) ?? ToolHelpers.Str(tray["tray_sub_brands"]),
            }).ToList(),
        }).ToList();

        if (units is null || units.Count == 0)
        {
            return ToolHelpers.Json(gate, new
            {
                alias = connection.Printer.Alias,
                reportedUtc,
                ams = (object?)null,
                note = "No AMS reported by this printer.",
            });
        }

        return ToolHelpers.Json(gate, new
        {
            alias = connection.Printer.Alias,
            reportedUtc,
            activeTray = ToolHelpers.Str(ams?["tray_now"]),
            units,
        });
    }
}
