using System.ComponentModel;
using System.Text.Json.Nodes;
using BambuMCPSharp.Services;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace BambuMCPSharp.Tools;

/// <summary>
/// Everything that changes the machine's behaviour: job control, speed, temperatures,
/// fans, light, motion, calibration, and the raw G-code escape hatch. Every tool here is
/// gated; see the SafetyGate for the layering.
/// </summary>
[McpServerToolType]
public static class ControlTools
{
    [McpServerTool(Name = "bambu_pause_print"),
     Description("Pause the running print. Reversible with bambu_resume_print. Requires Bambu:ReadOnly=false and Bambu:AllowPrintControl=true.")]
    public static async Task<string> PausePrint(
        PrinterRegistry registry,
        SafetyGate gate,
        [Description("Printer alias. Omit to use the default printer.")] string? alias = null,
        CancellationToken ct = default)
    {
        gate.EnsureFeature(gate.Options.EnableControl, "bambu_pause_print", "EnableControl");
        gate.EnsurePrintControl("bambu_pause_print");
        var connection = registry.Get(alias);
        var result = await connection.SendAsync("print", new JsonObject { ["command"] = "pause" }, ct);
        return ToolHelpers.CommandJson(gate, "pause", result);
    }

    [McpServerTool(Name = "bambu_resume_print"),
     Description("Resume a paused print. Requires Bambu:ReadOnly=false and Bambu:AllowPrintControl=true.")]
    public static async Task<string> ResumePrint(
        PrinterRegistry registry,
        SafetyGate gate,
        [Description("Printer alias. Omit to use the default printer.")] string? alias = null,
        CancellationToken ct = default)
    {
        gate.EnsureFeature(gate.Options.EnableControl, "bambu_resume_print", "EnableControl");
        gate.EnsurePrintControl("bambu_resume_print");
        var connection = registry.Get(alias);
        var result = await connection.SendAsync("print", new JsonObject { ["command"] = "resume" }, ct);
        return ToolHelpers.CommandJson(gate, "resume", result);
    }

    [McpServerTool(Name = "bambu_clear_print_error"),
     Description("Acknowledge the printer's current print_error after the physical cause is resolved. Requires the exact printError value returned by bambu_diagnostics, confirmPhysicalCauseResolved=true, Bambu:ReadOnly=false, and Bambu:AllowErrorClear=true (off by default). This does not repair faults, clear unrelated HMS alerts, or blindly clear all errors.")]
    public static async Task<string> ClearPrintError(
        PrinterRegistry registry,
        SafetyGate gate,
        [Description("Exact decimal printError value currently returned by bambu_diagnostics. The command is refused if it changed.")] long expectedPrintError,
        [Description("Must be true to confirm that a person has resolved the error's physical cause.")] bool confirmPhysicalCauseResolved,
        [Description("Printer alias. Omit to use the default printer.")] string? alias = null,
        CancellationToken ct = default)
    {
        gate.EnsureFeature(gate.Options.EnableControl, "bambu_clear_print_error", "EnableControl");
        gate.EnsureErrorClear("bambu_clear_print_error");
        if (!confirmPhysicalCauseResolved)
        {
            throw new McpException(
                "MCP tool 'bambu_clear_print_error' refused: confirmPhysicalCauseResolved must be true. " +
                "Diagnose and resolve the physical cause before acknowledging the printer error.");
        }

        var connection = registry.Get(alias);
        var (state, _) = await connection.GetStateAsync(ct);
        var current = PrinterDiagnostics.CurrentPrintError(state);
        if (current is null)
        {
            throw new McpException(
                "MCP tool 'bambu_clear_print_error' refused: the printer reports no active print_error. " +
                "Run bambu_diagnostics again before retrying.");
        }
        if (current.Code != expectedPrintError)
        {
            throw new McpException(
                $"MCP tool 'bambu_clear_print_error' refused: expected print_error {expectedPrintError} " +
                $"but the printer now reports {current.Code}. Run bambu_diagnostics again; do not clear a changed error blindly.");
        }

        var result = await connection.SendAsync(
            "print",
            PrinterDiagnostics.CreateClearPrintErrorCommand(current),
            ct);
        return ToolHelpers.CommandJson(gate, "clear_print_error", result, new
        {
            printError = current.Code,
            printErrorHex = current.HexCode,
            note = "Acknowledgement sent. Verify with bambu_diagnostics; an unresolved physical fault may reappear or remain active.",
        });
    }

