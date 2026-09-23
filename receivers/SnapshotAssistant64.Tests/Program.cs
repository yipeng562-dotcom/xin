using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SnapshotAssistant;

internal static class Program
{
    private const string RipA = "00007FF612345670";
    private const string RipB = "00007FF612345675";
    private const string Browse = "00007FF61234A538";
    private static int _failures;

    [STAThread]
    private static async Task<int> Main()
    {
        ApplicationConfiguration.Initialize();
        var tempDir = Path.Combine(Path.GetTempPath(), "SnapshotAssistant64Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            await LiveExportTests.Run(tempDir, "x64");
            TestParserAndState(tempDir);
            TestFormatting();
            TestPlainTextClipboard();
            TestXrefsAndOneShotCopy();
            TestFallbackDedup(tempDir);
            TestReceiveRegressions(tempDir);
            await TestTcpReconnect();
            TestUi();
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }

        Console.WriteLine(_failures == 0 ? "ALL SNAPSHOT ASSISTANT 64 TESTS PASSED" : $"FAILED: {_failures}");
        return _failures == 0 ? 0 : 1;
    }

    private static void TestParserAndState(string tempDir)
    {
        var store = new SnapshotStore();
        var coordinator = new EventCoordinator(store, new AppLogger(Path.Combine(tempDir, "state.log")));
        Expect(coordinator.ProcessRaw(SnapshotJson(100, "debug_paused", "paused", RipA, RipA), EventOrigin.Tcp) == ProcessResult.Added,
            "x64 pause JSON accepted");
        var cpu = store.LastCpuSnapshot;
        Expect(cpu?.Eip == RipA && cpu.Registers["RIP"] == RipA, "true 64-bit RIP parsed");
        Expect(cpu?.Registers.ContainsKey("R15") == true, "x64 R8-R15 registers retained");
        Expect(cpu?.Stack.Count == 16, "all 16 QWORD stack slots retained");

        coordinator.ProcessRaw(SnapshotJson(101, "selection_changed", "paused", RipA, Browse), EventOrigin.Tcp);
        Expect(ReferenceEquals(cpu, store.LastCpuSnapshot), "selection does not overwrite fixed CPU snapshot");
        Expect(store.CurrentSelectionSnapshot?.SelectedAddress == Browse, "64-bit browse address retained");

        coordinator.ProcessRaw(SnapshotJson(102, "debug_resumed", "running", RipA, Browse), EventOrigin.Tcp);
        Expect(ReferenceEquals(cpu, store.LastCpuSnapshot) && store.DebugState == "running",
            "resume retains last paused CPU snapshot");

        var wrapped = $"{{\"source\":\"live_context_fallback\",\"port\":8768,\"data\":{SnapshotJson(103, "debug_paused", "paused", RipB, RipB)}}}";
        Expect(EventJsonParser.TryParse(wrapped, EventOrigin.Fallback, out var parsed, out _) && parsed is SnapshotEvent,
            "x64 fallback envelope parsed");
        Expect(coordinator.ProcessRaw("{bad json", EventOrigin.Tcp) == ProcessResult.Invalid, "invalid JSON skipped without poisoning receiver");
    }

