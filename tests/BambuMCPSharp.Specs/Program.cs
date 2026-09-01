using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using BambuMCPSharp.Configuration;
using BambuMCPSharp.Services;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

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
    ("3MF Skip Parts catalogue is parsed by plate", ThreeMfSkipPartsCatalogueIsParsed),
    ("unsafe Skip Parts metadata is not addressable", UnsafeSkipPartsMetadataIsNotAddressable),
    ("slice-info DTD is refused", SliceInfoDtdIsRefused),
    ("Skip Parts plan validates and verifies object ids", SkipPartsPlanValidatesAndVerifiesObjectIds),
    ("Skip Parts binds to the reported active file", SkipPartsBindsToReportedActiveFile),
    ("Skip Parts refuses to remove every remaining part", SkipPartsRefusesEveryRemainingPart),
    ("printer diagnostics expose current error context", PrinterDiagnosticsExposeCurrentErrorContext),
    ("clear-print-error payload matches Bambu protocol", ClearPrintErrorPayloadMatchesProtocol),
    ("error acknowledgement requires both safety gates", ErrorAcknowledgementRequiresBothSafetyGates),
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

static async Task ThreeMfSkipPartsCatalogueIsParsed()
{
    await WithArchiveAsync(
        [
            ("Metadata/plate_1.gcode", ValidGcode("Bambu Lab X1 Carbon")),
            ("Metadata/slice_info.config", ValidSliceInfo()),
        ],
        async path =>
        {
            var result = await ProjectInspector.InspectAsync(path, "X1C", DefaultLimits());
            var plate = result.Plates.Single();
            AssertEx.True(plate.LabelObjectsEnabled);
            AssertEx.True(plate.PartsSafelyAddressable);
            AssertEx.Equal(3, plate.Parts.Count);
            AssertEx.Equal(101, plate.Parts[0].IdentifyId);
            AssertEx.Equal("left bracket", plate.Parts[0].Name);
            AssertEx.True(plate.Parts[1].PreSkipped);
        });
}

static async Task UnsafeSkipPartsMetadataIsNotAddressable()
{
    const string unsafeSliceInfo =
        "<config><plate>" +
        "<metadata key=\"index\" value=\"1\"/>" +
        "<metadata key=\"label_object_enabled\" value=\"false\"/>" +
        "<object identify_id=\"7\" name=\"first\" skipped=\"false\"/>" +
        "<object identify_id=\"7\" name=\"second\" skipped=\"false\"/>" +
        "</plate></config>";
    await WithArchiveAsync(
        [
            ("Metadata/plate_1.gcode", ValidGcode("Bambu Lab X1 Carbon")),
            ("Metadata/slice_info.config", unsafeSliceInfo),
        ],
        async path =>
        {
            var result = await ProjectInspector.InspectAsync(path, "X1C", DefaultLimits());
            var plate = result.Plates.Single();
            AssertEx.False(plate.LabelObjectsEnabled);
            AssertEx.False(plate.PartsSafelyAddressable);
            AssertEx.True(plate.PartFindings.Any(finding => finding.Contains("duplicate", StringComparison.OrdinalIgnoreCase)));
        });
}

static async Task SliceInfoDtdIsRefused()
{
    const string unsafeSliceInfo =
        "<!DOCTYPE config [<!ENTITY probe SYSTEM \"file:///must-not-be-read\">]>" +
        "<config><plate><metadata key=\"index\" value=\"1\"/><object identify_id=\"1\" name=\"&probe;\"/></plate></config>";
    await WithArchiveAsync(
        [
            ("Metadata/plate_1.gcode", ValidGcode("Bambu Lab X1 Carbon")),
            ("Metadata/slice_info.config", unsafeSliceInfo),
        ],
        async path => await AssertEx.ThrowsAsync<InvalidDataException>(() =>
            ProjectInspector.InspectAsync(path, "X1C", DefaultLimits())));
}

static async Task SkipPartsPlanValidatesAndVerifiesObjectIds()
{
    await WithArchiveAsync(
        [
            ("Metadata/plate_1.gcode", ValidGcode("Bambu Lab X1 Carbon")),
            ("Metadata/slice_info.config", ValidSliceInfo()),
        ],
        async path =>
        {
            var inspection = await ProjectInspector.InspectAsync(path, "X1C", DefaultLimits());
            var state = JsonNode.Parse("""
                {
                  "print": {
                    "gcode_state": "RUNNING",
                    "task_id": "44",
                    "subtask_id": "45",
                    "gcode_file": "/multi.gcode.3mf",
                    "subtask_name": "multi.gcode",
                    "s_obj": [202]
                  }
                }
                """)!.AsObject();

            var plan = SkipPartsWorkflow.CreatePlan(state, inspection.Plates.Single(), "multi.gcode.3mf", [101]);
            AssertEx.Equal("skip_objects", plan.Command["command"]!.GetValue<string>());
            AssertEx.Equal(101, plan.Command["obj_list"]![0]!.GetValue<int>());
            AssertEx.Equal("left bracket", plan.RequestedParts.Single().Name);
            AssertEx.Equal(202, plan.AlreadySkippedParts.Single().IdentifyId);
            AssertEx.Equal(303, plan.RemainingPartsAfterRequest.Single().IdentifyId);

            var after = JsonNode.Parse("""
                {
                  "print": {
                    "gcode_state": "RUNNING",
                    "task_id": "44",
                    "subtask_id": "45",
                    "gcode_file": "/multi.gcode.3mf",
                    "s_obj": [202, 101]
                  }
                }
                """)!.AsObject();
            var verification = SkipPartsWorkflow.Verify(after, plan);
            AssertEx.Equal("verified", verification.Outcome);
            AssertEx.Equal(0, verification.MissingRequestedObjectIds.Count);
        });
}