    [McpServerTool(Name = "bambu_stop_print"),
     Description("Cancel the running print entirely. NOT reversible — the job and the material already printed are lost. Requires Bambu:ReadOnly=false and Bambu:AllowStopPrint=true (off by default).")]
    public static async Task<string> StopPrint(
        PrinterRegistry registry,
        SafetyGate gate,
        [Description("Printer alias. Omit to use the default printer.")] string? alias = null,
        CancellationToken ct = default)
    {
        gate.EnsureFeature(gate.Options.EnableControl, "bambu_stop_print", "EnableControl");
        gate.EnsureStopPrint("bambu_stop_print");
        var connection = registry.Get(alias);
        var result = await connection.SendAsync("print", new JsonObject { ["command"] = "stop" }, ct);
        return ToolHelpers.CommandJson(gate, "stop", result);
    }

    [McpServerTool(Name = "bambu_skip_objects"),
     Description("Use the X1/X1C mid-print Skip Parts feature for named objects from a locally inspected sliced project. Intended for an agent that has identified one failing part with bambu_camera_snapshot: pause first, map the visible part unambiguously to an identify_id from bambu_inspect_project, skip it, visually re-check, then resume. Requires a matching running/paused job, safe unique metadata, at least one unskipped part left afterward, Bambu:ReadOnly=false, and Bambu:AllowPrintControl=true. Returns bounded verification from print.s_obj.")]
    public static async Task<string> SkipObjects(
        PrinterRegistry registry,
        SafetyGate gate,
        IHostEnvironment env,
        [Description("Object identify_id values returned under the selected plate by bambu_inspect_project.")] int[] objectIds,
        [Description("Name of the matching sliced .gcode.3mf/.3mf file inside the configured transfer directory.")] string localName,
        [Description("One-based plate number inside the sliced project. Default 1.")] int plate = 1,
        [Description("Printer alias. Omit to use the default printer.")] string? alias = null,
        CancellationToken ct = default)
    {
        gate.EnsureFeature(gate.Options.EnableControl, "bambu_skip_objects", "EnableControl");
        gate.EnsurePrintControl("bambu_skip_objects");
        if (plate is < 1 or > ProjectInspectionLimits.MaxPlates)
        {
            throw new McpException($"bambu_skip_objects plate must be between 1 and {ProjectInspectionLimits.MaxPlates}.");
        }

        var printer = registry.ResolveAlias(alias);
        var localPath = ToolHelpers.ResolveLocalFile(
            env.ContentRootPath, gate.Options.FileTransferDirectory, localName, "bambu_skip_objects");
        if (!File.Exists(localPath))
        {
            throw new McpException(
                $"'{localName}' was not found in the transfer directory ({gate.Options.FileTransferDirectory}).");
        }

        ProjectInspection inspection;
        try
        {
            inspection = await ProjectInspector.InspectAsync(
                localPath,
                printer.Model,
                new ProjectInspectionLimits(
                    gate.Options.MaxProjectInspectBytes,
                    gate.Options.MaxProjectArchiveEntries,
                    gate.Options.MaxProjectUncompressedBytes),
                ct);
        }
        catch (InvalidDataException exception)
        {
            throw new McpException($"bambu_skip_objects refused project '{localName}': {exception.Message}");
        }
        catch (IOException)
        {
            throw new McpException($"bambu_skip_objects could not safely read project '{localName}'.");
        }

        var selectedPlate = inspection.Plates.SingleOrDefault(item => item.Plate == plate);
        if (selectedPlate is null)
        {
            throw new McpException($"bambu_skip_objects refused: project '{localName}' has no plate {plate}.");
        }

        var connection = registry.Get(alias);
        var (state, beforeReportedUtc) = await connection.GetStateAsync(ct);
        SkipPartsPlan plan;
        try
        {
            plan = SkipPartsWorkflow.CreatePlan(state, selectedPlate, localName, objectIds ?? []);
        }
        catch (InvalidOperationException exception)
        {
            throw new McpException($"bambu_skip_objects refused: {exception.Message}");
        }

        var result = await connection.SendAsync("print", plan.Command, ct);
        var (verifiedState, verifiedReportedUtc) = await connection.GetStateAsync(ct, forceRefresh: true);
        var verification = SkipPartsWorkflow.Verify(verifiedState, plan);
        if (verifiedReportedUtc is null ||
            (beforeReportedUtc is not null && verifiedReportedUtc <= beforeReportedUtc))
        {
            verification = verification with { Outcome = "state_not_refreshed" };
        }

        return ToolHelpers.CommandJson(gate, "skip_objects", result, new
        {
            project = inspection.FileName,
            inspection.Sha256,
            plate,
            requestedParts = plan.RequestedParts.Select(part => new { identifyId = part.IdentifyId, name = part.Name }),
            alreadySkippedParts = plan.AlreadySkippedParts.Select(part => new { identifyId = part.IdentifyId, name = part.Name }),
            remainingParts = plan.RemainingPartsAfterRequest.Select(part => new { identifyId = part.IdentifyId, name = part.Name }),
            verification = new
            {
                verification.Outcome,
                verification.ReportedSkippedObjectIds,
                verification.MissingRequestedObjectIds,
                reportedUtc = verifiedReportedUtc,
            },
        });
    }

