using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BambuMCPSharp.Services;

/// <summary>The printer error value and task context required by <c>clean_print_error</c>.</summary>
public sealed record PrintErrorContext(long Code, string SubtaskId)
{
    public string HexCode => PrinterDiagnostics.FormatErrorCode(Code);
}

/// <summary>Extracts a bounded diagnostic view and builds the documented printer error acknowledgement.</summary>
public static class PrinterDiagnostics
{
    public static JsonObject CreateReport(
        JsonObject state,
        DateTimeOffset? reportedUtc,
        string alias,
        int staleAfterSeconds)
    {
        var print = state["print"] as JsonObject;
        var errors = (print?["hms"] as JsonArray)?
            .OfType<JsonObject>()
            .Select(HmsCode.Decode)
            .ToList() ?? new List<JsonObject>();
        var printError = CurrentPrintError(state);
        var stage = ReadDouble(print?["stg_cur"]);
        var stale = reportedUtc is null ||
            DateTimeOffset.UtcNow - reportedUtc.Value > TimeSpan.FromSeconds(Math.Max(1, staleAfterSeconds));

        var report = new
        {
            alias,
            reportedUtc,
            stale,
            summary = new
            {
                hasAnyError = errors.Count > 0 || printError is not null,
                activeHmsCount = errors.Count,
                hasPrintError = printError is not null,
                hasClearablePrintErrorContext = printError is not null,
            },
            errors = new
            {
                hms = errors,
                printError = printError is null ? null : new
                {
                    code = printError.Code,
                    hex = printError.HexCode,
                    subtaskId = printError.SubtaskId,
                    clearable = true,
                },
                mcPrintErrorCode = ReadInt64(print?["mc_print_error_code"]),
            },
            job = new
            {
                gcodeState = ReadString(print?["gcode_state"]),
                stageCode = stage,
                stage = DescribeStage(stage),
                subtaskId = ReadString(print?["subtask_id"]),
            },
            environment = new
            {
                nozzleC = ReadDouble(print?["nozzle_temper"]),
                nozzleTargetC = ReadDouble(print?["nozzle_target_temper"]),
                bedC = ReadDouble(print?["bed_temper"]),
                bedTargetC = ReadDouble(print?["bed_target_temper"]),
                chamberC = ReadDouble(print?["chamber_temper"]),
                heatbreakFan = ReadDouble(print?["heatbreak_fan_speed"]),
            },
            connectivity = new
            {
                wifiSignal = ReadString(print?["wifi_signal"]),
                sdCard = ReadString(print?["sdcard"]),
                cameraStatus = ReadString(print?["xcam_status"]),
            },
            guidance = Guidance(errors.Count, printError),
        };

        return (JsonObject)(JsonSerializer.SerializeToNode(
            report,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new JsonObject());
    }

    public static PrintErrorContext? CurrentPrintError(JsonObject state)
    {
        var print = state["print"] as JsonObject;
        var code = ReadInt64(print?["print_error"]);
        if (code is null or 0) return null;

        return new PrintErrorContext(code.Value, ReadString(print?["subtask_id"]) ?? string.Empty);
    }

    public static JsonObject CreateClearPrintErrorCommand(PrintErrorContext error) => new()
    {
        ["command"] = "clean_print_error",
        ["subtask_id"] = error.SubtaskId,
        ["print_error"] = error.Code,
    };

    public static string FormatErrorCode(long code) => $"0x{unchecked((uint)code):X8}";

    /// <summary>Decode <c>print.stg_cur</c> into the stage name shown on the printer screen.</summary>
    public static string DescribeStage(double? stage) => stage switch
    {
        null => "unknown",
        -1 or 255 => "idle",
        0 => "printing",
        1 => "auto bed leveling",
        2 => "heatbed preheating",
        3 => "sweeping xy mech mode",
        4 => "changing filament",
        5 => "M400 pause",
        6 => "paused: filament runout",
        7 => "heating hotend",
        8 => "calibrating extrusion",
        9 => "scanning bed surface",
        10 => "inspecting first layer",
        11 => "identifying build plate type",
        12 => "calibrating micro lidar",
        13 => "homing toolhead",
        14 => "cleaning nozzle tip",
        15 => "checking extruder temperature",
        16 => "paused: by user",
        17 => "paused: front cover falling",
        18 => "calibrating micro lidar",
        19 => "calibrating extrusion flow",
        20 => "paused: nozzle temperature malfunction",
        21 => "paused: heat bed temperature malfunction",
        22 => "filament unloading",
        23 => "paused: skipped step",
        24 => "filament loading",
        25 => "calibrating motor noise",
        26 => "paused: AMS lost",
        27 => "paused: low speed of heat break fan",
        28 => "paused: chamber temperature control error",
        29 => "cooling chamber",
        30 => "paused: user gcode",
        31 => "motor noise showoff",
        32 => "paused: nozzle filament covered detected",
        33 => "paused: cutter error",
        34 => "paused: first layer error",
        35 => "paused: nozzle clog",
        _ => $"stage-{stage}",
    };

    private static string Guidance(int hmsCount, PrintErrorContext? printError)
    {
        if (hmsCount == 0 && printError is null)
        {
            return "No active HMS alert or clearable print error is reported.";
        }

        if (hmsCount > 0 && printError is not null)
        {
            return "Both HMS alerts and a print_error are active. Resolve every physical cause and follow each HMS wiki link first. Then, if the print_error remains, call bambu_clear_print_error with this exact code and confirmPhysicalCauseResolved=true. Acknowledgement does not repair or clear HMS faults.";
        }

        if (printError is not null)
        {
            return "Resolve the physical cause first. Then call bambu_clear_print_error with this exact errors.printError.code value and confirmPhysicalCauseResolved=true. This acknowledges the current print error; it does not repair the fault or clear unrelated HMS alerts.";
        }

        return "HMS alerts have no generic clear operation. Follow each error's wiki guidance; the printer removes the alert after its underlying condition is resolved.";
    }

    private static long? ReadInt64(JsonNode? node)
    {
        if (node is null) return null;

        if (node is JsonValue value)
        {
            if (value.TryGetValue<long>(out var longValue)) return longValue;
            if (value.TryGetValue<int>(out var intValue)) return intValue;
            if (value.TryGetValue<uint>(out var uintValue)) return uintValue;
            if (value.TryGetValue<double>(out var doubleValue) &&
                doubleValue >= long.MinValue && doubleValue <= long.MaxValue &&
                Math.Truncate(doubleValue) == doubleValue)
            {
                return (long)doubleValue;
            }
            if (value.TryGetValue<string>(out var text))
            {
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }
                if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                    uint.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
                {
                    return hex;
                }
            }
        }

        return null;
    }

    private static double? ReadDouble(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<double>(out var number)) return number;
        return value.TryGetValue<string>(out var text) &&
               double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            ? number
            : null;
    }

    private static string? ReadString(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<string>(out var text)) return text;
        return node.ToJsonString();
    }

}