    private static void TestFormatting()
    {
        var snapshot = ParseSnapshot(SnapshotJson(200, "debug_paused", "paused", RipA, RipA,
            instruction: "call 00007FF612340000", size: 5, callTarget: "00007FF612340000", flags: "0000000000000845"));
        var compact = SnapshotFormatter.FormatCpu(snapshot, false, false);
        var full = SnapshotFormatter.FormatCpu(snapshot, true, true);
        Expect(compact.Contains($"真实RIP：{RipA}"), "CPU report labels true RIP");
        Expect(compact.Contains("CPU/浏览：一致") && compact.Contains("现场有效：是"), "CPU relationship and validity shown");
        Expect(compact.Contains("RAX=") && compact.Contains("R15=") && compact.Contains("RFLAGS="), "x64 register report complete");
        Expect(compact.Contains("RSP+18") && !compact.Contains("RSP+20"), "default report has four QWORD stack slots");
        Expect(full.Contains("RSP+78"), "expanded report has sixteen QWORD stack slots");
        Expect(compact.Contains("CF=1") && compact.Contains("ZF=1") && compact.Contains("OF=1"), "RFLAGS decoded");
        Expect(SnapshotFormatter.TryGetCall(snapshot.CurrentInstruction, out var call) &&
               call.Target == "00007FF612340000" && call.ReturnAddress == RipB,
            "64-bit CALL target and theoretical return address");

        var selection = ParseSnapshot(SnapshotJson(201, "selection_changed", "paused", RipA, Browse));
        var browse = SnapshotFormatter.FormatBrowse(selection, snapshot);
        var cpuWithBrowse = SnapshotFormatter.FormatCpu(snapshot, false, false, selection);
        Expect(cpuWithBrowse.Contains($"浏览地址：{Browse}") && cpuWithBrowse.Contains("CPU/浏览：不一致"),
            "CPU header uses current 64-bit browse position");
        Expect(browse.Contains("当前浏览位置【静态】") && browse.Contains("CPU没有执行到浏览地址"),
            "static browse warning is preserved");

        var next = ParseSnapshot(SnapshotJson(202, "step_completed", "paused", RipB, RipB, flags: "0000000000000805"));
        var difference = SnapshotFormatter.FormatComparison(next, snapshot);
        Expect(difference.Contains("RIP：") && difference.Contains("ZF：1 → 0"), "x64 snapshot comparison works");
    }

    private static void TestPlainTextClipboard()
    {
        const string expected = "=====\r\ndebug_paused\r\n<sub_7FF612345670>\r\n普通 空格";
        string actual = "";
        string[] formats = [];
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var savedClipboard = Clipboard.GetDataObject();
                try
                {
                PlainTextClipboard.SetText(expected);
                actual = Clipboard.GetText(TextDataFormat.UnicodeText);
                formats = Clipboard.GetDataObject()?.GetFormats(false) ?? [];
                }
                finally
                {
                    if(savedClipboard is not null) Clipboard.SetDataObject(savedClipboard, true);
                    else Clipboard.Clear();
                }
            }
            catch(Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Expect(failure is null && actual == expected, "Unicode plain text clipboard preserves raw characters");
        Expect(formats.SequenceEqual([DataFormats.UnicodeText]), "clipboard publishes UnicodeText only");
        Expect(!actual.Contains("\\=====") && !actual.Contains("&#x20;"), "clipboard contains no markup escaping");
    }

    private static void TestXrefsAndOneShotCopy()
    {
        var xrefs = ParseEvent(XrefsJson(RipA, "contextual"));
        var parsed = (XrefsResultEvent)xrefs;
        Expect(parsed.Count == 2 && parsed.References[0].Address == Browse, "64-bit XREF list parsed");
        Expect(SnapshotFormatter.FormatXrefs(parsed, "CALL").Contains(Browse), "XREF type filter works");

        var contextJson = $$"""
        {"event":"xref_context_result","target_address":"{{RipA}}","reference_address":"{{Browse}}","before":10,"after":5,
         "context":[{"address":"{{Browse}}","current":true,"instruction":"call {{RipA}}","module":"sample64","rva":"A538"}]}
        """;
        Expect(ParseEvent(contextJson) is XrefContextEvent { Context.Count: 1 }, "XREF static context parsed");

        var store = new SnapshotStore();
        store.Add(ParseSnapshot(SnapshotJson(300, "debug_paused", "paused", RipA, RipA)));
        store.Add(xrefs);
        var decision = SmartCopyComposer.Compose(store);
        Expect(store.XrefCopyPending && decision.IncludesXref && decision.Text.Contains("XREF / 引用"),
            "new XREF is attached to next matching smart copy");
        var copied = decision.IncludedXrefs!;
        Expect(store.ConsumeXrefsAfterSuccessfulCopy(copied) && !store.XrefCopyPending,
            "successful copy consumes XREF pending state once");
        decision = SmartCopyComposer.Compose(store);
        Expect(!decision.IncludesXref && store.LatestXrefs == copied, "XREF remains visible but is not copied twice");
        Expect(store.RearmXrefsForCopy() && SmartCopyComposer.Compose(store).IncludesXref,
            "manual XREF rearm works");
        store.ClearXrefs();
        Expect(store.LatestXrefs is null && !store.XrefCopyPending, "clear XREF clears UI and copy state");

        var xrefOnly = new SnapshotStore();
        xrefOnly.Add(ParseEvent(XrefsJson(RipA, "xref_only")));
        decision = SmartCopyComposer.Compose(xrefOnly);
        Expect(decision.ButtonText == "复制 XREF" && !decision.Text.Contains("CPU真实现场"),
            "CPU disassembly menu produces XREF-only copy");

    }

