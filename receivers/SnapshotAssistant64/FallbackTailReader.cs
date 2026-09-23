using System.Text;
using System.Text.Json;

namespace SnapshotAssistant;

// Only Snapshot events are recovered automatically: historical XREF must not be rearmed.
public sealed class FallbackTailReader
{
    private long _offset;
    private readonly List<byte> _pending = new();
    private bool _started;
    public int ReadAvailable(string path, EventCoordinator coordinator)
    {
        if(!File.Exists(path)) return 0;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if(stream.Length < _offset) { _offset = 0; _pending.Clear(); }
        if(!_started)
        {
            // Bound startup I/O even after a long debugger session.
            _offset = Math.Max(0, stream.Length - 4 * 1024 * 1024);
            _started = true;
            stream.Position = _offset;
            if(_offset > 0)
            {
                int value;
                do { value = stream.ReadByte(); } while(value >= 0 && value != '\n');
                _offset = stream.Position;
            }
        }
        stream.Position = _offset;
        var buffer = new byte[8192];
        var added = 0;
        var budget = 4 * 1024 * 1024;
        while(budget > 0)
        {
            var count = stream.Read(buffer, 0, Math.Min(buffer.Length, budget));
            if(count == 0) break;
            budget -= count;
            _offset += count;
            for(var i = 0; i < count; i++)
            {
                if(buffer[i] != '\n') { _pending.Add(buffer[i]); continue; }
                var line = Encoding.UTF8.GetString(_pending.ToArray());
                _pending.Clear();
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if(root.ValueKind != JsonValueKind.Object) continue;
                    if(root.TryGetProperty("data", out var data)) root = data;
                    if(root.ValueKind != JsonValueKind.Object ||
                       !root.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.String ||
                       source.GetString() != "x64dbg_context_v2" ||
                       !root.TryGetProperty("event_type", out _)) continue;
                    if(coordinator.ProcessRaw(line, EventOrigin.Fallback) == ProcessResult.Added) added++;
                }
                catch(JsonException) { /* A malformed line must not block later complete events. */ }
            }
            if(_pending.Count > 4 * 1024 * 1024) _pending.Clear();
        }
        return added;
    }
}
