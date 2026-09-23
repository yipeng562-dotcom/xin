namespace SnapshotAssistant;

public readonly record struct ImportResult(int Added, int Duplicate, int Invalid);

public static class FallbackImporter
{
    public static string DefaultPath => Path.Combine(Path.GetTempPath(), "x64dbg_ai_events.jsonl");

    public static ImportResult Import(string path, EventCoordinator coordinator)
    {
        var added = 0;
        var duplicate = 0;
        var invalid = 0;
        foreach(var line in File.ReadLines(path))
        {
            if(string.IsNullOrWhiteSpace(line))
                continue;
            switch(coordinator.ProcessRaw(line, EventOrigin.Fallback))
            {
            case ProcessResult.Added: added++; break;
            case ProcessResult.Duplicate: duplicate++; break;
            case ProcessResult.Invalid: invalid++; break;
            }
        }
        return new ImportResult(added, duplicate, invalid);
    }
}