    private static void TestFallbackDedup(string tempDir)
    {
        var path = Path.Combine(tempDir, "fallback.jsonl");
        using var document = JsonDocument.Parse(SnapshotJson(400, "debug_paused", "paused", RipA, RipA));
        var line = $"{{\"source\":\"live_context_fallback\",\"port\":8768,\"data\":{JsonSerializer.Serialize(document.RootElement)}}}";
        File.WriteAllLines(path, [line, line]);
        var coordinator = new EventCoordinator(new SnapshotStore(), new AppLogger(Path.Combine(tempDir, "fallback.log")));
        var first = FallbackImporter.Import(path, coordinator);
        var second = FallbackImporter.Import(path, coordinator);
        Expect(first.Added == 1 && first.Duplicate == 1 && second.Added == 0, "fallback duplicate events are ignored");
    }

    private static async Task TestTcpReconnect()
    {
        await using var server = new TcpEventServer(0);
        var count = 0;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.RawMessageReceived += _ =>
        {
            if(Interlocked.Increment(ref count) == 2)
                completed.TrySetResult();
        };
        server.Start();
        await Send(server.BoundPort, SnapshotJson(500, "debug_paused", "paused", RipA, RipA));
        await Send(server.BoundPort, SnapshotJson(501, "step_completed", "paused", RipB, RipB));
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Expect(count == 2 && server.IsListening, "TCP listener accepts disconnect and reconnect");
    }

