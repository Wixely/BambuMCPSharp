using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace BambuMCPSharp.Services;

public sealed record ProjectInspectionLimits(
    long MaxFileBytes,
    int MaxArchiveEntries,
    long MaxArchiveUncompressedBytes)
{
    public const int MaxPlates = 64;
    public const int MaxPartsPerPlate = 512;
    public const int HeaderCaptureBytes = 2 * 1024 * 1024;
    public const int MaxMetadataLineChars = 65_536;
}

public sealed record ProjectPart(
    int IdentifyId,
    string Name,
    bool PreSkipped);

public sealed record GcodeInspection(
    int Plate,
    string Entry,
    long SizeBytes,
    string? PrinterModel,
    string? PrinterSettingsId,
    double? NozzleDiameterMm,
    double? PrintableWidthMm,
    double? PrintableDepthMm,
    double? PrintableHeightMm,
    double? MaximumZMm,
    int? TotalLayers,
    string? EstimatedTime,
    string? BedType,
    IReadOnlyList<string> FilamentTypes,
    bool LabelObjectsEnabled,
    bool PartsSafelyAddressable,
    IReadOnlyList<ProjectPart> Parts,
    IReadOnlyList<string> PartFindings,
    bool HasHeaderBlock,
    bool HasConfigBlock,
    bool HasExecutableBlock,
    bool ModelMatchesTarget,
    bool StructurallyValid,
    IReadOnlyList<string> Findings);

public sealed record ProjectInspection(
    string FileName,
    string Format,
    long SizeBytes,
    string Sha256,
    string TargetModel,
    bool StructurallyValid,
    bool ModelMatchesTarget,
    IReadOnlyList<GcodeInspection> Plates,
    IReadOnlyList<string> Findings);

public static partial class ProjectInspector
{
    private static readonly byte[][] BlockTokens =
    [
        "; HEADER_BLOCK_START"u8.ToArray(),
        "; HEADER_BLOCK_END"u8.ToArray(),
        "; CONFIG_BLOCK_START"u8.ToArray(),
        "; CONFIG_BLOCK_END"u8.ToArray(),
        "; EXECUTABLE_BLOCK_START"u8.ToArray(),
        "; EXECUTABLE_BLOCK_END"u8.ToArray(),
    ];

    public static async Task<ProjectInspection> InspectAsync(
        string path,
        string targetModel,
        ProjectInspectionLimits limits,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(limits);
        ValidateLimits(limits);

        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("The project file does not exist.", path);
        }
        if (info.Length <= 0)
        {
            throw new InvalidDataException("The project file is empty.");
        }
        if (info.Length > limits.MaxFileBytes)
        {
            throw new InvalidDataException(
                $"The project is {info.Length:N0} bytes, over the inspection limit of {limits.MaxFileBytes:N0} bytes.");
        }

        var fileName = Path.GetFileName(path);
        var normalizedTarget = string.IsNullOrWhiteSpace(targetModel) ? "X1C" : targetModel.Trim();
        await using var file = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        if (file.Length != info.Length)
        {
            throw new InvalidDataException("The project file changed before inspection began.");
        }

