namespace BambuMCPSharp.Configuration;

/// <summary>
/// Domain options for the Bambu Lab MCP server. Points the server at one or more printers
/// in offline LAN mode, sets the layered safety gates that decide what the agent may do to
/// a machine with heaters and motors, and caps payload and transfer sizes.
///
/// The gates are layered rather than a single switch: this server can cancel a 20-hour
/// print, heat a nozzle to 300 °C, and move axes on an unattended machine, so
/// <see cref="ReadOnly"/>=false on its own must never unlock the dangerous tail.
/// </summary>
public sealed class BambuOptions
{
    public const string SectionName = "Bambu";

    // ---------------------------------------------------------------- printers

    /// <summary>Configured printers. Each is addressed by its alias.</summary>
    public List<BambuPrinterEntry> Printers { get; set; } = new();

    /// <summary>
    /// Alias picked when a tool omits <c>alias</c>. Falls back to the first entry in
    /// <see cref="Printers"/> when blank.
    /// </summary>
    public string? DefaultAlias { get; set; }

    // ---------------------------------------------------------------- master gate

    /// <summary>
    /// Master safety switch. When true, every tool that changes anything on the printer
    /// refuses with a clear error naming the config key. Monitoring, file listing/download,
    /// and camera snapshots stay available. Default true — flip it per deployment.
    /// </summary>
    public bool ReadOnly { get; set; } = true;

    // ---------------------------------------------------------------- per-category gates
    // Each additionally requires ReadOnly=false. Defaults follow blast radius: reversible
    // supervision defaults on, anything that wrecks a job or moves/heats hardware defaults off.

    /// <summary>Pause and resume the running print, skip failed objects.</summary>
    public bool AllowPrintControl { get; set; } = true;

    /// <summary>Cancel the running print. Off by default — it destroys the job and the material in it.</summary>
    public bool AllowStopPrint { get; set; } = false;

    /// <summary>Start a print from a file on the SD card. Off by default — it heats and moves an unattended machine.</summary>
    public bool AllowStartPrint { get; set; } = false;

    /// <summary>Change the print speed level (silent/standard/sport/ludicrous).</summary>
    public bool AllowSpeedControl { get; set; } = true;

    /// <summary>
    /// Set nozzle / bed temperatures manually. Off by default, clamped to
    /// <see cref="MaxNozzleTempC"/> / <see cref="MaxBedTempC"/>, and refused while a print
    /// is running — the job owns its temperatures.
    /// </summary>
    public bool AllowTemperatureControl { get; set; } = false;

    /// <summary>Part / auxiliary / chamber fan speeds.</summary>
    public bool AllowFanControl { get; set; } = true;

    /// <summary>Chamber light on/off. A camera watch-loop needs the light on.</summary>
    public bool AllowLightControl { get; set; } = true;

    /// <summary>Home and jog axes. Off by default — a crash risk; always refused while printing.</summary>
    public bool AllowMotionControl { get; set; } = false;

    /// <summary>Send raw G-code lines. Off by default — the unrestricted escape hatch.</summary>
    public bool AllowRawGcode { get; set; } = false;

    /// <summary>Run calibration (bed level / vibration / flow). Off by default — long, and occupies the machine.</summary>
    public bool AllowCalibration { get; set; } = false;

    /// <summary>Upload files to the SD card. On by default — non-destructive, and the hand-off point from the slicing system.</summary>
    public bool AllowFileUpload { get; set; } = true;

    /// <summary>Delete files from the SD card. Off by default.</summary>
    public bool AllowFileDelete { get; set; } = false;

    /// <summary>
    /// Acknowledge the printer's current <c>print_error</c> after its physical cause has
    /// been resolved. Off by default because dismissing an error may allow a paused job to proceed.
    /// </summary>
    public bool AllowErrorClear { get; set; } = false;

    // ---------------------------------------------------------------- feature toggles

    /// <summary>Expose status / monitoring tools.</summary>
    public bool EnableStatus { get; set; } = true;

    /// <summary>Expose control tools.</summary>
    public bool EnableControl { get; set; } = true;

    /// <summary>Expose the print-start tool.</summary>
    public bool EnablePrintJobs { get; set; } = true;

    /// <summary>Expose SD-card file tools.</summary>
    public bool EnableFiles { get; set; } = true;

    /// <summary>Expose camera tools.</summary>
    public bool EnableCamera { get; set; } = true;

    // ---------------------------------------------------------------- caps and clamps

    /// <summary>Ceiling for <c>bambu_set_nozzle_temp</c>. The X1C hotend tops out at 300 °C.</summary>
    public int MaxNozzleTempC { get; set; } = 300;

    /// <summary>Ceiling for <c>bambu_set_bed_temp</c>. The X1C bed tops out at 110 °C.</summary>
    public int MaxBedTempC { get; set; } = 110;