    [McpServerTool(Name = "bambu_set_print_speed"),
     Description("Set the print speed level: 1=silent, 2=standard, 3=sport, 4=ludicrous. Applies to the running print. Requires Bambu:AllowSpeedControl=true.")]
    public static async Task<string> SetPrintSpeed(
        PrinterRegistry registry,
        SafetyGate gate,
        [Description("Speed level 1-4 (1 silent, 2 standard, 3 sport, 4 ludicrous).")] int level,
        [Description("Printer alias. Omit to use the default printer.")] string? alias = null,
        CancellationToken ct = default)
    {
        gate.EnsureFeature(gate.Options.EnableControl, "bambu_set_print_speed", "EnableControl");
        gate.EnsureSpeedControl("bambu_set_print_speed");
        if (level is < 1 or > 4)
        {
            throw new McpException("Speed level must be 1 (silent), 2 (standard), 3 (sport), or 4 (ludicrous).");
        }

        var connection = registry.Get(alias);
        var result = await connection.SendAsync("print", new JsonObject
        {
            ["command"] = "print_speed",
            ["param"] = level.ToString(),
        }, ct);
        return ToolHelpers.CommandJson(gate, "print_speed", result, new { level, name = ToolHelpers.SpeedLevelName(level) });
    }

    [McpServerTool(Name = "bambu_set_nozzle_temp"),
     Description("Set the nozzle target temperature (G-code M104). Clamped to Bambu:MaxNozzleTempC; 0 turns the heater off. Refused while a print is running — the job owns its temperatures. Requires Bambu:AllowTemperatureControl=true (off by default).")]
    public static async Task<string> SetNozzleTemp(
        PrinterRegistry registry,
        SafetyGate gate,
        [Description("Target temperature in °C. 0 = heater off.")] int temperatureC,
        [Description("Printer alias. Omit to use the default printer.")] string? alias = null,
        CancellationToken ct = default)
    {
        gate.EnsureFeature(gate.Options.EnableControl, "bambu_set_nozzle_temp", "EnableControl");
        gate.EnsureTemperatureControl("bambu_set_nozzle_temp");
        var clamped = gate.ClampNozzleTemp(temperatureC, "bambu_set_nozzle_temp");

        var connection = registry.Get(alias);
        var (state, _) = await connection.GetStateAsync(ct);
        gate.EnsureNotPrinting(state, "bambu_set_nozzle_temp");

        var result = await SendGcodeAsync(connection, $"M104 S{clamped}", ct);
        return ToolHelpers.CommandJson(gate, "set_nozzle_temp", result, new { temperatureC = clamped });
    }

