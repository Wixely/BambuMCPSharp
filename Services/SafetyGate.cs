using System.Text.Json.Nodes;
using BambuMCPSharp.Configuration;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace BambuMCPSharp.Services;

/// <summary>
/// Every safety decision in one place, so a tool class stays declarative and no mutating
/// path can quietly skip a check.
///
/// Layered like the rest of the family: <see cref="BambuOptions.ReadOnly"/> is the master
/// switch, each risk category has its own gate on top, and hardware caps (temperatures,
/// transfer sizes) clamp under that. Error text always names the exact config key. On top
/// of config, two checks read the live machine: temperature and motion tools are refused
/// while a print is running, whatever the gates say.
/// </summary>
public sealed class SafetyGate
{
    private readonly BambuOptions _options;

    public SafetyGate(IOptions<BambuOptions> options) => _options = options.Value;

    public BambuOptions Options => _options;

    // ---------------------------------------------------------------- master + category gates

    public void EnsureWriteAllowed(string tool)
    {
        if (_options.ReadOnly)
        {
            throw new McpException(
                $"MCP tool '{tool}' is blocked by server configuration. " +
                "Set Bambu:ReadOnly=false to let this server change anything on the printer.");
        }
    }

    /// <summary>Second gate on top of <see cref="EnsureWriteAllowed"/>.</summary>
    public void EnsureAllowed(bool gate, string tool, string configKey, string why)
    {
        EnsureWriteAllowed(tool);
        if (!gate)
        {
            throw new McpException(
                $"MCP tool '{tool}' requires Bambu:{configKey}=true (in addition to Bambu:ReadOnly=false). {why}");
        }
    }

    public void EnsurePrintControl(string tool) => EnsureAllowed(
        _options.AllowPrintControl, tool, "AllowPrintControl",
        "It pauses, resumes, or alters the running print.");

    public void EnsureStopPrint(string tool) => EnsureAllowed(
        _options.AllowStopPrint, tool, "AllowStopPrint",
        "Cancelling a print destroys the job and the material already laid down.");

    public void EnsureStartPrint(string tool) => EnsureAllowed(
        _options.AllowStartPrint, tool, "AllowStartPrint",
        "Starting a print heats and moves an unattended machine.");

    public void EnsureSpeedControl(string tool) => EnsureAllowed(
        _options.AllowSpeedControl, tool, "AllowSpeedControl",
        "It changes how fast the running print goes.");

    public void EnsureTemperatureControl(string tool) => EnsureAllowed(
        _options.AllowTemperatureControl, tool, "AllowTemperatureControl",
        "Manual heater control on an unattended machine.");

    public void EnsureFanControl(string tool) => EnsureAllowed(
        _options.AllowFanControl, tool, "AllowFanControl",
        "It changes cooling, which affects print quality.");

    public void EnsureLightControl(string tool) => EnsureAllowed(
        _options.AllowLightControl, tool, "AllowLightControl",
        "It switches the chamber light.");

    public void EnsureMotionControl(string tool) => EnsureAllowed(
        _options.AllowMotionControl, tool, "AllowMotionControl",
        "Moving axes can crash the head into the bed, the part, or the frame.");

    public void EnsureRawGcode(string tool) => EnsureAllowed(
        _options.AllowRawGcode, tool, "AllowRawGcode",
        "Raw G-code bypasses every other gate on this server.");

    public void EnsureCalibration(string tool) => EnsureAllowed(
        _options.AllowCalibration, tool, "AllowCalibration",
        "Calibration occupies the machine for many minutes and moves everything.");

    public void EnsureFileUpload(string tool) => EnsureAllowed(
        _options.AllowFileUpload, tool, "AllowFileUpload",
        "It writes a file onto the printer's SD card.");

    public void EnsureFileDelete(string tool) => EnsureAllowed(
        _options.AllowFileDelete, tool, "AllowFileDelete",
        "Deleting an SD-card file cannot be undone from here.");

    /// <summary>Feature toggle for a whole tool category.</summary>
    public void EnsureFeature(bool enabled, string tool, string configKey)
    {
        if (!enabled)
        {
            throw new McpException($"MCP tool '{tool}' is disabled. Set Bambu:{configKey}=true to enable it.");
        }
    }

    // ---------------------------------------------------------------- live-machine checks

    /// <summary>
    /// True when the cached state says a job is active. RUNNING and PAUSE both count:
    /// a paused job still owns its temperatures and position.
    /// </summary>
    public static bool IsPrinting(JsonObject state)
    {
        var gcodeState = state["print"]?["gcode_state"]?.GetValue<string>();
        return gcodeState is "RUNNING" or "PAUSE" or "PREPARE" or "SLICING";
    }

    /// <summary>Refuse tools that must never run mid-print (manual temps, homing, jogging).</summary>
    public void EnsureNotPrinting(JsonObject state, string tool)
    {
        if (IsPrinting(state))
        {
            var gcodeState = state["print"]?["gcode_state"]?.GetValue<string>() ?? "?";
            throw new McpException(
                $"MCP tool '{tool}' refused: the printer reports an active job (gcode_state={gcodeState}). " +
                "This operation is only allowed on an idle machine. Pause/stop the print first if you really mean it.");
        }
    }

    // ---------------------------------------------------------------- clamps

    public int ClampNozzleTemp(int requested, string tool)
    {
        if (requested < 0) requested = 0;
        if (requested > _options.MaxNozzleTempC)
        {
            throw new McpException(
                $"MCP tool '{tool}' refused: {requested} °C exceeds Bambu:MaxNozzleTempC={_options.MaxNozzleTempC}.");
        }
        return requested;
    }

    public int ClampBedTemp(int requested, string tool)
    {
        if (requested < 0) requested = 0;
        if (requested > _options.MaxBedTempC)
        {
            throw new McpException(
                $"MCP tool '{tool}' refused: {requested} °C exceeds Bambu:MaxBedTempC={_options.MaxBedTempC}.");
        }
        return requested;
    }

    /// <summary>Fan percentage (0–100) to the 0–255 PWM value M106 expects.</summary>
    public static int FanPercentToPwm(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        return (int)Math.Round(percent * 255.0 / 100.0);
    }

    public int ClampLimit(int? requested)
    {
        var limit = requested ?? _options.MaxItems;
        if (limit <= 0) limit = _options.MaxItems;
        return Math.Min(limit, _options.MaxItems);
    }
}
