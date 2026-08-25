using System.IO.Compression;
using System.Text;
using BambuMCPSharp.Services;

var specifications = new (string Name, Func<Task> Run)[]
{
    ("valid X1C G-code metadata is parsed", ValidX1cGcodeIsParsed),
    ("printer-model mismatch is rejected", PrinterModelMismatchIsRejected),
    ("incomplete executable block is rejected", IncompleteExecutableBlockIsRejected),
    ("out-of-order Bambu blocks are rejected", OutOfOrderBlocksAreRejected),
    ("3MF plate entries are discovered", ThreeMfPlateEntriesAreDiscovered),
    ("3MF without a plate is invalid", ThreeMfWithoutPlateIsInvalid),
    ("file inspection size is bounded", FileInspectionSizeIsBounded),
    ("3MF entry count is bounded", ThreeMfEntryCountIsBounded),
    ("3MF expansion is bounded", ThreeMfExpansionIsBounded),
};

var failures = 0;
foreach (var specification in specifications)
{
    try
    {
        await specification.Run();
        Console.WriteLine($"PASS {specification.Name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {specification.Name}: {exception.Message}");
    }
}

Console.WriteLine($"{specifications.Length - failures}/{specifications.Length} specifications passed.");
if (failures != 0) return 1;

if (args is ["--sample", var samplePath])
{
    var inspection = await ProjectInspector.InspectAsync(
        samplePath,
        "X1C",
        new ProjectInspectionLimits(256_000_000, 2_048, 512_000_000));
    var plate = inspection.Plates.Single();
    Console.WriteLine(
        $"SAMPLE format={inspection.Format} structurallyValid={inspection.StructurallyValid} " +
        $"modelMatches={inspection.ModelMatchesTarget} model={plate.PrinterModel} " +
        $"nozzleMm={plate.NozzleDiameterMm} plates={inspection.Plates.Count} estimated={plate.EstimatedTime}");
}

return 0;

static async Task ValidX1cGcodeIsParsed()
{
    await WithTempFileAsync(".gcode", ValidGcode("Bambu Lab X1 Carbon"), async path =>
    {
        var result = await ProjectInspector.InspectAsync(path, "X1C", DefaultLimits());
        var plate = result.Plates.Single();
        AssertEx.True(result.StructurallyValid);
        AssertEx.True(result.ModelMatchesTarget);
        AssertEx.Equal("Bambu Lab X1 Carbon", plate.PrinterModel);
        AssertEx.Equal(0.4, plate.NozzleDiameterMm);
        AssertEx.Equal(256.0, plate.PrintableWidthMm);
        AssertEx.Equal(256.0, plate.PrintableDepthMm);
        AssertEx.Equal(250.0, plate.PrintableHeightMm);
        AssertEx.Equal(150, plate.TotalLayers);
        AssertEx.Equal("High Temp Plate", plate.BedType);
        AssertEx.SequenceEqual(["PLA", "PLA-CF"], plate.FilamentTypes);
    });
}

static async Task PrinterModelMismatchIsRejected()
{
    await WithTempFileAsync(".gcode", ValidGcode("MK3S"), async path =>
    {
        var result = await ProjectInspector.InspectAsync(path, "X1C", DefaultLimits());
        AssertEx.False(result.StructurallyValid);
        AssertEx.False(result.ModelMatchesTarget);
        AssertEx.True(result.Plates.Single().Findings.Any(finding => finding.Contains("does not match", StringComparison.Ordinal)));
    });
}

static async Task IncompleteExecutableBlockIsRejected()
{
    var content = ValidGcode("Bambu Lab X1 Carbon").Replace("; EXECUTABLE_BLOCK_END\n", string.Empty, StringComparison.Ordinal);
    await WithTempFileAsync(".gcode", content, async path =>
    {
        var result = await ProjectInspector.InspectAsync(path, "X1C", DefaultLimits());
        AssertEx.False(result.StructurallyValid);
        AssertEx.False(result.Plates.Single().HasExecutableBlock);
    });
}

static async Task OutOfOrderBlocksAreRejected()
{
    var content = ValidGcode("Bambu Lab X1 Carbon")
        .Replace(
            "; EXECUTABLE_BLOCK_START\n; TEST DATA ONLY - CONTAINS NO MACHINE COMMANDS\n; EXECUTABLE_BLOCK_END",
            "; EXECUTABLE_BLOCK_END\n; TEST DATA ONLY - CONTAINS NO MACHINE COMMANDS\n; EXECUTABLE_BLOCK_START",
            StringComparison.Ordinal);
    await WithTempFileAsync(".gcode", content, async path =>
    {
        var result = await ProjectInspector.InspectAsync(path, "X1C", DefaultLimits());
        AssertEx.False(result.StructurallyValid);
        AssertEx.False(result.Plates.Single().HasExecutableBlock);
    });
}

