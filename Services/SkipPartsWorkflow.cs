using System.Globalization;
using System.Text.Json.Nodes;

namespace BambuMCPSharp.Services;

public sealed record ActivePrintIdentity(
    string? TaskId,
    string? SubtaskId,
    string? GcodeFile,
    string? SubtaskName);

public sealed record SkipPartsPlan(
    JsonObject Command,
    ActivePrintIdentity Job,
    IReadOnlyList<ProjectPart> RequestedParts,
    IReadOnlyList<ProjectPart> AlreadySkippedParts,
    IReadOnlyList<ProjectPart> RemainingPartsAfterRequest);

public sealed record SkipPartsVerification(
    string Outcome,
    IReadOnlyList<int> ReportedSkippedObjectIds,
    IReadOnlyList<int> MissingRequestedObjectIds);

/// <summary>
/// Builds and verifies the X1 Skip Parts command from bounded sliced-project metadata and
/// the live printer state. The caller must inspect the project before invoking this class.
/// </summary>
public static class SkipPartsWorkflow
{
    public static SkipPartsPlan CreatePlan(
        JsonObject state,
        GcodeInspection plate,
        string localName,
        IReadOnlyCollection<int> requestedObjectIds)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(plate);
        ArgumentException.ThrowIfNullOrWhiteSpace(localName);
        ArgumentNullException.ThrowIfNull(requestedObjectIds);

        if (!plate.StructurallyValid)
        {
            throw new InvalidOperationException("The selected plate did not pass structural and printer-model inspection.");
        }
        if (!plate.PartsSafelyAddressable)
        {
            var reason = plate.PartFindings.Count > 0
                ? string.Join(" ", plate.PartFindings)
                : "The selected plate has no safe part catalogue.";
            throw new InvalidOperationException(reason);
        }
        if (requestedObjectIds.Count == 0)
        {
            throw new InvalidOperationException("At least one part identify_id is required.");
        }
        if (requestedObjectIds.Count > ProjectInspectionLimits.MaxPartsPerPlate)
        {
            throw new InvalidOperationException("The requested part list exceeds the bounded per-plate limit.");
        }
        if (requestedObjectIds.Distinct().Count() != requestedObjectIds.Count)
        {
            throw new InvalidOperationException("The requested part list contains duplicate identify_id values.");
        }

        var print = state["print"] as JsonObject ??
                    throw new InvalidOperationException("The printer has not reported print state.");
        var gcodeState = ReadString(print["gcode_state"]);
        if (!string.Equals(gcodeState, "RUNNING", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(gcodeState, "PAUSE", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Skip Parts requires a running or paused print; the printer reports gcode_state={gcodeState ?? "?"}.");
        }

        var job = ReadJob(print);
        if (!MatchesLocalProject(localName, job))
        {
            throw new InvalidOperationException(
                $"The active job ({job.GcodeFile ?? job.SubtaskName ?? "unnamed"}) does not match local project '{Path.GetFileName(localName)}'.");
        }

        var partsById = plate.Parts.ToDictionary(part => part.IdentifyId);
        var unknown = requestedObjectIds.Where(id => !partsById.ContainsKey(id)).Order().ToArray();
        if (unknown.Length > 0)
        {
            throw new InvalidOperationException(
                $"Unknown part identify_id value(s): {string.Join(", ", unknown)}. Re-run bambu_inspect_project.");
        }

        var reportedSkippedIds = ReadSkippedObjectIds(state).ToHashSet();
        var alreadySkipped = plate.Parts
            .Where(part => part.PreSkipped || reportedSkippedIds.Contains(part.IdentifyId))
            .ToArray();
        var alreadySkippedRequested = requestedObjectIds
            .Where(id => partsById[id].PreSkipped || reportedSkippedIds.Contains(id))
            .Order()
            .ToArray();
        if (alreadySkippedRequested.Length > 0)
        {
            throw new InvalidOperationException(
                $"Part identify_id value(s) already skipped: {string.Join(", ", alreadySkippedRequested)}.");
        }

        var available = plate.Parts
            .Where(part => !part.PreSkipped && !reportedSkippedIds.Contains(part.IdentifyId))
            .ToArray();
        var requestedSet = requestedObjectIds.ToHashSet();
        if (available.All(part => requestedSet.Contains(part.IdentifyId)))
        {
            throw new InvalidOperationException(
                "Refusing to skip every remaining part. Stop the print explicitly if no part should continue.");
        }

        var requested = requestedObjectIds.Select(id => partsById[id]).ToArray();
        var remaining = available.Where(part => !requestedSet.Contains(part.IdentifyId)).ToArray();
        var command = new JsonObject
        {
            ["command"] = "skip_objects",
            ["obj_list"] = new JsonArray(requestedObjectIds.Select(id => (JsonNode)id).ToArray()),
        };
        return new SkipPartsPlan(command, job, requested, alreadySkipped, remaining);
    }

    public static SkipPartsVerification Verify(JsonObject state, SkipPartsPlan plan)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(plan);

        var print = state["print"] as JsonObject;
        if (print is null || !SameJob(plan.Job, ReadJob(print)))
        {
            return new SkipPartsVerification("job_changed", ReadSkippedObjectIds(state), plan.RequestedParts.Select(part => part.IdentifyId).ToArray());
        }

        var reported = ReadSkippedObjectIds(state);
        var reportedSet = reported.ToHashSet();
        var missing = plan.RequestedParts
            .Select(part => part.IdentifyId)
            .Where(id => !reportedSet.Contains(id))
            .ToArray();
        return new SkipPartsVerification(
            missing.Length == 0 ? "verified" : "not_reported",
            reported,
            missing);
    }