static async Task SkipPartsRefusesEveryRemainingPart()
{
    await WithArchiveAsync(
        [
            ("Metadata/plate_1.gcode", ValidGcode("Bambu Lab X1 Carbon")),
            ("Metadata/slice_info.config", ValidSliceInfo()),
        ],
        async path =>
        {
            var inspection = await ProjectInspector.InspectAsync(path, "X1C", DefaultLimits());
            var state = JsonNode.Parse("""
                {
                  "print": {
                    "gcode_state": "PAUSE",
                    "gcode_file": "multi.gcode.3mf",
                    "s_obj": [202]
                  }
                }
                """)!.AsObject();
            await AssertEx.ThrowsAsync<InvalidOperationException>(() =>
            {
                SkipPartsWorkflow.CreatePlan(state, inspection.Plates.Single(), "multi.gcode.3mf", [101, 303]);
                return Task.CompletedTask;
            });
        });
}

static async Task SkipPartsBindsToReportedActiveFile()
{
    await WithArchiveAsync(
        [
            ("Metadata/plate_1.gcode", ValidGcode("Bambu Lab X1 Carbon")),
            ("Metadata/slice_info.config", ValidSliceInfo()),
        ],
        async path =>
        {
            var inspection = await ProjectInspector.InspectAsync(path, "X1C", DefaultLimits());
            var state = JsonNode.Parse("""
                {
                  "print": {
                    "gcode_state": "RUNNING",
                    "gcode_file": "different.gcode.3mf",
                    "subtask_name": "multi.gcode",
                    "s_obj": []
                  }
                }
                """)!.AsObject();
            await AssertEx.ThrowsAsync<InvalidOperationException>(() =>
            {
                SkipPartsWorkflow.CreatePlan(state, inspection.Plates.Single(), "multi.gcode.3mf", [101]);
                return Task.CompletedTask;
            });
        });
}

static Task PrinterDiagnosticsExposeCurrentErrorContext()
{
    var state = JsonNode.Parse("""
        {
          "print": {
            "gcode_state": "PAUSE",
            "stg_cur": 35,
            "subtask_id": "task-1",
            "print_error": 168496141,
            "nozzle_temper": 220.5,
            "bed_temper": 55,
            "wifi_signal": "-48dBm",
            "hms": [ { "attr": 134217729, "code": 196609 } ]
          }
        }
        """)!.AsObject();

    var report = PrinterDiagnostics.CreateReport(
        state,
        DateTimeOffset.UtcNow,
        "test-printer",
        staleAfterSeconds: 15);
    var current = PrinterDiagnostics.CurrentPrintError(state);

    AssertEx.Equal("test-printer", report["alias"]!.GetValue<string>());
    AssertEx.Equal(1, report["summary"]!["activeHmsCount"]!.GetValue<int>());
    AssertEx.True(report["summary"]!["hasClearablePrintErrorContext"]!.GetValue<bool>());
    AssertEx.Equal("paused: nozzle clog", report["print"]!["stage"]!.GetValue<string>());
    AssertEx.Equal(168496141L, current!.Code);
    AssertEx.Equal("0x0A0B0C0D", current.HexCode);
    AssertEx.Equal("task-1", current.SubtaskId);
    return Task.CompletedTask;
}

static Task ClearPrintErrorPayloadMatchesProtocol()
{
    var command = PrinterDiagnostics.CreateClearPrintErrorCommand(
        new PrintErrorContext(168496141, "task-1"));

    AssertEx.Equal("clean_print_error", command["command"]!.GetValue<string>());
    AssertEx.Equal("task-1", command["subtask_id"]!.GetValue<string>());
    AssertEx.Equal(168496141L, command["print_error"]!.GetValue<long>());
    AssertEx.False(command.ContainsKey("sequence_id"));
    return Task.CompletedTask;
}

static async Task ErrorAcknowledgementRequiresBothSafetyGates()
{
    var readOnlyGate = new SafetyGate(Options.Create(new BambuOptions
    {
        ReadOnly = true,
        AllowErrorClear = true,
    }));
    await AssertEx.ThrowsAsync<McpException>(() =>
    {
        readOnlyGate.EnsureErrorClear("bambu_clear_print_error");
        return Task.CompletedTask;
    });

    var categoryOffGate = new SafetyGate(Options.Create(new BambuOptions
    {
        ReadOnly = false,
        AllowErrorClear = false,
    }));
    await AssertEx.ThrowsAsync<McpException>(() =>
    {
        categoryOffGate.EnsureErrorClear("bambu_clear_print_error");
        return Task.CompletedTask;
    });

    var enabledGate = new SafetyGate(Options.Create(new BambuOptions
    {
        ReadOnly = false,
        AllowErrorClear = true,
    }));
    enabledGate.EnsureErrorClear("bambu_clear_print_error");
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

static string ValidSliceInfo() =>
    "<config><plate>" +
    "<metadata key=\"index\" value=\"1\"/>" +
    "<metadata key=\"label_object_enabled\" value=\"true\"/>" +
    "<object identify_id=\"101\" name=\"left bracket\" skipped=\"false\"/>" +
    "<object identify_id=\"202\" name=\"failed bracket\" skipped=\"true\"/>" +
    "<object identify_id=\"303\" name=\"right bracket\" skipped=\"false\"/>" +
    "</plate></config>";

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