    [McpServerTool(Name = "bambu_set_bed_temp"),
     Description("Set the heatbed target temperature (G-code M140). Clamped to Bambu:MaxBedTempC; 0 turns the heater off. Refused while a print is running. Requires Bambu:AllowTemperatureControl=true (off by default).")]
    public static async Task<string> SetBedTemp(
        PrinterRegistry registry,
        SafetyGate gate,
        [Description("Target temperature in °C. 0 = heater off.")] int temperatureC,
        [Description("Printer alias. Omit to use the default printer.")] string? alias = null,
        CancellationToken ct = default)
    {
        gate.EnsureFeature(gate.Options.EnableControl, "bambu_set_bed_temp", "EnableControl");
        gate.EnsureTemperatureControl("bambu_set_bed_temp");
        var clamped = gate.ClampBedTemp(temperatureC, "bambu_set_bed_temp");

        var connection = registry.Get(alias);
        var (state, _) = await connection.GetStateAsync(ct);
        gate.EnsureNotPrinting(state, "bambu_set_bed_temp");

        var result = await SendGcodeAsync(connection, $"M140 S{clamped}", ct);
        return ToolHelpers.CommandJson(gate, "set_bed_temp", result, new { temperatureC = clamped });
    }

    [McpServerTool(Name = "bambu_set_part_fan"),
     Description("Set the part cooling fan speed as a percentage (G-code M106 P1). Requires Bambu:AllowFanControl=true. Note: while printing, the job's own fan commands will override this on the next layer change.")]
    public static Task<string> SetPartFan(
        PrinterRegistry registry,
        SafetyGate gate,
        [Description("Fan speed 0-100 percent.")] int percent,
        [Description("Printer alias. Omit to use the default printer.")] string? alias = null,
        CancellationToken ct = default)
        => SetFanAsync(registry, gate, "bambu_set_part_fan", 1, "part", percent, alias, ct);

    [McpServerTool(Name = "bambu_set_aux_fan"),
     Description("Set the auxiliary (side) fan speed as a percentage (G-code M106 P2). Requires Bambu:AllowFanControl=true.")]
    public static Task<string> SetAuxFan(
        PrinterRegistry registry,
        SafetyGate gate,
        [Description("Fan speed 0-100 percent.")] int percent,
        [Description("Printer alias. Omit to use the default printer.")] string? alias = null,
        CancellationToken ct = default)
        => SetFanAsync(registry, gate, "bambu_set_aux_fan", 2, "auxiliary", percent, alias, ct);

    [McpServerTool(Name = "bambu_set_chamber_fan"),
     Description("Set the chamber exhaust fan speed as a percentage (G-code M106 P3). Requires Bambu:AllowFanControl=true.")]
    public static Task<string> SetChamberFan(
        PrinterRegistry registry,
        SafetyGate gate,
        [Description("Fan speed 0-100 percent.")] int percent,
        [Description("Printer alias. Omit to use the default printer.")] string? alias = null,
        CancellationToken ct = default)
        => SetFanAsync(registry, gate, "bambu_set_chamber_fan", 3, "chamber", percent, alias, ct);

