namespace SnapshotAssistant;

public enum EventOrigin
{
    Tcp,
    Fallback
}

public abstract class ReceivedEvent
{
    public required string RawJson { get; init; }
    public required string DedupKey { get; init; }
    public EventOrigin Origin { get; init; }
    public DateTime ReceivedAt { get; init; } = DateTime.Now;
}

public sealed class InstructionInfo
{
    public string Address { get; init; } = HexUtil.Zero;
    public string Module { get; init; } = "";
    public string ModuleOffset { get; init; } = HexUtil.Zero;
    public string CallTarget { get; init; } = HexUtil.Zero;
    public string Label { get; init; } = "";
    public string Instruction { get; init; } = "";
    public string Bytes { get; init; } = "";
    public int Size { get; init; }
}

public sealed class StackSlot
{
    public string Offset { get; init; } = HexUtil.Zero;
    public string Address { get; init; } = HexUtil.Zero;
    public string Value { get; init; } = HexUtil.Zero;
}

public sealed class SnapshotEvent : ReceivedEvent
{
    public int SchemaVersion { get; init; }
    public string Source { get; init; } = "";
    public string SessionId { get; init; } = "";
    public long Sequence { get; init; }
    public long TimestampMs { get; init; }
    public string EventType { get; init; } = "unknown";
    public string Action { get; init; } = "";
    public string Window { get; init; } = "";
    public string DebugState { get; init; } = "unknown";
    public bool RegistersValid { get; init; }
    public bool SnapshotConsistent { get; init; }
    public string SelectedAddress { get; init; } = HexUtil.Zero;
    public string PreviousSelectedAddress { get; init; } = HexUtil.Zero;
    public string Eip { get; init; } = HexUtil.Zero;
    public bool SelectionEqualsEip { get; init; }
    public InstructionInfo? SelectedInstruction { get; init; }
    public InstructionInfo? CurrentInstruction { get; init; }
    public IReadOnlyDictionary<string, string> Registers { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<StackSlot> Stack { get; init; } = Array.Empty<StackSlot>();
    public IReadOnlyList<InstructionInfo> DisassemblyContext { get; init; } = Array.Empty<InstructionInfo>();
    public IReadOnlyList<InstructionInfo> BrowseDisassemblyContext { get; init; } = Array.Empty<InstructionInfo>();
}

public sealed class XrefReference
{
    public string Address { get; init; } = HexUtil.Zero;
    public string Type { get; init; } = "UNKNOWN";
    public string Instruction { get; init; } = "";
    public string Module { get; init; } = "";
    public string Rva { get; init; } = HexUtil.Zero;
}

public sealed class XrefsResultEvent : ReceivedEvent
{
    public string Source { get; init; } = "";
    public string Query { get; init; } = "";
    public string CopyMode { get; init; } = "contextual";
    public string TargetAddress { get; init; } = HexUtil.Zero;
    public int Count { get; init; }
    public string Error { get; init; } = "";
    public IReadOnlyList<XrefReference> References { get; init; } = Array.Empty<XrefReference>();
}

public sealed class XrefContextLine
{
    public string Address { get; init; } = HexUtil.Zero;
    public bool Current { get; init; }
    public string Instruction { get; init; } = "";
    public string Module { get; init; } = "";
    public string Rva { get; init; } = HexUtil.Zero;
}

public sealed class XrefContextEvent : ReceivedEvent
{
    public string TargetAddress { get; init; } = HexUtil.Zero;
    public string ReferenceAddress { get; init; } = HexUtil.Zero;
    public int Before { get; init; }
    public int After { get; init; }
    public string Error { get; init; } = "";
    public IReadOnlyList<XrefContextLine> Context { get; init; } = Array.Empty<XrefContextLine>();
}
