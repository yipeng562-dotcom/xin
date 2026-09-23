using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SnapshotAssistant;

// Called on the UI thread: capture under the coordinator lock, then perform IO
// outside that lock. Clipboard/XREF UI actions are reflected on the next tick.
public sealed class LiveStateExporter : IDisposable
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly string _architecture;
    private readonly int _port;
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private FileStream? _lease;
    public string OutputPath { get; }

    public LiveStateExporter(string architecture, int port, string? directory = null)
    {
        _architecture = architecture;
        _port = port;
        var expectedPort = architecture == "x32" ? 8765 : 8768;
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexDbg", architecture, port == expectedPort ? "live" : $"port-{port}");
        OutputPath = Path.Combine(Path.GetFullPath(directory), "latest.json");
    }

    public void Write(EventCoordinator coordinator, bool running, bool listening)
    {
        var state = coordinator.CaptureLiveState();
        var document = new
        {
            ExportSchemaVersion = 1,
            TargetArchitecture = _architecture,
            ExportedAtUtc = DateTimeOffset.UtcNow,
            Receiver = new
            {
                InstanceId = _instanceId, ProcessId = Environment.ProcessId,
                StartedAtUtc = _startedAt, Running = running, Listening = listening,
                Port = _port, Executable = Environment.ProcessPath
            },
            State = state
        };
        var json = JsonSerializer.Serialize(document, Options);
        var directory = Path.GetDirectoryName(OutputPath)!;
        Directory.CreateDirectory(directory);
        // A second receiver may not overwrite this receiver's export.
        _lease ??= new FileStream(Path.Combine(directory, "writer.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        var temporary = Path.Combine(directory, $".{_instanceId}.tmp");
        try
        {
            File.WriteAllText(temporary, json, new UTF8Encoding(false));
            if(File.Exists(OutputPath)) File.Replace(temporary, OutputPath, null);
            else File.Move(temporary, OutputPath);
        }
        finally
        {
            if(File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public void Dispose() => _lease?.Dispose();
}