    private static async Task<string> SetFanAsync(
        PrinterRegistry registry, SafetyGate gate, string tool, int fanIndex, string fanName,
        int percent, string? alias, CancellationToken ct)
    {
        gate.EnsureFeature(gate.Options.EnableControl, tool, "EnableControl");
        gate.EnsureFanControl(tool);

        var pwm = SafetyGate.FanPercentToPwm(percent);
        var connection = registry.Get(alias);
        var result = await SendGcodeAsync(connection, $"M106 P{fanIndex} S{pwm}", ct);
        return ToolHelpers.CommandJson(gate, $"set_{fanName}_fan", result, new { percent = Math.Clamp(percent, 0, 100), pwm });
    }

    [McpServerTool(Name = "bambu_set_chamber_light"),
     Description("Turn the chamber light on or off. A camera watch-loop needs it on. Requires Bambu:AllowLightControl=true.")]
    public static async Task<string> SetChamberLight(
        PrinterRegistry registry,
        SafetyGate gate,
        [Description("true = on, false = off.")] bool on,
        [Description("Printer alias. Omit to use the default printer.")] string? alias = null,
        CancellationToken ct = default)
    {
        gate.EnsureFeature(gate.Options.EnableControl, "bambu_set_chamber_light", "EnableControl");
        gate.EnsureLightControl("bambu_set_chamber_light");

        var connection = registry.Get(alias);
        var result = await connection.SendAsync("system", new JsonObject
        {
            ["command"] = "ledctrl",
            ["led_node"] = "chamber_light",
            ["led_mode"] = on ? "on" : "off",
            ["led_on_time"] = 500,
            ["led_off_time"] = 500,
            ["loop_times"] = 0,
            ["interval_time"] = 0,
        }, ct);
        return ToolHelpers.CommandJson(gate, "set_chamber_light", result, new { on });
    }

    [McpServerTool(Name = "bambu_home_axes"),
     Description("Home all axes (G-code G28). Refused while a print is running. Requires Bambu:AllowMotionControl=true (off by default).")]
    public static async Task<string> HomeAxes(
        PrinterRegistry registry,
        SafetyGate gate,
        [Description("Printer alias. Omit to use the default printer.")] string? alias = null,
        CancellationToken ct = default)
    {
        gate.EnsureFeature(gate.Options.EnableControl, "bambu_home_axes", "EnableControl");
        gate.EnsureMotionControl("bambu_home_axes");

        var connection = registry.Get(alias);
        var (state, _) = await connection.GetStateAsync(ct);
        gate.EnsureNotPrinting(state, "bambu_home_axes");

        var result = await SendGcodeAsync(connection, "G28", ct);
        return ToolHelpers.CommandJson(gate, "home_axes", result);
    }

    [McpServerTool(Name = "bambu_jog"),
     Description("Jog an axis by a relative distance in mm (G-code G91/G1). Home first for a known position. Refused while a print is running. Requires Bambu:AllowMotionControl=true (off by default).")]
    public static async Task<string> Jog(
        PrinterRegistry registry,
        SafetyGate gate,
        [Description("Axis to move: X, Y, or Z.")] string axis,
        [Description("Relative distance in millimetres. Negative moves the other way. Clamped to ±100.")] double distanceMm,
        [Description("Feed rate in mm/min. Default 3000 for X/Y; Z is forced to 900 max.")] int? feedRate = null,
        [Description("Printer alias. Omit to use the default printer.")] string? alias = null,
        CancellationToken ct = default)
    {
        gate.EnsureFeature(gate.Options.EnableControl, "bambu_jog", "EnableControl");
        gate.EnsureMotionControl("bambu_jog");

        var normalizedAxis = (axis ?? string.Empty).Trim().ToUpperInvariant();
        if (normalizedAxis is not ("X" or "Y" or "Z"))
        {
            throw new McpException("Axis must be X, Y, or Z.");
        }

        var distance = Math.Clamp(distanceMm, -100, 100);
        var feed = feedRate ?? 3000;
        if (normalizedAxis == "Z") feed = Math.Min(feed, 900);
        feed = Math.Clamp(feed, 60, 12000);

        var connection = registry.Get(alias);
        var (state, _) = await connection.GetStateAsync(ct);
        gate.EnsureNotPrinting(state, "bambu_jog");

        var gcode = string.Join('\n', "G91", $"G1 {normalizedAxis}{distance:0.###} F{feed}", "G90");
        var result = await SendGcodeAsync(connection, gcode, ct);
        return ToolHelpers.CommandJson(gate, "jog", result, new { axis = normalizedAxis, distanceMm = distance, feedRate = feed });
    }