static async Task ThreeMfPlateEntriesAreDiscovered()
{
    await WithArchiveAsync(
        [("Metadata/plate_2.gcode", ValidGcode("Bambu Lab X1 Carbon")), ("[Content_Types].xml", "<Types/>")],
        async path =>
        {
            var result = await ProjectInspector.InspectAsync(path, "X1C", DefaultLimits());
            AssertEx.Equal("3mf", result.Format);
            AssertEx.True(result.StructurallyValid);
            AssertEx.Equal(2, result.Plates.Single().Plate);
        });
}

static async Task ThreeMfWithoutPlateIsInvalid()
{
    await WithArchiveAsync([("[Content_Types].xml", "<Types/>")], async path =>
    {
        var result = await ProjectInspector.InspectAsync(path, "X1C", DefaultLimits());
        AssertEx.False(result.StructurallyValid);
        AssertEx.True(result.Findings.Any(finding => finding.Contains("no Metadata", StringComparison.Ordinal)));
    });
}

static async Task FileInspectionSizeIsBounded()
{
    await WithTempFileAsync(".gcode", ValidGcode("Bambu Lab X1 Carbon"), async path =>
    {
        await AssertEx.ThrowsAsync<InvalidDataException>(() =>
            ProjectInspector.InspectAsync(path, "X1C", new ProjectInspectionLimits(8, 10, 1_024)));
    });
}

static async Task ThreeMfEntryCountIsBounded()
{
    await WithArchiveAsync([("a", "1"), ("b", "2")], async path =>
    {
        await AssertEx.ThrowsAsync<InvalidDataException>(() =>
            ProjectInspector.InspectAsync(path, "X1C", new ProjectInspectionLimits(1_000_000, 1, 1_000_000)));
    });
}

static async Task ThreeMfExpansionIsBounded()
{
    await WithArchiveAsync([("Metadata/plate_1.gcode", ValidGcode("Bambu Lab X1 Carbon"))], async path =>
    {
        await AssertEx.ThrowsAsync<InvalidDataException>(() =>
            ProjectInspector.InspectAsync(path, "X1C", new ProjectInspectionLimits(1_000_000, 10, 32)));
    });
}

static ProjectInspectionLimits DefaultLimits() => new(1_000_000, 100, 2_000_000);

static string ValidGcode(string model) =>
    "; TEST_FIXTURE_NOT_PRINTABLE\n" +
    "; HEADER_BLOCK_START\n" +
    "; total estimated time: 1h 2m\n" +
    "; total layer number: 150\n" +
    "; max_z_height: 30.00\n" +
    "; HEADER_BLOCK_END\n" +
    "; CONFIG_BLOCK_START\n" +
    $"; printer_model = {model}\n" +
    "; printer_settings_id = test-only X1C profile\n" +
    "; nozzle_diameter = 0.4\n" +
    "; printable_area = 0x0,256x0,256x256,0x256\n" +
    "; printable_height = 250\n" +
    "; curr_bed_type = High Temp Plate\n" +
    "; filament_type = PLA;PLA-CF\n" +
    "; CONFIG_BLOCK_END\n" +
    "; EXECUTABLE_BLOCK_START\n" +
    "; TEST DATA ONLY - CONTAINS NO MACHINE COMMANDS\n" +
    "; EXECUTABLE_BLOCK_END\n";

static async Task WithTempFileAsync(string extension, string content, Func<string, Task> action)
{
    var path = Path.Combine(Path.GetTempPath(), $"BambuMCPSharp-Spec-{Guid.NewGuid():N}{extension}");
    try
    {
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false));
        await action(path);
    }
    finally
    {
        File.Delete(path);
    }
}

static async Task WithArchiveAsync(IReadOnlyList<(string Name, string Content)> entries, Func<string, Task> action)
{
    var path = Path.Combine(Path.GetTempPath(), $"BambuMCPSharp-Spec-{Guid.NewGuid():N}.gcode.3mf");
    try
    {
        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4_096, true))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false))
        {
            foreach (var item in entries)
            {
                var entry = archive.CreateEntry(item.Name, CompressionLevel.SmallestSize);
                await using var entryStream = entry.Open();
                await using var writer = new StreamWriter(entryStream, new UTF8Encoding(false), leaveOpen: false);
                await writer.WriteAsync(item.Content);
            }
        }

        await action(path);
    }
    finally
    {
        File.Delete(path);
    }
}

internal static class AssertEx
{
    public static void True(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Expected true.");
    }

    public static void False(bool condition) => True(!condition);

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }

    public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    {
        if (!expected.SequenceEqual(actual)) throw new InvalidOperationException("Sequences differ.");
    }

    public static async Task ThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}
