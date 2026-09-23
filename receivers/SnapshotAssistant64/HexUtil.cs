using System.Globalization;

namespace SnapshotAssistant;

public static class HexUtil
{
    public const string Zero = "0000000000000000";

    public static string Normalize(string? value)
    {
        if(string.IsNullOrWhiteSpace(value))
            return Zero;
        var text = value.Trim();
        if(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            text = text[2..];
        return ulong.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed)
            ? parsed.ToString("X16", CultureInfo.InvariantCulture)
            : value.Trim().ToUpperInvariant();
    }

    public static bool TryParse(string? value, out ulong result)
    {
        var text = value?.Trim() ?? "";
        if(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            text = text[2..];
        return ulong.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result);
    }

    public static bool HasAddress(string? value) =>
        TryParse(value, out var parsed) && parsed != 0;

    public static string ModuleRva(InstructionInfo? instruction)
    {
        if(instruction is null || string.IsNullOrWhiteSpace(instruction.Module))
            return "未知";
        return $"{instruction.Module}+{Normalize(instruction.ModuleOffset)}";
    }
}
