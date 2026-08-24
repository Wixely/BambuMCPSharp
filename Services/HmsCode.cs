using System.Text.Json.Nodes;

namespace BambuMCPSharp.Services;

/// <summary>
/// Decodes the printer's Health Management System entries. A report carries them as
/// <c>print.hms: [{"attr": &lt;u32&gt;, "code": &lt;u32&gt;}]</c>; the human-facing form is
/// <c>HMS_XXXX_XXXX_XXXX_XXXX</c> (attr high/low sixteen bits, then code high/low), which is
/// also the key Bambu's wiki uses.
/// </summary>
public static class HmsCode
{
    public static JsonObject Decode(JsonObject entry)
    {
        var attr = ReadUInt(entry["attr"]);
        var code = ReadUInt(entry["code"]);

        var parts = new[]
        {
            (attr >> 16) & 0xFFFF,
            attr & 0xFFFF,
            (code >> 16) & 0xFFFF,
            code & 0xFFFF,
        };

        var codeString = $"HMS_{parts[0]:X4}_{parts[1]:X4}_{parts[2]:X4}_{parts[3]:X4}";
        var wikiSlug = $"{parts[0]:x4}-{parts[1]:x4}-{parts[2]:x4}-{parts[3]:x4}";

        return new JsonObject
        {
            ["code"] = codeString,
            ["severity"] = SeverityName((code >> 16) & 0xFFFF),
            ["module"] = ModuleName((attr >> 24) & 0xFF),
            ["wiki"] = $"https://wiki.bambulab.com/en/x1/troubleshooting/hms/{wikiSlug}",
            ["attr"] = attr,
            ["rawCode"] = code,
        };
    }

    private static uint ReadUInt(JsonNode? node)
    {
        try
        {
            return node?.GetValue<uint>() ?? 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return 0;
        }
    }

    private static string SeverityName(uint severity) => severity switch
    {
        1 => "fatal",
        2 => "serious",
        3 => "common",
        4 => "info",
        _ => $"unknown({severity})",
    };

    private static string ModuleName(uint module) => module switch
    {
        0x05 => "mainboard",
        0x0C => "xcam",
        0x07 => "ams",
        0x08 => "toolhead",
        0x03 => "mc",
        _ => $"module-0x{module:X2}",
    };
}