        var hash = await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false);
        var sha256 = Convert.ToHexString(hash).ToLowerInvariant();
        file.Position = 0;

        if (fileName.EndsWith(".gcode", StringComparison.OrdinalIgnoreCase))
        {
            var plate = await InspectGcodeAsync(
                file, info.Length, 1, fileName, normalizedTarget, limits.MaxArchiveUncompressedBytes,
                PartCatalogue.Unavailable("Standalone G-code has no Metadata/slice_info.config part catalogue."),
                cancellationToken)
                .ConfigureAwait(false);
            return Shape(fileName, "gcode", info.Length, sha256, normalizedTarget, [plate], []);
        }

        if (!fileName.EndsWith(".3mf", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Only .gcode, .gcode.3mf, and .3mf files can be inspected.");
        }

        return await InspectArchiveAsync(
            file, fileName, info.Length, sha256, normalizedTarget, limits, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ProjectInspection> InspectArchiveAsync(
        Stream file,
        string fileName,
        long fileSize,
        string sha256,
        string targetModel,
        ProjectInspectionLimits limits,
        CancellationToken cancellationToken)
    {
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count > limits.MaxArchiveEntries)
        {
            throw new InvalidDataException(
                $"The archive contains {archive.Entries.Count:N0} entries, over the limit of {limits.MaxArchiveEntries:N0}.");
        }

        long totalUncompressed = 0;
        foreach (var entry in archive.Entries)
        {
            totalUncompressed = checked(totalUncompressed + entry.Length);
            if (totalUncompressed > limits.MaxArchiveUncompressedBytes)
            {
                throw new InvalidDataException(
                    $"The archive expands beyond the {limits.MaxArchiveUncompressedBytes:N0}-byte inspection limit.");
            }
        }

        var candidates = archive.Entries
            .Select(entry => (Entry: entry, Match: PlateEntryRegex().Match(entry.FullName.Replace('\\', '/'))))
            .Where(candidate => candidate.Match.Success)
            .Select(candidate => (candidate.Entry, Plate: int.Parse(candidate.Match.Groups[1].Value, CultureInfo.InvariantCulture)))
            .OrderBy(candidate => candidate.Plate)
            .ToList();

        var findings = new List<string>();
        if (candidates.Count == 0)
        {
            findings.Add("Archive contains no Metadata/plate_N.gcode entry.");
            return Shape(fileName, "3mf", fileSize, sha256, targetModel, [], findings);
        }
        if (candidates.Count > ProjectInspectionLimits.MaxPlates)
        {
            throw new InvalidDataException(
                $"The archive contains {candidates.Count:N0} plate G-code entries, over the limit of {ProjectInspectionLimits.MaxPlates}.");
        }
        if (candidates.Any(candidate => candidate.Plate is < 1 or > ProjectInspectionLimits.MaxPlates))
        {
            throw new InvalidDataException(
                $"Archive plate numbers must be between 1 and {ProjectInspectionLimits.MaxPlates}.");
        }
        if (candidates.GroupBy(candidate => candidate.Plate).Any(group => group.Count() > 1))
        {
            throw new InvalidDataException("The archive contains duplicate plate numbers.");
        }

        var partCatalogues = await ReadPartCataloguesAsync(
            archive,
            candidates.Select(candidate => candidate.Plate).ToArray(),
            cancellationToken).ConfigureAwait(false);

        var plates = new List<GcodeInspection>(candidates.Count);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var stream = candidate.Entry.Open();
            plates.Add(await InspectGcodeAsync(
                stream,
                candidate.Entry.Length,
                candidate.Plate,
                candidate.Entry.FullName.Replace('\\', '/'),
                targetModel,
                limits.MaxArchiveUncompressedBytes,
                partCatalogues.TryGetValue(candidate.Plate, out var catalogue)
                    ? catalogue
                    : PartCatalogue.Unavailable($"Metadata/slice_info.config has no unambiguous catalogue for plate {candidate.Plate}."),
                cancellationToken).ConfigureAwait(false));
        }

        return Shape(fileName, "3mf", fileSize, sha256, targetModel, plates, findings);
    }

    private static async Task<GcodeInspection> InspectGcodeAsync(
        Stream stream,
        long declaredLength,
        int plate,
        string entry,
        string targetModel,
        long maxBytes,
        PartCatalogue partCatalogue,
        CancellationToken cancellationToken)
    {
        if (declaredLength <= 0 || declaredLength > maxBytes)
        {
            throw new InvalidDataException($"G-code entry '{entry}' has an invalid or excessive size.");
        }

        var tokenPositions = Enumerable.Repeat(-1L, BlockTokens.Length).ToArray();
        var longestToken = BlockTokens.Max(token => token.Length);
        var carry = Array.Empty<byte>();
        var buffer = new byte[65_536];
        using var prefix = new MemoryStream(Math.Min(ProjectInspectionLimits.HeaderCaptureBytes, checked((int)Math.Min(declaredLength, int.MaxValue))));
        long total = 0;

        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total = checked(total + read);
            if (total > maxBytes || total > declaredLength)
            {
                throw new InvalidDataException($"G-code entry '{entry}' exceeded its declared or configured size.");
            }

            var remainingPrefix = ProjectInspectionLimits.HeaderCaptureBytes - checked((int)prefix.Length);
            if (remainingPrefix > 0)
            {
                prefix.Write(buffer, 0, Math.Min(read, remainingPrefix));
            }

            var scan = new byte[carry.Length + read];
            carry.CopyTo(scan, 0);
            Buffer.BlockCopy(buffer, 0, scan, carry.Length, read);
            var scanStart = total - read - carry.Length;
            for (var index = 0; index < BlockTokens.Length; index++)
            {
                if (tokenPositions[index] >= 0) continue;
                var position = scan.AsSpan().IndexOf(BlockTokens[index]);
                if (position >= 0) tokenPositions[index] = scanStart + position;
            }

            var carryLength = Math.Min(longestToken - 1, scan.Length);
            carry = scan.AsSpan(scan.Length - carryLength, carryLength).ToArray();
        }

        if (total != declaredLength)
        {
            throw new InvalidDataException(
                $"G-code entry '{entry}' length changed while it was being inspected.");
        }

        var headerText = Encoding.UTF8.GetString(prefix.GetBuffer(), 0, checked((int)prefix.Length));
        var metadata = ParseMetadata(headerText, entry);
        var hasHeader = tokenPositions[0] >= 0 && tokenPositions[1] > tokenPositions[0];
        var hasConfig = tokenPositions[2] > tokenPositions[1] && tokenPositions[3] > tokenPositions[2];
        var hasExecutable = tokenPositions[4] > tokenPositions[3] && tokenPositions[5] > tokenPositions[4];
        var findings = new List<string>();

        if (!hasHeader) findings.Add("Missing or incomplete HEADER block.");
        if (!hasConfig) findings.Add("Missing or incomplete CONFIG block.");
        if (!hasExecutable) findings.Add("Missing or incomplete EXECUTABLE block.");
        if (string.IsNullOrWhiteSpace(metadata.PrinterModel)) findings.Add("Printer model metadata is missing.");

        var modelMatches = ModelMatches(metadata.PrinterModel, targetModel);
        if (!string.IsNullOrWhiteSpace(metadata.PrinterModel) && !modelMatches)
        {
            findings.Add($"Printer model '{metadata.PrinterModel}' does not match target '{targetModel}'.");
        }

        return new GcodeInspection(
            plate,
            entry,
            total,
            metadata.PrinterModel,
            metadata.PrinterSettingsId,
            metadata.NozzleDiameterMm,
            metadata.PrintableWidthMm,
            metadata.PrintableDepthMm,
            metadata.PrintableHeightMm,
            metadata.MaximumZMm,
            metadata.TotalLayers,
            metadata.EstimatedTime,
            metadata.BedType,
            metadata.FilamentTypes,
            partCatalogue.LabelObjectsEnabled,
            partCatalogue.SafelyAddressable,
            partCatalogue.Parts,
            partCatalogue.Findings,
            hasHeader,
            hasConfig,
            hasExecutable,
            modelMatches,
            hasHeader && hasConfig && hasExecutable && modelMatches,
            findings);
    }

    private static ParsedMetadata ParseMetadata(string headerText, string entry)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? estimatedTime = null;
        int? totalLayers = null;
        double? maximumZ = null;

        foreach (var rawLine in headerText.Split('\n'))
        {
            if (rawLine.Length > ProjectInspectionLimits.MaxMetadataLineChars)
            {
                throw new InvalidDataException($"G-code entry '{entry}' contains an excessive metadata line.");
            }

            var line = rawLine.TrimEnd('\r');
            if (!line.StartsWith(';')) continue;
            var content = line[1..].Trim();
            var separator = content.IndexOf(" = ", StringComparison.Ordinal);
            if (separator > 0)
            {
                values.TryAdd(content[..separator].Trim(), content[(separator + 3)..].Trim());
                continue;
            }

            var totalTimeIndex = content.IndexOf("total estimated time:", StringComparison.OrdinalIgnoreCase);
            if (totalTimeIndex >= 0)
            {
                var totalTime = content[(totalTimeIndex + "total estimated time:".Length)..];
                estimatedTime = totalTime.Split(';')[0].Trim();
            }
            else if (content.StartsWith("model printing time:", StringComparison.OrdinalIgnoreCase) && estimatedTime is null)
            {
                estimatedTime = content["model printing time:".Length..].Split(';')[0].Trim();
            }
            else if (content.StartsWith("total layer number:", StringComparison.OrdinalIgnoreCase) &&
                     int.TryParse(content["total layer number:".Length..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var layers))
            {
                totalLayers = layers;
            }
            else if (content.StartsWith("max_z_height:", StringComparison.OrdinalIgnoreCase) &&
                     double.TryParse(content["max_z_height:".Length..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
            {
                maximumZ = z;
            }
        }

        var (width, depth) = ParsePrintableArea(Value(values, "printable_area") ?? Value(values, "bed_shape"));
        return new ParsedMetadata(
            Value(values, "printer_model"),
            Value(values, "printer_settings_id"),
            ParseDouble(Value(values, "nozzle_diameter")?.Split(',')[0]),
            width,
            depth,
            ParseDouble(Value(values, "printable_height") ?? Value(values, "max_print_height")),
            maximumZ,
            totalLayers,
            estimatedTime,
            Value(values, "curr_bed_type") ?? Value(values, "bed_type"),
            SplitList(Value(values, "filament_type")));
    }

    private static async Task<IReadOnlyDictionary<int, PartCatalogue>> ReadPartCataloguesAsync(
        ZipArchive archive,
        IReadOnlyCollection<int> gcodePlates,
        CancellationToken cancellationToken)
    {
        var entries = archive.Entries
            .Where(entry => string.Equals(
                entry.FullName.Replace('\\', '/'),
                "Metadata/slice_info.config",
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (entries.Count == 0) return new Dictionary<int, PartCatalogue>();
        if (entries.Count > 1)
        {
            throw new InvalidDataException("The archive contains duplicate Metadata/slice_info.config entries.");
        }

        var entry = entries[0];
        if (entry.Length <= 0)
        {
            throw new InvalidDataException("Metadata/slice_info.config is empty.");
        }

        await using var stream = entry.Open();
        var settings = new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = entry.Length,
        };

        XDocument document;
        try
        {
            using var reader = XmlReader.Create(stream, settings);
            document = await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken).ConfigureAwait(false);
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException("Metadata/slice_info.config is not safe, well-formed XML.", exception);
        }

        var plateElements = document.Descendants().Where(element => element.Name.LocalName == "plate").ToList();
        var catalogues = new Dictionary<int, PartCatalogue>();
        foreach (var plateElement in plateElements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var plateNumber = ReadPlateNumber(plateElement);
            if (plateNumber is null && plateElements.Count == 1 && gcodePlates.Count == 1)
            {
                plateNumber = gcodePlates.Single();
            }
            if (plateNumber is null || !gcodePlates.Contains(plateNumber.Value)) continue;
            if (catalogues.ContainsKey(plateNumber.Value))
            {
                throw new InvalidDataException($"Metadata/slice_info.config contains duplicate plate {plateNumber.Value} catalogues.");
            }

            catalogues.Add(plateNumber.Value, ReadPartCatalogue(plateElement));
        }

        return catalogues;
    }

    private static int? ReadPlateNumber(XElement plate)
    {
        var value = plate.Elements()
            .FirstOrDefault(element =>
                element.Name.LocalName == "metadata" &&
                string.Equals((string?)element.Attribute("key"), "index", StringComparison.OrdinalIgnoreCase))?
            .Attribute("value")?.Value;
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) &&
               number is >= 1 and <= ProjectInspectionLimits.MaxPlates
            ? number
            : null;
    }

    private static PartCatalogue ReadPartCatalogue(XElement plate)
    {
        var findings = new List<string>();
        var labelValue = plate.Elements()
            .FirstOrDefault(element =>
                element.Name.LocalName == "metadata" &&
                string.Equals((string?)element.Attribute("key"), "label_object_enabled", StringComparison.OrdinalIgnoreCase))?
            .Attribute("value")?.Value;
        var labelObjectsEnabled = labelValue is not null &&
            (string.Equals(labelValue, "true", StringComparison.OrdinalIgnoreCase) || labelValue == "1");
        if (!labelObjectsEnabled)
        {
            findings.Add("The sliced plate does not enable object labelling/exclusion, so Skip Parts is unavailable.");
        }

        var objectElements = plate.Elements().Where(element => element.Name.LocalName == "object").ToList();
        if (objectElements.Count > ProjectInspectionLimits.MaxPartsPerPlate)
        {
            throw new InvalidDataException(
                $"A plate contains {objectElements.Count:N0} objects, over the {ProjectInspectionLimits.MaxPartsPerPlate:N0}-part inspection limit.");
        }

        var parts = new List<ProjectPart>(objectElements.Count);
        foreach (var element in objectElements)
        {
            var idText = element.Attribute("identify_id")?.Value;
            var name = element.Attribute("name")?.Value?.Trim();
            if (!int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ||
                id is < 0 or > 0x00FF_FFFF)
            {
                findings.Add("At least one part has a missing or invalid identify_id.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(name) || name.Length > 512)
            {
                findings.Add($"Part {id} has a missing or excessive name.");
                continue;
            }

            var skipped = string.Equals(element.Attribute("skipped")?.Value, "true", StringComparison.OrdinalIgnoreCase) ||
                          element.Attribute("skipped")?.Value == "1";
            parts.Add(new ProjectPart(id, name, skipped));
        }

        if (parts.Count == 0) findings.Add("The selected plate has no addressable parts.");
        if (parts.GroupBy(part => part.IdentifyId).Any(group => group.Count() > 1))
        {
            findings.Add("The selected plate contains duplicate identify_id values; selecting one could skip multiple parts.");
        }

        var safelyAddressable = labelObjectsEnabled &&
                                parts.Count > 0 &&
                                parts.Count == objectElements.Count &&
                                parts.Select(part => part.IdentifyId).Distinct().Count() == parts.Count;
        return new PartCatalogue(labelObjectsEnabled, safelyAddressable, parts, findings);
    }

    private static ProjectInspection Shape(
        string fileName,
        string format,
        long size,
        string sha256,
        string targetModel,
        IReadOnlyList<GcodeInspection> plates,
        IReadOnlyList<string> findings)
    {
        var structurallyValid = plates.Count > 0 && plates.All(plate => plate.StructurallyValid);
        var modelMatches = plates.Count > 0 && plates.All(plate => plate.ModelMatchesTarget);
        return new ProjectInspection(
            fileName, format, size, sha256, targetModel, structurallyValid, modelMatches, plates, findings);
    }

    private static void ValidateLimits(ProjectInspectionLimits limits)
    {
        if (limits.MaxFileBytes <= 0 || limits.MaxArchiveEntries <= 0 || limits.MaxArchiveUncompressedBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limits), "Every project inspection limit must be positive.");
        }
    }

    private static bool ModelMatches(string? projectModel, string targetModel)
    {
        if (string.IsNullOrWhiteSpace(projectModel)) return false;
        var project = NormalizeModel(projectModel);
        var target = NormalizeModel(targetModel);
        if (project == target) return true;
        return target == "x1c" && project is "bambulabx1carbon" or "x1carbon";
    }

    private static string NormalizeModel(string value) =>
        new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray()) switch
        {
            "bambulabx1c" => "x1c",
            var normalized => normalized,
        };

    private static (double? Width, double? Depth) ParsePrintableArea(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return (null, null);
        var points = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(point => point.Split('x', StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2 &&
                            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out _) &&
                            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            .Select(parts => (
                X: double.Parse(parts[0], CultureInfo.InvariantCulture),
                Y: double.Parse(parts[1], CultureInfo.InvariantCulture)))
            .ToList();
        if (points.Count < 2) return (null, null);
        return (points.Max(point => point.X) - points.Min(point => point.X),
                points.Max(point => point.Y) - points.Min(point => point.Y));
    }

    private static double? ParseDouble(string? value) =>
        double.TryParse(value?.Trim().Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static IReadOnlyList<string> SplitList(string? value) => string.IsNullOrWhiteSpace(value)
        ? []
        : value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim('"'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(32)
            .ToArray();

    private static string? Value(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim('"') : null;

    [GeneratedRegex(@"^Metadata/plate_(\d{1,3})\.gcode$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlateEntryRegex();

    private sealed record ParsedMetadata(
        string? PrinterModel,
        string? PrinterSettingsId,
        double? NozzleDiameterMm,
        double? PrintableWidthMm,
        double? PrintableDepthMm,
        double? PrintableHeightMm,
        double? MaximumZMm,
        int? TotalLayers,
        string? EstimatedTime,
        string? BedType,
        IReadOnlyList<string> FilamentTypes);

    private sealed record PartCatalogue(
        bool LabelObjectsEnabled,
        bool SafelyAddressable,
        IReadOnlyList<ProjectPart> Parts,
        IReadOnlyList<string> Findings)
    {
        public static PartCatalogue Unavailable(string finding) => new(false, false, [], [finding]);
    }
}
