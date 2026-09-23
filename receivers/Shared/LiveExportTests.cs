using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SnapshotAssistant;

internal static class LiveExportTests
{
    public static async Task Run(string temporaryDirectory, string architecture)
    {
        var store = new SnapshotStore();
        var coordinator = new EventCoordinator(store, new AppLogger(Path.Combine(temporaryDirectory, "export.log")));
        var directory = Path.Combine(temporaryDirectory, "live");
        using var writer = new LiveStateExporter(architecture, 0, directory);
        JsonElement Read() => JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(writer.OutputPath));
        void Check(bool ok, string message)
        {
            if(!ok) throw new Exception("Live export: " + message);
            Console.WriteLine("PASS live export " + message);
        }
        writer.Write(coordinator, true, false);
        var initial = Read();
        Check(initial.GetProperty("state").GetProperty("cpu").ValueKind == JsonValueKind.Null, "startup clears previous data");
        var instance = initial.GetProperty("receiver").GetProperty("instance_id").GetString();
        var ip = architecture == "x32" ? "00530170" : "00007FF612345670";
        var browse = architecture == "x32" ? "00531000" : "00007FF61234A000";
        string Snapshot(long sequence, string type, string state, string selected) => JsonSerializer.Serialize(new
        {
            schema_version = 2, source = "export-test", session_id = "EXPORT-TEST",
            sequence, timestamp_ms = 1800000000000L + sequence, event_type = type,
            debug_state = state, window = "disassembly", registers_valid = true,
            snapshot_consistent = true, eip = ip, rip = ip, selected_address = selected,
            registers = new Dictionary<string, string> { [architecture == "x32" ? "EIP" : "RIP"] = ip },
            stack = new[] { new { address = ip, offset = "0", value = ip } }
        });

        await using var server = new TcpEventServer(0);
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.RawMessageReceived += raw =>
        {
            coordinator.ProcessRaw(raw, EventOrigin.Tcp);
            accepted.TrySetResult();
        };
        server.Start();
        using(var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, server.BoundPort);
            await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes(Snapshot(1, "debug_paused", "paused", ip)));
            client.Client.Shutdown(SocketShutdown.Send);
        }
        await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.ProcessRaw(Snapshot(2, "selection_changed", "paused", browse), EventOrigin.Tcp);
        writer.Write(coordinator, true, true);
        var state = Read().GetProperty("state");
        Check(state.GetProperty("cpu").GetProperty("eip").GetString() == ip &&
            state.GetProperty("browse").GetProperty("selected_address").GetString() == browse, "TCP CPU and browsing stay separate; full-width address retained");
        Check(state.GetProperty("cpu").GetProperty("stack").GetArrayLength() == 1, "stack retained");
        coordinator.ProcessRaw(Snapshot(3, "debug_resumed", "running", browse), EventOrigin.Tcp);
        coordinator.ProcessRaw("{\"event\":\"xrefs_result\",\"target_address\":\"" + ip + "\",\"count\":0,\"references\":[]}", EventOrigin.Tcp);
        writer.Write(coordinator, true, true);
        state = Read().GetProperty("state");
        Check(state.GetProperty("debug_state").GetString() == "running" && state.GetProperty("cpu").GetProperty("eip").GetString() == ip, "running status does not claim saved pause is live");
        Check(state.GetProperty("xref_copy_pending").GetBoolean(), "XREF pending preserved without consuming clipboard state");
        store.ConsumeXrefsAfterSuccessfulCopy(store.LatestXrefs!);
        writer.Write(coordinator, true, true);
        Check(!Read().GetProperty("state").GetProperty("xref_copy_pending").GetBoolean(), "copy consumption reflected");
        store.ClearXrefs();
        coordinator.ProcessRaw(Snapshot(4, "debug_paused", "paused", ip), EventOrigin.Fallback);
        writer.Write(coordinator, true, true);
        state = Read().GetProperty("state");
        Check(state.GetProperty("xrefs").ValueKind == JsonValueKind.Null &&
            state.GetProperty("cpu").GetProperty("origin").GetString() == "Fallback", "clear and fallback provenance reflected");
        var before = File.ReadAllText(writer.OutputPath);
        using(var competing = new LiveStateExporter(architecture, 0, directory))
        {
            var blocked = false;
            try { competing.Write(coordinator, true, true); } catch(IOException) { blocked = true; }
            Check(blocked && File.ReadAllText(writer.OutputPath) == before, "second writer cannot replace active export");
        }
        // Hold an old reader open while the next whole file is atomically published.
        using(var reader = new FileStream(writer.OutputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            writer.Write(coordinator, true, true);
            using var oldJson = JsonDocument.Parse(reader);
            Check(oldJson.RootElement.GetProperty("export_schema_version").GetInt32() == 1, "reader never sees a partial document");
        }
        writer.Write(coordinator, false, false);
        Check(!Read().GetProperty("receiver").GetProperty("running").GetBoolean(), "clean shutdown recorded");
        writer.Dispose();
        using var restarted = new LiveStateExporter(architecture, 0, directory);
        restarted.Write(new EventCoordinator(new SnapshotStore(), coordinator.Logger), true, false);
        Check(Read().GetProperty("receiver").GetProperty("instance_id").GetString() != instance &&
            Read().GetProperty("state").GetProperty("cpu").ValueKind == JsonValueKind.Null, "restart has new identity and no stale CPU");
        var blockedDirectory = Path.Combine(temporaryDirectory, "not-a-directory");
        File.WriteAllText(blockedDirectory, "test");
        using var failedWriter = new LiveStateExporter(architecture, 0, blockedDirectory);
        var failed = false;
        try { failedWriter.Write(coordinator, true, true); } catch(IOException) { failed = true; }
        Check(failed && coordinator.ProcessRaw(Snapshot(5, "step_completed", "paused", ip), EventOrigin.Tcp) == ProcessResult.Added,
            "export IO failure does not corrupt receiving state");
    }
}
