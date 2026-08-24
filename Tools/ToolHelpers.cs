using System.Text.Json;
using System.Text.Json.Nodes;
using BambuMCPSharp.Services;
using ModelContextProtocol;

namespace BambuMCPSharp.Tools;

/// <summary>Shared result shaping for the tool layer.</summary>
internal static class ToolHelpers
{
    public static string Json(SafetyGate gate, object value) =>
        Cap(gate, JsonSerializer.Serialize(value, JsonOpts.Default));

    public static string Json(SafetyGate gate, JsonNode? node) =>
        Cap(gate, node?.ToJsonString(JsonOpts.Default) ?? "null");

    /// <summary>
    /// Apply the size ceiling without handing back malformed JSON. An oversized payload is
    /// wrapped in a small valid envelope carrying the prefix as a string.
    /// </summary>
    private static string Cap(SafetyGate gate, string json)
    {
        var limit = gate.Options.MaxJsonChars;
        if (limit <= 0 || json.Length <= limit) return json;

        var room = Math.Max(0, limit - 400);

        return JsonSerializer.Serialize(new
        {
            truncated = true,
            originalChars = json.Length,
            maxChars = limit,
            hint = "Payload exceeded Bambu:MaxJsonChars. `partial` holds the leading fragment as text, "
                 + "not parseable JSON. Prefer bambu_status over bambu_status_raw, or raise Bambu:MaxJsonChars.",
            partial = json[..room],
        }, JsonOpts.Default);
    }

    /// <summary>Standard command-outcome envelope for every mutating tool.</summary>
    public static string CommandJson(SafetyGate gate, string action, CommandResult result, object? extra = null)
    {
        var node = new JsonObject
        {
            ["action"] = action,
            ["outcome"] = result.Outcome,
            ["acknowledged"] = result.Acknowledged,
            ["sequenceId"] = result.SequenceId,
        };
        if (result.Ack is not null)
        {
            node["ack"] = new JsonObject
            {
                ["result"] = result.Ack["result"]?.DeepClone(),
                ["reason"] = result.Ack["reason"]?.DeepClone(),
            };
        }
        if (extra is not null)
        {
            node["detail"] = JsonSerializer.SerializeToNode(extra, JsonOpts.Default);
        }
        return Json(gate, node);
    }

    /// <summary>
    /// Resolve a file name inside one of the server's confined local directories
    /// (transfers / snapshots). Rejects anything that would escape it: the agent names
    /// files, never paths.
    /// </summary>
    public static string ResolveLocalFile(string contentRoot, string configuredDir, string fileName, string tool)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new McpException($"MCP tool '{tool}' needs a file name.");
        }

        var baseDir = Path.IsPathRooted(configuredDir)
            ? configuredDir
            : Path.Combine(contentRoot, configuredDir);
        Directory.CreateDirectory(baseDir);

        var combined = Path.GetFullPath(Path.Combine(baseDir, fileName));
        var root = Path.GetFullPath(baseDir);
        if (!combined.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(combined, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new McpException(
                $"MCP tool '{tool}' refused: '{fileName}' escapes the configured directory '{configuredDir}'. " +
                "Use a plain file name (subfolders inside the directory are fine).");
        }

        return combined;
    }

    // ---------------------------------------------------------------- report field readers
    // The firmware is inconsistent about numeric types (temperatures arrive as numbers,
    // fan gears as strings), so read defensively and normalize.

    public static double? Num(JsonNode? node)
    {
        if (node is null) return null;
        try
        {
            return node.GetValueKind() switch
            {
                JsonValueKind.Number => node.GetValue<double>(),
                JsonValueKind.String when double.TryParse(node.GetValue<string>(), out var d) => d,
                _ => null,
            };
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public static string? Str(JsonNode? node)
    {
        if (node is null) return null;
        try
        {
            return node.GetValueKind() switch
            {
                JsonValueKind.String => node.GetValue<string>(),
                JsonValueKind.Number => node.ToJsonString(),
                _ => null,
            };
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Bambu fan speeds are 0–15 "gears"; report them as a percentage too.</summary>
    public static int? FanGearToPercent(JsonNode? node)
    {
        var gear = Num(node);
        return gear is null ? null : (int)Math.Round(Math.Clamp(gear.Value, 0, 15) / 15.0 * 100.0);
    }

    public static string SpeedLevelName(double? level) => level switch
    {
        1 => "silent",
        2 => "standard",
        3 => "sport",
        4 => "ludicrous",
        _ => level is null ? "unknown" : $"level-{level}",
    };

    /// <summary>Decode <c>print.stg_cur</c> into the stage name shown on the printer screen.</summary>
    public static string StageName(double? stage) => stage switch
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
}