    private static void TestReceiveRegressions(string tempDir)
    {
        var store = new SnapshotStore();
        var coordinator = new EventCoordinator(store, new AppLogger(Path.Combine(tempDir, "recovery.log")));
        var path = Path.Combine(tempDir, "tail.jsonl");
        var tail = new FallbackTailReader();
        string JsonLine(string json) => JsonSerializer.Serialize(JsonDocument.Parse(json).RootElement);
        var paused = JsonLine(SnapshotJson(600, "debug_paused", "paused", RipA, Browse));
        File.WriteAllText(path, paused[..(paused.Length / 2)], new UTF8Encoding(false));
        Expect(tail.ReadAvailable(path, coordinator) == 0, "partial fallback record waits for newline");
        File.AppendAllText(path, paused[(paused.Length / 2)..] + "\n", new UTF8Encoding(false));
        Expect(tail.ReadAvailable(path, coordinator) == 1 && store.LastCpuSnapshot?.Eip == RipA &&
            store.CurrentSelectionSnapshot?.SelectedAddress == Browse, "late receiver automatically recovers CPU and browse");
        Expect(tail.ReadAvailable(path, coordinator) == 0, "tail reader does not replay existing records");
        File.AppendAllText(path, "null\n[]\n{\"data\":null}\n{\"source\":42}\nnot-json\n", new UTF8Encoding(false));
        Expect(tail.ReadAvailable(path, coordinator) == 0, "malformed fallback shapes do not crash automatic recovery");
        File.AppendAllText(path, JsonLine(XrefsJson(RipA, "xref_only")) + "\n", new UTF8Encoding(false));
        tail.ReadAvailable(path, coordinator);
        Expect(!store.XrefCopyPending && store.LatestXrefs is null, "automatic recovery never rearms historical XREF");
        coordinator.ProcessRaw(SnapshotJson(602, "step_completed", "paused", RipB, RipB), EventOrigin.Tcp);
        File.AppendAllText(path, JsonLine(SnapshotJson(601, "selection_changed", "paused", RipA, Browse)) + "\n", new UTF8Encoding(false));
        tail.ReadAvailable(path, coordinator);
        Expect(store.LastCpuSnapshot?.Eip == RipB && store.CurrentSelectionSnapshot?.SelectedAddress == RipB,
            "delayed fallback cannot overwrite newer TCP CPU or browse state");

        var selectionOnly = new SnapshotStore();
        selectionOnly.Add(ParseSnapshot(SnapshotJson(700, "selection_changed", "paused", RipA, Browse)));
        Expect(selectionOnly.LastCpuSnapshot?.Eip == RipA, "paused browse event bootstraps missing CPU pause");
        var fixedCpu = selectionOnly.LastCpuSnapshot;
        selectionOnly.Add(ParseSnapshot(SnapshotJson(701, "selection_changed", "paused", RipA, RipB)));
        Expect(ReferenceEquals(fixedCpu, selectionOnly.LastCpuSnapshot), "ordinary browsing preserves fixed CPU context");
        selectionOnly.Add(ParseSnapshot(SnapshotJson(702, "selection_changed", "paused", RipA, Browse)
            .Replace("\"window\":\"disassembly\"", "\"window\":\"stack\"")));
        Expect(selectionOnly.CurrentSelectionSnapshot?.SelectedAddress == RipB, "stack selection does not replace code browse position");

        var reordered = new SnapshotStore();
        reordered.Add(ParseSnapshot(SnapshotJson(902, "selection_changed", "paused", RipA, Browse)));
        reordered.Add(ParseSnapshot(SnapshotJson(901, "breakpoint_hit", "paused", RipA, RipA)));
        Expect(reordered.LastCpuSnapshot?.EventType == "breakpoint_hit" &&
            reordered.CurrentSelectionSnapshot?.SelectedAddress == Browse,
            "late breakpoint is not swallowed by an earlier-arriving browse event");
        reordered.Add(ParseSnapshot(SnapshotJson(905, "debug_resumed", "running", RipA, Browse)));
        reordered.Add(ParseSnapshot(SnapshotJson(904, "step_completed", "paused", RipB, RipB)));
        Expect(reordered.DebugState == "running" && reordered.LastCpuSnapshot?.Eip == RipB &&
            reordered.CurrentSelectionSnapshot?.SelectedAddress == Browse,
            "late CPU pause updates retained pause without rewinding current running state or browse");
        reordered.Add(ParseSnapshot(SnapshotJson(903, "debug_paused", "paused", RipA, RipA)));
        Expect(reordered.LastCpuSnapshot?.Eip == RipB, "older CPU event cannot replace newer CPU event");

        var xrefFirst = new SnapshotStore();
        xrefFirst.Add(ParseEvent(XrefsJson(RipA, "contextual")));
        xrefFirst.Add(ParseSnapshot(SnapshotJson(710, "debug_paused", "paused", RipA, Browse)));
        Expect(xrefFirst.XrefCopyPending && xrefFirst.LatestXrefs is not null, "first CPU event does not erase just-received XREF");
        xrefFirst.Add(ParseSnapshot(SnapshotJson(1, "debug_started", "running", RipA, RipA).Replace("TEST-X64", "NEW-SESSION")));
        Expect(xrefFirst.LastCpuSnapshot is null && xrefFirst.LatestXrefs is null, "new debug session clears previous session context");

        EventJsonParser.TryParse(SnapshotJson(720, "debug_paused", "paused", RipA, Browse)
            .Replace("1788328712202", long.MaxValue.ToString()), EventOrigin.Fallback, out var badTime, out _);
        Expect(SnapshotFormatter.FormatCpu((SnapshotEvent)badTime!, false, false).Contains("时间戳异常"),
            "corrupt fallback timestamp does not crash rendering");

        var node = System.Text.Json.Nodes.JsonNode.Parse(SnapshotJson(800, "breakpoint_hit", "paused", RipA, Browse))!;
        node["disassembly_context"] = System.Text.Json.Nodes.JsonNode.Parse($"[{{\"address\":\"{RipA}\",\"instruction\":\"CPU_SENTINEL\",\"size\":1}}]");
        node["browse_disassembly_context"] = System.Text.Json.Nodes.JsonNode.Parse($"[{{\"address\":\"{Browse}\",\"instruction\":\"BROWSE_SENTINEL\",\"size\":1}}]");
        var separate = ParseSnapshot(node.ToJsonString());
        var cpuText = SnapshotFormatter.FormatCpu(separate, false, true);
        var browseText = SnapshotFormatter.FormatBrowse(separate, separate, true);
        Expect(cpuText.Contains("CPU_SENTINEL") && !cpuText.Contains("BROWSE_SENTINEL") &&
            browseText.Contains("BROWSE_SENTINEL") && !browseText.Contains("CPU_SENTINEL"),
            "real breakpoint CPU and browse assembly use independent address centers");
        node["snapshot_consistent"] = false;
        node["sequence"] = 801;
        selectionOnly.Add(separate);
        var valid = selectionOnly.LastCpuSnapshot;
        selectionOnly.Add(ParseSnapshot(node.ToJsonString()));
        Expect(ReferenceEquals(valid, selectionOnly.LastCpuSnapshot) &&
            SmartCopyComposer.Compose(selectionOnly).Text.Contains("snapshot_consistent=false"),
            "inconsistent observation warns without discarding last valid pause");
    }

