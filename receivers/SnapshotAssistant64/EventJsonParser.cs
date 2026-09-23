using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SnapshotAssistant;

public static class EventJsonParser
{
    public static bool TryParse(string raw, EventOrigin origin, out ReceivedEvent? received, out string error)
    {
        received = null;
        error = "";
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if(root.ValueKind != JsonValueKind.Object)
            {
                error = "JSON根节点不是对象";
                return false;
            }

            if(root.TryGetProperty("data", out var wrapped) && wrapped.ValueKind == JsonValueKind.Object)
                root = wrapped;

            var normalizedRaw = root.GetRawText();
            var eventName = Text(root, "event");
            if(eventName.Equals("xrefs_result", StringComparison.OrdinalIgnoreCase))
            {
                received = ParseXrefs(root, normalizedRaw, origin);
                return true;
            }
            if(eventName.Equals("xref_context_result", StringComparison.OrdinalIgnoreCase))
            {
                received = ParseXrefContext(root, normalizedRaw, origin);
                return true;
            }
            if(root.TryGetProperty("event_type", out _))
            {
                received = ParseSnapshot(root, normalizedRaw, origin);
                return true;
            }

            error = "未识别的事件格式";
            return false;
        }
        catch(JsonException ex)
        {
            error = ex.Message;
            return false;
        }
        catch(Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static SnapshotEvent ParseSnapshot(JsonElement root, string raw, EventOrigin origin)
    {
        var session = Text(root, "session_id");
        var sequence = Integer64(root, "sequence", Integer64(root, "event_sequence"));
        var registers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if(root.TryGetProperty("registers", out var registerElement) && registerElement.ValueKind == JsonValueKind.Object)
        {
            foreach(var item in registerElement.EnumerateObject())
                registers[item.Name.ToUpperInvariant()] = HexUtil.Normalize(item.Value.ToString());
        }

        var stack = new List<StackSlot>();
        if(root.TryGetProperty("stack", out var stackElement) && stackElement.ValueKind == JsonValueKind.Array)
        {
            foreach(var item in stackElement.EnumerateArray())
            {
                stack.Add(new StackSlot
                {
                    Offset = HexUtil.Normalize(Text(item, "offset")),
                    Address = HexUtil.Normalize(Text(item, "address")),
                    Value = HexUtil.Normalize(Text(item, "value"))
                });
            }
        }

        var context = new List<InstructionInfo>();
        if(root.TryGetProperty("disassembly_context", out var contextElement) && contextElement.ValueKind == JsonValueKind.Array)
        {
            foreach(var item in contextElement.EnumerateArray())
                context.Add(ParseInstruction(item)!);
        }

        var eip = HexUtil.Normalize(Text(root, "rip", Text(root, "eip")));
        if(!HexUtil.HasAddress(eip) && registers.TryGetValue("RIP", out var registerRip))
            eip = registerRip;
        if(!HexUtil.HasAddress(eip) && registers.TryGetValue("EIP", out var registerEip))
            eip = registerEip;

        return new SnapshotEvent
        {
            RawJson = raw,
            DedupKey = sequence > 0 && session.Length > 0 ? $"snapshot:{session}:{sequence}" : $"raw:{Hash(raw)}",
            Origin = origin,
            SchemaVersion = Integer(root, "schema_version"),
            Source = Text(root, "source"),
            SessionId = session,
            Sequence = sequence,
            TimestampMs = Integer64(root, "timestamp_ms"),
            EventType = Text(root, "event_type", "unknown"),
            Action = Text(root, "action"),
            Window = Text(root, "window"),
            DebugState = Text(root, "debug_state", "unknown"),
            RegistersValid = Boolean(root, "registers_valid"),
            SnapshotConsistent = Boolean(root, "snapshot_consistent"),
            SelectedAddress = HexUtil.Normalize(Text(root, "selected_address")),
            PreviousSelectedAddress = HexUtil.Normalize(Text(root, "previous_selected_address")),
            Eip = eip,
            SelectionEqualsEip = Boolean(root, "selection_equals_eip"),
            SelectedInstruction = PropertyObject(root, "selected_instruction", ParseInstruction),
            CurrentInstruction = PropertyObject(root, "current_instruction", ParseInstruction),
            Registers = registers,
            Stack = stack,
            DisassemblyContext = context
                .Where(item => item is not null).ToArray(),
            BrowseDisassemblyContext = root.TryGetProperty("browse_disassembly_context", out var browseContext) &&
                browseContext.ValueKind == JsonValueKind.Array
                ? browseContext.EnumerateArray().Select(ParseInstruction).OfType<InstructionInfo>().ToArray()
                : context.Where(item => item is not null).ToArray()
        };
    }

    private static XrefsResultEvent ParseXrefs(JsonElement root, string raw, EventOrigin origin)
    {
        var references = new List<XrefReference>();
        if(root.TryGetProperty("references", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach(var item in array.EnumerateArray())
            {
                references.Add(new XrefReference
                {
                    Address = HexUtil.Normalize(Text(item, "address", Text(item, "reference_address"))),
                    Type = Text(item, "type", Text(item, "reference_type", "UNKNOWN")),
                    Instruction = Text(item, "instruction"),
                    Module = Text(item, "module"),
                    Rva = HexUtil.Normalize(Text(item, "rva"))
                });
            }
        }
        return new XrefsResultEvent
        {
            RawJson = raw,
            DedupKey = $"xref:{Hash(raw)}",
            Origin = origin,
            Source = Text(root, "source"),
            Query = Text(root, "query"),
            CopyMode = Text(root, "copy_mode", "contextual"),
            TargetAddress = HexUtil.Normalize(Text(root, "target_address")),
            Count = Integer(root, "count", references.Count),
            Error = Text(root, "error"),
            References = references
        };
    }

    private static XrefContextEvent ParseXrefContext(JsonElement root, string raw, EventOrigin origin)
    {
        var lines = new List<XrefContextLine>();
        if(root.TryGetProperty("context", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach(var item in array.EnumerateArray())
            {
                lines.Add(new XrefContextLine
                {
                    Address = HexUtil.Normalize(Text(item, "address")),
                    Current = Boolean(item, "current"),
                    Instruction = Text(item, "instruction"),
                    Module = Text(item, "module"),
                    Rva = HexUtil.Normalize(Text(item, "rva"))
                });
            }
        }
        return new XrefContextEvent
        {
            RawJson = raw,
            DedupKey = $"xref-context:{Hash(raw)}",
            Origin = origin,
            TargetAddress = HexUtil.Normalize(Text(root, "target_address")),
            ReferenceAddress = HexUtil.Normalize(Text(root, "reference_address")),
            Before = Integer(root, "before"),
            After = Integer(root, "after"),
            Error = Text(root, "error"),
            Context = lines
        };
    }

    private static InstructionInfo? ParseInstruction(JsonElement item)
    {
        if(item.ValueKind != JsonValueKind.Object)
            return null;
        return new InstructionInfo
        {
            Address = HexUtil.Normalize(Text(item, "address")),
            Module = Text(item, "module"),
            ModuleOffset = HexUtil.Normalize(Text(item, "module_offset", Text(item, "rva"))),
            CallTarget = HexUtil.Normalize(Text(item, "call_target")),
            Label = Text(item, "label"),
            Instruction = Text(item, "instruction"),
            Bytes = Text(item, "bytes"),
            Size = Integer(item, "size")
        };
    }

    private static T? PropertyObject<T>(JsonElement root, string name, Func<JsonElement, T?> factory) where T : class
    {
        return root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? factory(value)
            : null;
    }

    private static string Text(JsonElement root, string name, string fallback = "")
    {
        if(!root.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return fallback;
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : value.ToString();
    }

    private static bool Boolean(JsonElement root, string name, bool fallback = false)
    {
        if(!root.TryGetProperty(name, out var value))
            return fallback;
        return value.ValueKind == JsonValueKind.True ||
               value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed) && parsed;
    }

    private static int Integer(JsonElement root, string name, int fallback = 0)
    {
        return root.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : fallback;
    }

    private static long Integer64(JsonElement root, string name, long fallback = 0)
    {
        return root.TryGetProperty(name, out var value) && value.TryGetInt64(out var parsed) ? parsed : fallback;
    }

    private static string Hash(string text)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
