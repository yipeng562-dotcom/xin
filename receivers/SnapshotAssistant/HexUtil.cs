using System.Globalization;

namespace SnapshotAssistant;

public static class HexUtil
{
    public static string Normalize(string? value)
    {
        if(string.IsNullOrWhiteSpace(value))
            return "00000000";
        var text = value.Trim();
        if(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            text = text[2..];
        return uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed)
            ? parsed.ToString("X8", CultureInfo.InvariantCulture)
            : value.Trim().ToUpperInvariant();
    }

    public static bool TryParse(string? value, out uint result)
    {
        var text = value?.Trim() ?? "";
        if(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            text = text[2..];
        return uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result);
    }

    public static string ModuleRva(InstructionInfo? instruction)
    {
        if(instruction is null || string.IsNullOrWhiteSpace(instruction.Module))
            return "未知";
        return $"{instruction.Module}+{Normalize(instruction.ModuleOffset)}";
    }
}