    [McpServerTool(Name = "bambu_send_gcode"),
     Description("Send raw G-code line(s) to the printer. The unrestricted escape hatch — it bypasses every other gate's semantics, so it requires Bambu:AllowRawGcode=true (off by default). Multiple lines separated by \\n.")]
    public static async Task<string> SendGcode(
        PrinterRegistry registry,
        SafetyGate gate,
        [Description("G-code to execute. Multiple lines allowed, separated by newlines.")] string gcode,
        [Description("Printer alias. Omit to use the default printer.")] string? alias = null,
        CancellationToken ct = default)
    {
        gate.EnsureFeature(gate.Options.EnableControl, "bambu_send_gcode", "EnableControl");
        gate.EnsureRawGcode("bambu_send_gcode");

        if (string.IsNullOrWhiteSpace(gcode))
        {
            throw new McpException("bambu_send_gcode needs at least one G-code line.");
        }
        if (gcode.Length > gate.Options.MaxGcodeChars)
        {
            throw new McpException(
                $"G-code exceeds Bambu:MaxGcodeChars={gate.Options.MaxGcodeChars}. " +
                "For anything that big, upload a file and print it instead.");
        }

        var connection = registry.Get(alias);
        var result = await SendGcodeAsync(connection, gcode, ct);
        return ToolHelpers.CommandJson(gate, "send_gcode", result, new { lines = gcode.Split('\n').Length });
    }

    [McpServerTool(Name = "bambu_run_calibration"),
     Description("Run the printer's calibration routine (as on first setup: bed level, vibration compensation, and/or flow). Occupies the machine for many minutes. Requires Bambu:AllowCalibration=true (off by default).")]
    public static async Task<string> RunCalibration(
        PrinterRegistry registry,
        SafetyGate gate,
        [Description("Include automatic bed levelling.")] bool bedLevelling = true,
        [Description("Include vibration compensation (X1C accelerometer sweep).")] bool vibrationCompensation = true,
        [Description("Include motor noise cancellation.")] bool motorNoise = false,
        [Description("Printer alias. Omit to use the default printer.")] string? alias = null,
        CancellationToken ct = default)
    {
        gate.EnsureFeature(gate.Options.EnableControl, "bambu_run_calibration", "EnableControl");
        gate.EnsureCalibration("bambu_run_calibration");

        var connection = registry.Get(alias);
        var (state, _) = await connection.GetStateAsync(ct);
        gate.EnsureNotPrinting(state, "bambu_run_calibration");

        // Bitmask per Bambu's firmware: 1 = bed level, 2 = vibration, 4 = motor noise.
        var option = (bedLevelling ? 1 : 0) | (vibrationCompensation ? 2 : 0) | (motorNoise ? 4 : 0);
        if (option == 0)
        {
            throw new McpException("Select at least one calibration: bedLevelling, vibrationCompensation, or motorNoise.");
        }

        var result = await connection.SendAsync("print", new JsonObject
        {
            ["command"] = "calibration",
            ["option"] = option,
        }, ct);
        return ToolHelpers.CommandJson(gate, "calibration", result, new { bedLevelling, vibrationCompensation, motorNoise });
    }

    private static Task<CommandResult> SendGcodeAsync(PrinterConnection connection, string gcode, CancellationToken ct)
    {
        // The firmware wants a trailing newline on gcode_line params.
        var param = gcode.EndsWith('\n') ? gcode : gcode + "\n";
        return connection.SendAsync("print", new JsonObject
        {
            ["command"] = "gcode_line",
            ["param"] = param,
        }, ct);
    }
}
