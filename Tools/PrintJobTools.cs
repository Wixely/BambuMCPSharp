using System.ComponentModel;
using System.Text.Json.Nodes;
using BambuMCPSharp.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace BambuMCPSharp.Tools;

/// <summary>
/// Starting prints from files already on the SD card. Slicing happens elsewhere; the
/// hand-off is a .3mf uploaded via bambu_upload_file (or dropped onto the card by the
/// slicing system) and started here.
/// </summary>
[McpServerToolType]
public static class PrintJobTools
{
    [McpServerTool(Name = "bambu_start_print"),
     Description("Start printing a sliced .3mf file that is already on the printer's SD card (see bambu_list_files; upload with bambu_upload_file). Heats and moves the machine. Requires Bambu:ReadOnly=false and Bambu:AllowStartPrint=true (off by default).")]
    public static async Task<string> StartPrint(
        PrinterRegistry registry,
        SafetyGate gate,
        [Description("Path of the .3mf on the SD card, e.g. '/model.3mf' or '/cache/model.3mf'.")] string file,
        [Description("Plate number inside the .3mf to print. Default 1.")] int plate = 1,
        [Description("Use the AMS for filament. Default false (external spool).")] bool useAms = false,
        [Description("AMS tray mapping, e.g. [0] maps the model's first filament to tray 0. Empty = firmware default.")] int[]? amsMapping = null,
        [Description("Run automatic bed levelling before the print. Default true.")] bool bedLevelling = true,
        [Description("Run flow calibration before the print. Default true.")] bool flowCalibration = true,
        [Description("Run vibration calibration before the print. Default true.")] bool vibrationCalibration = true,
        [Description("Record a timelapse of the print. Default false.")] bool timelapse = false,
        [Description("Enable first-layer AI inspection. Default true.")] bool layerInspect = true,
        [Description("Printer alias. Omit to use the default printer.")] string? alias = null,
        CancellationToken ct = default)
    {
        gate.EnsureFeature(gate.Options.EnablePrintJobs, "bambu_start_print", "EnablePrintJobs");
        gate.EnsureStartPrint("bambu_start_print");

        if (string.IsNullOrWhiteSpace(file))
        {
            throw new McpException("bambu_start_print needs the SD-card path of a .3mf file.");
        }
        if (plate < 1) plate = 1;

        var connection = registry.Get(alias);
        var (state, _) = await connection.GetStateAsync(ct);
        if (SafetyGate.IsPrinting(state))
        {
            var gcodeState = state["print"]?["gcode_state"]?.GetValue<string>() ?? "?";
            throw new McpException(
                $"bambu_start_print refused: a job is already active (gcode_state={gcodeState}). " +
                "Stop or finish it first.");
        }

        var remote = BambuFtp.NormalizeRemotePath(file);
        var body = new JsonObject
        {
            ["command"] = "project_file",
            // The param names the entry inside the .3mf; the url names the file itself.
            ["param"] = $"Metadata/plate_{plate}.gcode",
            ["url"] = $"file:///sdcard{remote}",
            ["subtask_name"] = Path.GetFileNameWithoutExtension(remote),
            ["project_id"] = "0",
            ["profile_id"] = "0",
            ["task_id"] = "0",
            ["subtask_id"] = "0",
            ["timelapse"] = timelapse,
            ["bed_leveling"] = bedLevelling,
            ["flow_cali"] = flowCalibration,
            ["vibration_cali"] = vibrationCalibration,
            ["layer_inspect"] = layerInspect,
            ["use_ams"] = useAms,
            ["bed_type"] = "auto",
        };

        if (useAms && amsMapping is { Length: > 0 })
        {
            body["ams_mapping"] = new JsonArray(amsMapping.Select(t => (JsonNode)t).ToArray());
        }

        var result = await connection.SendAsync("print", body, ct);
        return ToolHelpers.CommandJson(gate, "start_print", result, new
        {
            file = remote,
            plate,
            useAms,
            hint = "Poll bambu_status: gcode_state should move to PREPARE then RUNNING. " +
                   "If it stays IDLE, check the ack reason and that the file path is exact (case-sensitive).",
        });
    }
}