    public static IReadOnlyList<int> ReadSkippedObjectIds(JsonObject state)
    {
        var values = state["print"]?["s_obj"] as JsonArray;
        if (values is null) return [];

        return values
            .Select(ReadInt)
            .Where(value => value is >= 0 and <= 0x00FF_FFFF)
            .Select(value => value!.Value)
            .Distinct()
            .Order()
            .Take(ProjectInspectionLimits.MaxPartsPerPlate)
            .ToArray();
    }

    private static ActivePrintIdentity ReadJob(JsonObject print) => new(
        ReadString(print["task_id"]),
        ReadString(print["subtask_id"]),
        ReadString(print["gcode_file"]),
        ReadString(print["subtask_name"]));

    private static bool SameJob(ActivePrintIdentity expected, ActivePrintIdentity actual)
    {
        if (MeaningfulId(expected.SubtaskId) && MeaningfulId(actual.SubtaskId))
        {
            return string.Equals(expected.SubtaskId, actual.SubtaskId, StringComparison.Ordinal);
        }
        if (MeaningfulId(expected.TaskId) && MeaningfulId(actual.TaskId))
        {
            return string.Equals(expected.TaskId, actual.TaskId, StringComparison.Ordinal);
        }

        var expectedFileNames = NameVariants(expected.GcodeFile).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actualFileNames = NameVariants(actual.GcodeFile).ToArray();
        if (expectedFileNames.Count > 0 && actualFileNames.Length > 0)
        {
            return actualFileNames.Any(expectedFileNames.Contains);
        }

        var expectedSubtaskNames = NameVariants(expected.SubtaskName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return NameVariants(actual.SubtaskName).Any(expectedSubtaskNames.Contains);
    }

    private static bool MatchesLocalProject(string localName, ActivePrintIdentity job)
    {
        var localNames = NameVariants(localName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reportedFileNames = NameVariants(job.GcodeFile).ToArray();
        return reportedFileNames.Length > 0
            ? reportedFileNames.Any(localNames.Contains)
            : NameVariants(job.SubtaskName).Any(localNames.Contains);
    }

    private static IEnumerable<string> NameVariants(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) yield break;
        var normalized = value.Replace('\\', '/');
        var name = normalized[(normalized.LastIndexOf('/') + 1)..].Trim();
        if (name.Length == 0) yield break;
        yield return name;

        var withoutExtension = Path.GetFileNameWithoutExtension(name);
        if (!string.Equals(withoutExtension, name, StringComparison.OrdinalIgnoreCase)) yield return withoutExtension;
        if (withoutExtension.EndsWith(".gcode", StringComparison.OrdinalIgnoreCase))
        {
            yield return withoutExtension[..^".gcode".Length];
        }
    }

    private static bool MeaningfulId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value != "0";

    private static int? ReadInt(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<int>(out var integer)) return integer;
        if (value.TryGetValue<long>(out var longer) && longer is >= int.MinValue and <= int.MaxValue) return (int)longer;
        return value.TryGetValue<string>(out var text) &&
               int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out integer)
            ? integer
            : null;
    }

    private static string? ReadString(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<string>(out var text)) return text;
        return node.ToJsonString();
    }
}