    /// <summary>Cap on bytes uploaded to the printer in one call.</summary>
    public long MaxUploadBytes { get; set; } = 256_000_000;

    /// <summary>Cap on bytes downloaded from the printer in one call.</summary>
    public long MaxDownloadBytes { get; set; } = 256_000_000;

    /// <summary>Cap on characters of any single JSON blob echoed back.</summary>
    public int MaxJsonChars { get; set; } = 60_000;

    /// <summary>Cap on characters of G-code accepted by <c>bambu_send_gcode</c> per call.</summary>
    public int MaxGcodeChars { get; set; } = 2_000;

    /// <summary>Cap on items returned by any single list tool.</summary>
    public int MaxItems { get; set; } = 500;

    /// <summary>Cap on the compressed or standalone file size accepted by project inspection.</summary>
    public long MaxProjectInspectBytes { get; set; } = 256_000_000;

    /// <summary>Cap on ZIP entries examined in one 3MF project.</summary>
    public int MaxProjectArchiveEntries { get; set; } = 2_048;

    /// <summary>Cap on the total expanded bytes declared by a 3MF archive.</summary>
    public long MaxProjectUncompressedBytes { get; set; } = 512_000_000;

    // ---------------------------------------------------------------- timing

    /// <summary>
    /// Age (seconds) at which the cached printer state is considered stale; a tool then
    /// requests a fresh <c>pushall</c> before answering. The X1C pushes roughly every
    /// second while printing, so this mostly matters for an idle machine.
    /// </summary>
    public int StateFreshSeconds { get; set; } = 5;

    /// <summary>Seconds to wait for the printer to echo a command's sequence id before reporting "not acknowledged".</summary>
    public int CommandAckTimeoutSeconds { get; set; } = 10;

    /// <summary>Seconds allowed for the MQTT connect + subscribe handshake.</summary>
    public int MqttConnectTimeoutSeconds { get; set; } = 15;

    /// <summary>Seconds allowed for an RTSPS connect + first decodable keyframe.</summary>
    public int CameraTimeoutSeconds { get; set; } = 30;

    /// <summary>Seconds allowed for any single FTPS operation.</summary>
    public int FtpTimeoutSeconds { get; set; } = 60;

    // ---------------------------------------------------------------- local directories

    /// <summary>
    /// Directory (relative to the content root unless absolute) that file transfers are
    /// confined to. Downloads land here; uploads are read from here. The agent never
    /// names an arbitrary local path.
    /// </summary>
    public string FileTransferDirectory { get; set; } = "transfers";

    /// <summary>Directory camera snapshots are saved into (same resolution rules).</summary>
    public string SnapshotDirectory { get; set; } = "snapshots";
}

/// <summary>One configured printer in LAN mode.</summary>
public sealed class BambuPrinterEntry
{
    /// <summary>
    /// Stable handle the agent uses to pick this printer. When omitted the registry
    /// generates one at load time (<c>bambu-1</c>, <c>bambu-2</c>, …).
    /// </summary>
    public string Alias { get; set; } = string.Empty;

    /// <summary>Printer IP address or hostname on the LAN.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>
    /// Printer serial number (from the printer screen or Bambu Studio's device page).
    /// Forms the MQTT topics <c>device/{serial}/report|request</c>.
    /// </summary>
    public string SerialNumber { get; set; } = string.Empty;

    /// <summary>
    /// LAN access code from the printer's network settings screen. The single secret:
    /// it is the password for MQTT, FTPS, and the camera stream alike (user is always
    /// <c>bblp</c>). Never logged, never echoed by any tool.
    /// </summary>
    public string AccessCode { get; set; } = string.Empty;

    /// <summary>Printer model, informational. This server is validated on the X1C.</summary>
    public string Model { get; set; } = "X1C";

    /// <summary>RTSPS camera port. 322 on the X1 series.</summary>
    public int CameraPort { get; set; } = 322;

    /// <summary>
    /// Optional SHA-256 pin for the camera's TLS certificate. Blank accepts the printer's
    /// self-signed certificate (normal for LAN mode; MQTT and FTPS do the same).
    /// </summary>
    public string CameraCertificateSha256 { get; set; } = string.Empty;

    /// <summary>Free-text description shown by <c>bambu_list_printers</c>.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Serial with all but the last four characters masked, for logs and tool output.</summary>
    public string MaskedSerial =>
        SerialNumber.Length <= 4 ? SerialNumber : new string('*', SerialNumber.Length - 4) + SerialNumber[^4..];
}

public sealed class ServerOptions
{
    public const string SectionName = "Server";

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5718;
    public string Path { get; set; } = "/mcp";

    /// <summary>Service name when running as a Windows Service.</summary>
    public string WindowsServiceName { get; set; } = "BambuMCPSharp";

    /// <summary>Optional MCP endpoint password. Blank disables MCP password auth.</summary>
    public string Password { get; set; } = string.Empty;
}