    private static void TestUi()
    {
        using var form = new MainForm(18768);
        var controls = Descendants(form).ToArray();
        Expect(form.Text == "x64dbg Snapshot Assistant", "x64 UI title");
        Expect(!controls.OfType<ListBox>().Any(), "UI has no history list");
        Expect(controls.OfType<GroupBox>().Any(item => item.Text.Contains("固定 CPU Snapshot")), "fixed CPU pane exists");
        Expect(controls.OfType<GroupBox>().Any(item => item.Text.Contains("当前浏览位置")), "browse pane exists");
        var buttons = controls.OfType<Button>().ToArray();
        Expect(buttons.SingleOrDefault(item => item.Text == "等待 x64dbg...") is { Enabled: false },
            "one smart-copy button waits for x64dbg data");
        Expect(buttons.Count(item => item.Text is "重新附带到下次复制" or "清空XREF") == 2,
            "XREF rearm and clear controls retained");
        Expect(!buttons.Any(item => item.Text is "复制CPU" or "复制浏览" or "复制CPU+浏览" or "复制完整上下文给ChatGPT" or "刷新显示"),
            "duplicate copy and refresh buttons absent");
        var title = controls.OfType<Label>().Single(item => item.Text == "x64dbg Snapshot Assistant");
        var note = controls.OfType<Label>().Single(item => item.Text.Contains("固定 CPU Snapshot"));
        Expect(note.Top > title.Bottom, "header remains uncluttered");
        var menu = controls.OfType<MenuStrip>().Single();
        Expect(HasMenuPath(menu.Items, "工具", "诊断", "读取回退日志") &&
               HasMenuPath(menu.Items, "工具", "诊断", "打开运行日志"),
            "diagnostic actions remain under Tools menu");
    }

    private static SnapshotEvent ParseSnapshot(string json) => (SnapshotEvent)ParseEvent(json);

    private static ReceivedEvent ParseEvent(string json)
    {
        Expect(EventJsonParser.TryParse(json, EventOrigin.Tcp, out var received, out _), "fixture parsed");
        return received!;
    }

    private static string XrefsJson(string target, string copyMode) => $$"""
    {"event":"xrefs_result","source":"x64dbg_context_v2","query":"manual","copy_mode":"{{copyMode}}","target_address":"{{target}}","count":2,
     "references":[{"address":"{{Browse}}","type":"CALL","instruction":"call {{target}}","module":"sample64","rva":"A538"},
                   {"address":"00007FF61234B7A4","type":"Jcc","instruction":"je {{target}}","module":"sample64","rva":"B7A4"}]}
    """;

