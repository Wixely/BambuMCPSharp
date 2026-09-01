using System.ComponentModel;
using BambuMCPSharp.Services;
using FluentFTP;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace BambuMCPSharp.Tools;

/// <summary>
/// SD-card file management over implicit FTPS. Local files live only in the configured
/// transfer directory: the agent names files, never local paths, so this server cannot be
/// steered into reading or writing elsewhere on the host.
/// </summary>
[McpServerToolType]
public static class FileTools
{
    [McpServerTool(Name = "bambu_inspect_project"),
     Description("Inspect an already-sliced .gcode.3mf/.3mf or standalone .gcode in the configured transfer directory. Performs bounded structural and printer-model checks without uploading, modifying, or executing the file.")]
    public static async Task<string> InspectProject(
        PrinterRegistry registry,
        SafetyGate gate,
        IHostEnvironment env,
        [Description("Name of the file inside the transfer directory to inspect.")] string localName,
        [Description("Printer alias whose configured model should be used for compatibility checking. Omit for the default printer.")] string? alias = null,
        CancellationToken ct = default)
    {
        gate.EnsureFeature(gate.Options.EnableFiles, "bambu_inspect_project", "EnableFiles");
        var printer = registry.ResolveAlias(alias);
        var localPath = ToolHelpers.ResolveLocalFile(
            env.ContentRootPath, gate.Options.FileTransferDirectory, localName, "bambu_inspect_project");
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
            throw new McpException($"Inspection of '{localName}' refused: {exception.Message}");
        }
        catch (IOException)
        {
            throw new McpException($"Inspection of '{localName}' failed because the file could not be read safely.");
        }

