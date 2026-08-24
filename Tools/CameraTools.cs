using System.ComponentModel;
using System.Text.Json;
using BambuMCPSharp.Services;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace BambuMCPSharp.Tools;

/// <summary>
/// Chamber camera snapshots from the X1C's RTSPS stream, decoded to PNG entirely in
/// managed code. Built for computer-vision loops: capture, hand the PNG to a vision model,
/// react with the control tools.
/// </summary>
[McpServerToolType]
public static class CameraTools
{
    [McpServerTool(Name = "bambu_camera_snapshot"),
     Description("Capture one still frame (PNG) from the printer's chamber camera. Returns the image for a vision model to inspect, and/or saves it into the snapshot directory (Bambu:SnapshotDirectory). Needs 'LAN Mode Liveview' enabled on the printer. Tip: bambu_set_chamber_light on first — the chamber is dark.")]
    public static async Task<CallToolResult> Snapshot(
        PrinterRegistry registry,
        SafetyGate gate,
        CameraService camera,
        IHostEnvironment env,
        [Description("Return the PNG as image content in the response. Default true.")] bool returnImage = true,
        [Description("Also save the PNG into the snapshot directory. Default false.")] bool save = false,
        [Description("File name to save as (only with save=true). Default: <alias>-<timestamp>.png.")] string? saveName = null,
        [Description("Printer alias. Omit to use the default printer.")] string? alias = null,
        CancellationToken ct = default)
    {
        gate.EnsureFeature(gate.Options.EnableCamera, "bambu_camera_snapshot", "EnableCamera");
        var printer = registry.ResolveAlias(alias);

        if (!returnImage && !save)
        {
            throw new McpException("bambu_camera_snapshot: set returnImage=true, save=true, or both — otherwise there is nothing to do.");
        }

        var snapshot = await camera.SnapshotAsync(printer, ct);

        string? savedPath = null;
        if (save)
        {
            var fileName = string.IsNullOrWhiteSpace(saveName)
                ? $"{printer.Alias}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.png"
                : saveName.Trim();
            if (!fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) fileName += ".png";

            savedPath = ToolHelpers.ResolveLocalFile(
                env.ContentRootPath, gate.Options.SnapshotDirectory, fileName, "bambu_camera_snapshot");
            await File.WriteAllBytesAsync(savedPath, snapshot.PngData, ct);
        }

        var meta = JsonSerializer.Serialize(new
        {
            printer = printer.Alias,
            width = snapshot.Width,
            height = snapshot.Height,
            bytes = snapshot.PngData.Length,
            captureSeconds = Math.Round(snapshot.CaptureDuration.TotalSeconds, 2),
            savedPath,
            capturedUtc = DateTimeOffset.UtcNow,
        }, JsonOpts.Default);

        var content = new List<ContentBlock> { new TextContentBlock { Text = meta } };
        if (returnImage)
        {
            content.Add(new ImageContentBlock
            {
                Data = snapshot.PngData,
                MimeType = "image/png",
            });
        }

        return new CallToolResult { Content = content };
    }

    [McpServerTool(Name = "bambu_camera_check"),
     Description("Probe the camera path without returning pixels: connect over RTSPS, decode the first keyframe, and report resolution and timing. Use it to verify 'LAN Mode Liveview' is working before wiring up a vision loop.")]
    public static async Task<string> Check(
        PrinterRegistry registry,
        SafetyGate gate,
        CameraService camera,
        [Description("Printer alias. Omit to use the default printer.")] string? alias = null,
        CancellationToken ct = default)
    {
        gate.EnsureFeature(gate.Options.EnableCamera, "bambu_camera_check", "EnableCamera");
        var printer = registry.ResolveAlias(alias);

        var snapshot = await camera.SnapshotAsync(printer, ct);
        return ToolHelpers.Json(gate, new
        {
            printer = printer.Alias,
            ok = true,
            width = snapshot.Width,
            height = snapshot.Height,
            pngBytes = snapshot.PngData.Length,
            captureSeconds = Math.Round(snapshot.CaptureDuration.TotalSeconds, 2),
            endpoint = $"rtsps://{printer.Host}:{printer.CameraPort}/streaming/live/1",
        });
    }
}