    private static string SnapshotJson(long sequence, string eventType, string state, string rip, string selected,
        string instruction = "nop", int size = 1, string callTarget = "", string flags = "0000000000000202")
    {
        var rsp = 0x0000007FFDF00000UL;
        var stack = string.Join(',', Enumerable.Range(0, 16).Select(i =>
            $"{{\"offset\":\"{i * 8:X16}\",\"address\":\"{rsp + (ulong)i * 8:X16}\",\"value\":\"{(ulong)i:X16}\"}}"));
        var center = Convert.ToUInt64(selected, 16);
        var context = string.Join(',', Enumerable.Range(-20, 50).Select(i =>
        {
            var address = unchecked(center + (ulong)i);
            return $"{{\"address\":\"{address:X16}\",\"module\":\"sample64\",\"module_offset\":\"{address & 0xFFFF:X16}\",\"label\":\"\",\"instruction\":\"nop\",\"bytes\":\"90\",\"size\":1,\"call_target\":null}}";
        }));
        var targetJson = callTarget.Length == 0 ? "null" : $"\"{callTarget}\"";
        return $$"""
        {"schema_version":2,"source":"x64dbg_context_v2","architecture":"x64","session_id":"TEST-X64","sequence":{{sequence}},"timestamp_ms":1788328712202,
         "event_type":"{{eventType}}","action":"test","window":"disassembly","debug_state":"{{state}}","registers_valid":true,"snapshot_consistent":true,
         "selected_address":"{{selected}}","previous_selected_address":"0000000000000000","eip":"{{rip}}","rip":"{{rip}}","selection_equals_eip":{{(selected == rip ? "true" : "false")}},
         "selected_instruction":{"address":"{{selected}}","module":"sample64","module_offset":"0000000000005670","label":"selected_label","instruction":"{{instruction}}","bytes":"90","size":{{size}},"call_target":{{targetJson}}},
         "current_instruction":{"address":"{{rip}}","module":"sample64","module_offset":"0000000000005670","label":"cpu_label","instruction":"{{instruction}}","bytes":"90","size":{{size}},"call_target":{{targetJson}}},
         "registers":{"RAX":"0000000000000010","RBX":"0000000000000020","RCX":"0000000000000030","RDX":"0000000000000040","RSI":"0000000000000050","RDI":"0000000000000060","RBP":"0000007FFDF00100","RSP":"0000007FFDF00000","R8":"0000000000000008","R9":"0000000000000009","R10":"0000000000000010","R11":"0000000000000011","R12":"0000000000000012","R13":"0000000000000013","R14":"0000000000000014","R15":"0000000000000015","RIP":"{{rip}}","RFLAGS":"{{flags}}"},
         "stack":[{{stack}}],"disassembly_context":[{{context}}]}
        """;
    }

    private static async Task Send(int port, string text)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var bytes = Encoding.UTF8.GetBytes(text);
        await client.GetStream().WriteAsync(bytes);
        client.Client.Shutdown(SocketShutdown.Send);
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach(Control child in root.Controls)
        {
            yield return child;
            foreach(var nested in Descendants(child))
                yield return nested;
        }
    }

    private static bool HasMenuPath(ToolStripItemCollection items, params string[] path)
    {
        ToolStripItemCollection current = items;
        for(var index = 0; index < path.Length; ++index)
        {
            var match = current.Cast<ToolStripItem>().FirstOrDefault(item => item.Text == path[index]);
            if(match is null)
                return false;
            if(index == path.Length - 1)
                return true;
            if(match is not ToolStripDropDownItem dropdown)
                return false;
            current = dropdown.DropDownItems;
        }
        return false;
    }

    private static void Expect(bool condition, string name)
    {
        if(condition)
            Console.WriteLine($"PASS {name}");
        else
        {
            Console.WriteLine($"FAIL {name}");
            ++_failures;
        }
    }
}