        return ToolHelpers.Json(gate, new
        {
            fileName = inspection.FileName,
            format = inspection.Format,
            sizeBytes = inspection.SizeBytes,
            sha256 = inspection.Sha256,
            targetModel = inspection.TargetModel,
            structurallyValid = inspection.StructurallyValid,
            modelMatchesTarget = inspection.ModelMatchesTarget,
            findings = inspection.Findings,
            plates = inspection.Plates.Select(plate => new
            {
                plate = plate.Plate,
                entry = plate.Entry,
                sizeBytes = plate.SizeBytes,
                printerModel = plate.PrinterModel,
                printerSettingsId = plate.PrinterSettingsId,
                nozzleDiameterMm = plate.NozzleDiameterMm,
                printableWidthMm = plate.PrintableWidthMm,
                printableDepthMm = plate.PrintableDepthMm,
                printableHeightMm = plate.PrintableHeightMm,
                maximumZMm = plate.MaximumZMm,
                totalLayers = plate.TotalLayers,
                estimatedTime = plate.EstimatedTime,
                bedType = plate.BedType,
                filamentTypes = plate.FilamentTypes,
                labelObjectsEnabled = plate.LabelObjectsEnabled,
                partsSafelyAddressable = plate.PartsSafelyAddressable,
                parts = plate.Parts.Select(part => new
                {
                    identifyId = part.IdentifyId,
                    name = part.Name,
                    preSkipped = part.PreSkipped,
                }),
                partFindings = plate.PartFindings,
                hasHeaderBlock = plate.HasHeaderBlock,
                hasConfigBlock = plate.HasConfigBlock,
                hasExecutableBlock = plate.HasExecutableBlock,
                modelMatchesTarget = plate.ModelMatchesTarget,
                structurallyValid = plate.StructurallyValid,
                findings = plate.Findings,
            }),
        });
    }

    [McpServerTool(Name = "bambu_list_files"),
     Description("List files on the printer's SD card over FTPS. Sliced projects usually live in '/' or '/cache'; timelapses in '/timelapse'. Model files (.3mf/.gcode) are flagged.")]
    public static async Task<string> ListFiles(
        PrinterRegistry registry,
        SafetyGate gate,
        BambuFtp ftp,
        [Description("Directory to list. Default '/'.")] string? path = null,
        [Description("Maximum entries to return.")] int? limit = null,
        [Description("Printer alias. Omit to use the default printer.")] string? alias = null,
        CancellationToken ct = default)
    {
        gate.EnsureFeature(gate.Options.EnableFiles, "bambu_list_files", "EnableFiles");
        var printer = registry.ResolveAlias(alias);
        var remote = BambuFtp.NormalizeRemotePath(path);
        var max = gate.ClampLimit(limit);

        var items = await ftp.WithClientAsync(printer, async client =>
            (await client.GetListing(remote, ct)).ToList(), ct);

        var shaped = items
            .OrderByDescending(i => i.Modified)
            .Take(max)
            .Select(i => new
            {
                name = i.Name,
                path = i.FullName,
                type = i.Type == FtpObjectType.Directory ? "directory" : "file",
                sizeBytes = i.Type == FtpObjectType.Directory ? (long?)null : i.Size,
                modifiedUtc = i.Modified == DateTime.MinValue ? (DateTime?)null : i.Modified.ToUniversalTime(),
                isModel = i.Type == FtpObjectType.File &&
                          (i.Name.EndsWith(".3mf", StringComparison.OrdinalIgnoreCase) ||
                           i.Name.EndsWith(".gcode", StringComparison.OrdinalIgnoreCase)),
            })
            .ToList();

        return ToolHelpers.Json(gate, new
        {
            printer = printer.Alias,
            path = remote,
            total = items.Count,
            truncated = items.Count > shaped.Count,
            files = shaped,
        });
    }

    [McpServerTool(Name = "bambu_download_file"),
     Description("Download a file from the printer's SD card into the server's transfer directory (Bambu:FileTransferDirectory). Returns the local path. Size-capped by Bambu:MaxDownloadBytes.")]
    public static async Task<string> DownloadFile(
        PrinterRegistry registry,
        SafetyGate gate,
        BambuFtp ftp,
        IHostEnvironment env,
        [Description("Path of the file on the SD card, e.g. '/timelapse/video.mp4'.")] string remotePath,
        [Description("File name to save as locally. Default: the remote file's name.")] string? localName = null,
        [Description("Printer alias. Omit to use the default printer.")] string? alias = null,
        CancellationToken ct = default)
    {
        gate.EnsureFeature(gate.Options.EnableFiles, "bambu_download_file", "EnableFiles");
        var printer = registry.ResolveAlias(alias);
        var remote = BambuFtp.NormalizeRemotePath(remotePath);
        var fileName = string.IsNullOrWhiteSpace(localName) ? Path.GetFileName(remote) : localName.Trim();
        var localPath = ToolHelpers.ResolveLocalFile(
            env.ContentRootPath, gate.Options.FileTransferDirectory, fileName, "bambu_download_file");

        var (status, size) = await ftp.WithClientAsync(printer, async client =>
        {
            var remoteSize = await client.GetFileSize(remote, -1, ct);
            if (remoteSize > gate.Options.MaxDownloadBytes)
            {
                throw new McpException(
                    $"'{remote}' is {remoteSize:N0} bytes, over Bambu:MaxDownloadBytes={gate.Options.MaxDownloadBytes:N0}.");
            }

            var result = await client.DownloadFile(localPath, remote, FtpLocalExists.Overwrite, FtpVerify.None, null, ct);
            return (result, remoteSize);
        }, ct);

        if (status == FtpStatus.Failed)
        {
            throw new McpException($"Download of '{remote}' from '{printer.Alias}' failed. Does the file exist? Check bambu_list_files.");
        }

        return ToolHelpers.Json(gate, new
        {
            printer = printer.Alias,
            remotePath = remote,
            localPath,
            sizeBytes = size >= 0 ? size : new FileInfo(localPath).Length,
        });
    }

    [McpServerTool(Name = "bambu_upload_file"),
     Description("Upload a file from the server's transfer directory (Bambu:FileTransferDirectory) to the printer's SD card. This is the hand-off from the slicing system: drop the .3mf in the transfer directory, upload, then bambu_start_print. Requires Bambu:AllowFileUpload=true.")]
    public static async Task<string> UploadFile(
        PrinterRegistry registry,
        SafetyGate gate,
        BambuFtp ftp,
        IHostEnvironment env,
        [Description("Name of the file inside the transfer directory to upload.")] string localName,
        [Description("Destination path on the SD card. Default: '/' + the file name.")] string? remotePath = null,
        [Description("Printer alias. Omit to use the default printer.")] string? alias = null,
        CancellationToken ct = default)
    {
        gate.EnsureFeature(gate.Options.EnableFiles, "bambu_upload_file", "EnableFiles");
        gate.EnsureFileUpload("bambu_upload_file");
        var printer = registry.ResolveAlias(alias);

        var localPath = ToolHelpers.ResolveLocalFile(
            env.ContentRootPath, gate.Options.FileTransferDirectory, localName, "bambu_upload_file");
        if (!File.Exists(localPath))
        {
            throw new McpException(
                $"'{localName}' was not found in the transfer directory ({gate.Options.FileTransferDirectory}). " +
                "The slicing system (or you) must place the file there first.");
        }

        var info = new FileInfo(localPath);
        if (info.Length > gate.Options.MaxUploadBytes)
        {
            throw new McpException(
                $"'{localName}' is {info.Length:N0} bytes, over Bambu:MaxUploadBytes={gate.Options.MaxUploadBytes:N0}.");
        }

        var remote = BambuFtp.NormalizeRemotePath(
            string.IsNullOrWhiteSpace(remotePath) ? "/" + Path.GetFileName(localPath) : remotePath);

        var status = await ftp.WithClientAsync(printer, client =>
            client.UploadFile(localPath, remote, FtpRemoteExists.Overwrite, true, FtpVerify.None, null, ct), ct);

        if (status == FtpStatus.Failed)
        {
            throw new McpException($"Upload of '{localName}' to '{printer.Alias}:{remote}' failed.");
        }

        return ToolHelpers.Json(gate, new
        {
            printer = printer.Alias,
            localPath,
            remotePath = remote,
            sizeBytes = info.Length,
            hint = "Start it with bambu_start_print file=" + remote,
        });
    }

    [McpServerTool(Name = "bambu_delete_file"),
     Description("Delete a file from the printer's SD card. Cannot be undone. Requires Bambu:ReadOnly=false and Bambu:AllowFileDelete=true (off by default).")]
    public static async Task<string> DeleteFile(
        PrinterRegistry registry,
        SafetyGate gate,
        BambuFtp ftp,
        [Description("Path of the file on the SD card to delete.")] string remotePath,
        [Description("Printer alias. Omit to use the default printer.")] string? alias = null,
        CancellationToken ct = default)
    {
        gate.EnsureFeature(gate.Options.EnableFiles, "bambu_delete_file", "EnableFiles");
        gate.EnsureFileDelete("bambu_delete_file");
        var printer = registry.ResolveAlias(alias);
        var remote = BambuFtp.NormalizeRemotePath(remotePath);

        await ftp.WithClientAsync<object?>(printer, async client =>
        {
            await client.DeleteFile(remote, ct);
            return null;
        }, ct);

        return ToolHelpers.Json(gate, new
        {
            printer = printer.Alias,
            deleted = remote,
        });
    }
}
