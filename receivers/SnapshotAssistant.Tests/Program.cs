using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SnapshotAssistant;

internal static class Program
{
    private static int _failures;

    [STAThread]
    private static async Task<int> Main()
    {
        ApplicationConfiguration.Initialize();
        var tempDir = Path.Combine(Path.GetTempPath(), "SnapshotAssistantTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            await LiveExportTests.Run(tempDir, "x32");
            TestParserAndState(tempDir);
            TestFormatting();
            TestPlainTextClipboard();
            TestXrefs();
            TestSmartCopy();
            TestFallbackDedup(tempDir);
            await TestTcpReconnect();
            using(var form = new MainForm(18765))
            {
                var controls = Descendants(form).ToArray();
                Expect(form.Text.Contains("Snapshot Assistant"), "UI form constructs");
                Expect(!controls.OfType<ListBox>().Any(), "UI has no Snapshot history list");
                Expect(controls.OfType<GroupBox>().Any(item => item.Text.Contains("固定 CPU Snapshot")), "fixed CPU pane exists");
                Expect(controls.OfType<GroupBox>().Any(item => item.Text.Contains("当前浏览位置")), "browse pane exists");
                Expect(controls.OfType<TabControl>().Any(), "right-side analysis tabs exist");
                var buttons = controls.OfType<Button>().ToArray();
                var smartButton = buttons.SingleOrDefault(item => item.Text == "等待 x32dbg...");
                Expect(smartButton is not null && !smartButton.Enabled,
                    "UI exposes one disabled smart-copy button before data");
                Expect(buttons.Count(item => item.Text is "重新附带到下次复制" or "清空XREF") == 2,
                    "XREF pane exposes rearm and clear controls");
                var title = controls.OfType<Label>().Single(item => item.Text == "x32dbg Snapshot Assistant");
                var note = controls.OfType<Label>().Single(item => item.Text.Contains("固定 CPU Snapshot"));
                Expect(note.Top > title.Bottom, "header explanation is placed below the title");
                Expect(!buttons.Any(item => item.Text is "复制CPU" or "复制浏览" or "复制CPU+浏览" or "复制完整上下文给ChatGPT" or "刷新显示"),
                    "legacy copy and refresh buttons removed");
                var menu = controls.OfType<MenuStrip>().SingleOrDefault();
                Expect(menu is not null && HasMenuPath(menu.Items, "工具", "诊断", "读取回退日志") &&
                       HasMenuPath(menu.Items, "工具", "诊断", "打开运行日志"),
                    "fallback and runtime logs moved under Tools Diagnostics");
            }
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }

        Console.WriteLine(_failures == 0 ? "ALL SNAPSHOT ASSISTANT TESTS PASSED" : $"FAILED: {_failures}");
        return _failures == 0 ? 0 : 1;
    }

    private static void TestParserAndState(string tempDir)
    {
        var logger = new AppLogger(Path.Combine(tempDir, "state.log"));
        var store = new SnapshotStore();
        var coordinator = new EventCoordinator(store, logger);

        Expect(coordinator.ProcessRaw(PauseJson(100, "00530170", "00530170", "00000000"), EventOrigin.Tcp) == ProcessResult.Added,
            "pause JSON accepted");
        var cpu = store.LastCpuSnapshot;
        Expect(cpu is not null && cpu.Eip == "00530170", "true EIP parsed");
        Expect(cpu?.Window == "disassembly", "selection window parsed");
        Expect(cpu?.Stack.Count == 16, "all 16 DWORD stack slots retained");
        Expect(cpu?.CurrentInstruction?.Bytes == "55", "instruction bytes parsed");

        var selectionJson = SnapshotJson(
            sequence: 101,
            eventType: "selection_changed",
            state: "paused",
            eip: "00530170",
            selected: "005317A4",
            eax: "DEADBEEF",
            instruction: "call 00530170",
            instructionSize: 5);
        coordinator.ProcessRaw(selectionJson, EventOrigin.Tcp);
        Expect(ReferenceEquals(cpu, store.LastCpuSnapshot), "selection never overwrites CPU snapshot");
        Expect(store.CurrentSelectionSnapshot?.SelectedAddress == "005317A4", "selection updates browse snapshot");

        coordinator.ProcessRaw(SnapshotJson(102, "debug_resumed", "running", "00530170", "005317A4", "11111111"), EventOrigin.Tcp);
        Expect(ReferenceEquals(cpu, store.LastCpuSnapshot), "resume retains paused CPU snapshot");
        Expect(store.DebugState == "running", "resume updates CPU state");

        var wrapped = $"{{\"source\":\"live_context_fallback\",\"port\":8765,\"data\":{PauseJson(103, "00530171", "00530171", "00000001")}}}";
        Expect(EventJsonParser.TryParse(wrapped, EventOrigin.Fallback, out var parsed, out _) && parsed is SnapshotEvent,
            "fallback data envelope parsed");
        Expect(coordinator.ProcessRaw("{bad json", EventOrigin.Tcp) == ProcessResult.Invalid, "invalid JSON is skipped");
        Expect(coordinator.ProcessRaw(PauseJson(104, "00530172", "00530172", "00000002"), EventOrigin.Tcp) == ProcessResult.Added,
            "valid event continues after invalid JSON");
    }

    private static void TestFormatting()
    {
        EventJsonParser.TryParse(PauseJson(200, "00531872", "00531872", "00000845", "call 005312D0", 5), EventOrigin.Tcp, out var received, out _);
        var snapshot = (SnapshotEvent)received!;
        var compact = SnapshotFormatter.FormatCpu(snapshot, false, false);
        var full = SnapshotFormatter.FormatCpu(snapshot, true, true);
        Expect(compact.Contains("真实EIP：00531872"), "CPU copy contains true EIP");
        Expect(compact.Contains("浏览地址：00531872") && compact.Contains("CPU/浏览：一致"),
            "CPU copy top contains browse relationship");
        Expect(compact.Contains("现场有效：是") && !compact.Contains("registers_valid=false"),
            "normal CPU copy has one concise validity result");
        Expect(compact.Contains("ESP+0C") && !compact.Contains("ESP+10"), "default CPU copy has four stack slots");
        Expect(full.Contains("ESP+3C"), "full copy has sixteen stack slots");
        Expect(compact.Contains("CF=1") && compact.Contains("ZF=1") && compact.Contains("OF=1"), "EFLAGS default bits decoded");
        Expect(SnapshotFormatter.TryGetCall(snapshot.CurrentInstruction, out var call) &&
               call.Target == "005312D0" && call.ReturnAddress == "00531877", "direct CALL details and return address");

        var selectionJson = SnapshotJson(201, "selection_changed", "paused", "00531872", "005317A4", "00000000", "call 00530170", 5);
        EventJsonParser.TryParse(selectionJson, EventOrigin.Tcp, out var selectionEvent, out _);
        var browse = SnapshotFormatter.FormatBrowse((SnapshotEvent)selectionEvent!, snapshot);
        var cpuWithBrowse = SnapshotFormatter.FormatCpu(snapshot, false, false, (SnapshotEvent)selectionEvent!);
        Expect(cpuWithBrowse.Contains("浏览地址：005317A4") && cpuWithBrowse.Contains("CPU/浏览：不一致"),
            "CPU copy top uses current browse relationship");
        Expect(browse.Contains("===== 当前浏览位置【静态】 =====") && browse.Contains("CPU没有执行到浏览地址") &&
               browse.Contains("CPU真实停在00531872"), "browse copy warns when selection differs");

        EventJsonParser.TryParse(PauseJson(202, "00531877", "00531877", "00000805"), EventOrigin.Tcp, out var nextEvent, out _);
        var difference = SnapshotFormatter.FormatComparison((SnapshotEvent)nextEvent!, snapshot);
        Expect(difference.Contains("EIP：") && difference.Contains("ZF：1 → 0"), "CPU comparison reports changed registers and flags");
    }

    private static void TestPlainTextClipboard()
    {
        const string expected = "=====\r\ndebug_paused\r\n<sub_530170>\r\n普通 空格";
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
                var data = Clipboard.GetDataObject();
                actual = Clipboard.GetText(TextDataFormat.UnicodeText);
                formats = data?.GetFormats(false) ?? [];
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
        Expect(failure is null, "clipboard write succeeds on STA thread");
        Expect(actual == expected, "clipboard preserves raw markers, angle brackets and ordinary spaces");
        Expect(formats.SequenceEqual([DataFormats.UnicodeText]),
            "clipboard publishes Unicode plain text only");
        Expect(!actual.Contains("\\=====") && !actual.Contains("\\<sub_530170>") && !actual.Contains("&#x20;"),
            "clipboard contains no HTML or Markdown escaping");
    }

    private static void TestXrefs()
    {
        const string json = """
        {"event":"xrefs_result","source":"x32dbg_context_v2","query":"manual","target_address":"00530170","count":2,"references":[
          {"address":"0052A538","type":"CALL","instruction":"call 00530170","module":"plantsvszombies","rva":"12A538"},
          {"reference_address":"005317A4","reference_type":"Jcc","instruction":"je 00530170","module":"plantsvszombies","rva":"1317A4"}]}
        """;
        Expect(EventJsonParser.TryParse(json, EventOrigin.Tcp, out var parsed, out _), "XREF JSON accepted");
        var xrefs = (XrefsResultEvent)parsed!;
        Expect(xrefs.Count == 2 && xrefs.References[1].Address == "005317A4", "XREF compatibility fields parsed");
        Expect(xrefs.CopyMode == "contextual", "legacy XREF defaults to contextual copy mode");
        var filtered = SnapshotFormatter.FormatXrefs(xrefs, "CALL");
        Expect(filtered.Contains("0052A538") && !filtered.Contains("005317A4"), "XREF type filter");

        const string contextJson = """
        {"event":"xref_context_result","target_address":"00530170","reference_address":"0052A538","before":10,"after":5,
         "context":[{"address":"0052A538","current":true,"instruction":"call 00530170","module":"plantsvszombies","rva":"12A538"}]}
        """;
        Expect(EventJsonParser.TryParse(contextJson, EventOrigin.Tcp, out var context, out _) &&
               ((XrefContextEvent)context!).Context[0].Current, "XREF context parsed");
    }

    private static void TestSmartCopy()
    {
        var empty = new SnapshotStore();
        var decision = SmartCopyComposer.Compose(empty);
        Expect(!decision.Enabled && decision.ButtonText == "等待 x32dbg...", "smart copy waits before valid data");

        var xrefOnly = new SnapshotStore();
        xrefOnly.Add(ParseEvent(XrefsJson("00533240", 3)));
        decision = SmartCopyComposer.Compose(xrefOnly);
        Expect(decision.Enabled && decision.ButtonText == "复制 XREF" && decision.IncludesXref &&
               decision.Text.StartsWith("===== XREF / 引用 =====", StringComparison.Ordinal),
            "XREF received before a CPU event remains directly copyable");

        var same = new SnapshotStore();
        same.Add(Parse(PauseJson(500, "00530170", "00530170", "00000202")));
        decision = SmartCopyComposer.Compose(same);
        Expect(decision.Enabled && decision.ButtonText == "复制 CPU 现场" &&
               decision.Text.Contains("===== CPU真实现场 =====") && !decision.Text.Contains("===== 当前浏览位置【静态】 ====="),
            "matching CPU and browse copies CPU once");

        var xrefOnlyWithCpu = new SnapshotStore();
        xrefOnlyWithCpu.Add(Parse(PauseJson(490, "00533240", "005317A4", "00000202")));
        xrefOnlyWithCpu.Add(ParseEvent(XrefsJson("00533240", 3, "xref_only")));
        decision = SmartCopyComposer.Compose(xrefOnlyWithCpu);
        Expect(decision.ButtonText == "复制 XREF" && decision.IncludesXref &&
               decision.Text.StartsWith("===== XREF / 引用 =====", StringComparison.Ordinal) &&
               !decision.Text.Contains("===== CPU真实现场 =====") &&
               !decision.Text.Contains("===== 当前浏览位置【静态】 ====="),
            "CPU-disassembly XREF mode excludes CPU and browse assembly from smart copy");

        same.Add(ParseEvent(XrefsJson("00530170", 2)));
        decision = SmartCopyComposer.Compose(same);
        Expect(same.XrefCopyPending && decision.ButtonText == "复制 CPU 现场 + XREF" &&
               decision.IncludesXref && decision.Text.Contains("===== XREF / 引用 ====="),
            "new XREF result is pending for the next smart copy");
        var firstXrefs = decision.IncludedXrefs!;
        var secondComposition = SmartCopyComposer.Compose(same);
        Expect(secondComposition.IncludesXref && same.XrefCopyPending,
            "composing clipboard text does not consume pending XREF before write succeeds");
        Expect(same.ConsumeXrefsAfterSuccessfulCopy(firstXrefs) && !same.XrefCopyPending,
            "successful clipboard write consumes pending XREF once");
        decision = SmartCopyComposer.Compose(same);
        Expect(!decision.IncludesXref && decision.ButtonText == "复制 CPU 现场" &&
               same.LatestXrefs == firstXrefs && SnapshotFormatter.FormatXrefs(same.LatestXrefs, "全部").Contains("00530170"),
            "consumed XREF remains visible in UI state but is excluded from later copies");

        same.Add(Parse(SnapshotJson(501, "selection_changed", "paused", "00530170", "005317A4", "00000000")));
        decision = SmartCopyComposer.Compose(same);
        Expect(decision.ButtonText == "复制 CPU + 浏览" && !decision.IncludesXref && !same.XrefCopyPending &&
               decision.Text.Contains("===== CPU真实现场 =====") && decision.Text.Contains("===== 当前浏览位置【静态】 ====="),
            "selection change neither includes nor rearms consumed XREF");

        same.Add(ParseEvent(XrefsJson("005317A4", 1)));
        decision = SmartCopyComposer.Compose(same);
        Expect(decision.ButtonText == "复制 CPU + 浏览 + XREF" && decision.IncludesXref && same.XrefCopyPending,
            "a new XREF query activates one-shot copy again");
        var browseXrefs = decision.IncludedXrefs!;
        same.ConsumeXrefsAfterSuccessfulCopy(browseXrefs);

        same.Add(Parse(SnapshotJson(502, "selection_changed", "paused", "00530170", "00600000", "00000000")));
        same.Add(Parse(SnapshotJson(503, "step_completed", "paused", "00530171", "00530171", "00000001")));
        same.Add(Parse(SnapshotJson(504, "breakpoint_hit", "paused", "00530172", "00530172", "00000002")));
        same.Add(Parse(SnapshotJson(505, "debug_resumed", "running", "00530172", "00530172", "00000002")));
        decision = SmartCopyComposer.Compose(same);
        Expect(!same.XrefCopyPending && !decision.IncludesXref,
            "browse, step, breakpoint and resume events never rearm consumed XREF");

        Expect(decision.ButtonText.StartsWith("复制最后暂停现场", StringComparison.Ordinal) &&
               decision.Text.Contains("最后一次暂停现场"),
            "running state retains and labels last paused context");

        Expect(same.RearmXrefsForCopy() && same.XrefCopyPending, "manual rearm schedules retained XREF for the next copy");
        decision = SmartCopyComposer.Compose(same);
        Expect(decision.IncludesXref && decision.IncludedXrefs == browseXrefs,
            "manual rearm includes the retained XREF without a new query");

        same.Add(ParseEvent(XrefsJson("00533240", 4)));
        var newestXrefs = same.LatestXrefs!;
        Expect(same.XrefCopyPending && newestXrefs.TargetAddress == "00533240",
            "later XREF query replaces UI result and activates pending state");
        Expect(!same.ConsumeXrefsAfterSuccessfulCopy(browseXrefs) && same.XrefCopyPending,
            "completion of an older copy cannot consume a newer XREF result");
        same.ClearXrefs();
        decision = SmartCopyComposer.Compose(same);
        Expect(same.LatestXrefs is null && !same.XrefCopyPending && !decision.IncludesXref,
            "clear XREF removes UI result and pending copy state together");

        var browseOnly = new SnapshotStore();
        browseOnly.Add(Parse(SnapshotJson(510, "selection_changed", "unknown", "00000000", "005317A4", "00000000")));
        decision = SmartCopyComposer.Compose(browseOnly);
        Expect(decision.ButtonText == "复制浏览位置" && !decision.Text.Contains("===== CPU真实现场 ====="),
            "browse-only state copies browse position");

        var invalid = new SnapshotStore();
        var invalidJson = PauseJson(520, "00530170", "00530170", "00000202")
            .Replace("\"registers_valid\":true", "\"registers_valid\":false", StringComparison.Ordinal)
            .Replace("\"snapshot_consistent\":true", "\"snapshot_consistent\":false", StringComparison.Ordinal);
        invalid.Add(Parse(invalidJson));
        decision = SmartCopyComposer.Compose(invalid);
        Expect(decision.ButtonText == "⚠ 复制 CPU 现场" && decision.Text.StartsWith("===== CPU真实现场 =====") &&
               decision.Text.Contains("registers_valid=false") && decision.Text.Contains("snapshot_consistent=false"),
            "invalid or inconsistent snapshot is visibly warned");

        var callTarget = new SnapshotStore();
        callTarget.Add(Parse(PauseJson(530, "00531872", "00531872", "00000202", "call 005312D0", 5)));
        callTarget.Add(ParseEvent(XrefsJson("005312D0", 3)));
        callTarget.Add(Parse(SnapshotJson(531, "selection_changed", "paused", "00531872", "00600000", "00000000")));
        decision = SmartCopyComposer.Compose(callTarget);
        Expect(decision.IncludesXref && callTarget.XrefCopyPending,
            "pending XREF matching current CALL target survives browse changes before its first copy");
    }

    private static SnapshotEvent Parse(string json)
    {
        Expect(EventJsonParser.TryParse(json, EventOrigin.Tcp, out var received, out _), "smart-copy fixture parsed");
        return (SnapshotEvent)received!;
    }

    private static ReceivedEvent ParseEvent(string json)
    {
        Expect(EventJsonParser.TryParse(json, EventOrigin.Tcp, out var received, out _), "smart-copy event fixture parsed");
        return received!;
    }

    private static string XrefsJson(string target, int count, string copyMode = "contextual") => $$"""
        {"event":"xrefs_result","source":"x32dbg_context_v2","query":"manual","copy_mode":"{{copyMode}}","target_address":"{{target}}","count":{{count}},"references":[]}
        """;

    private static void TestFallbackDedup(string tempDir)
    {
        var path = Path.Combine(tempDir, "fallback.jsonl");
        using var document = JsonDocument.Parse(PauseJson(300, "00530170", "00530170", "0"));
        var compact = JsonSerializer.Serialize(document.RootElement);
        var line = $"{{\"source\":\"live_context_fallback\",\"port\":8765,\"data\":{compact}}}";
        File.WriteAllLines(path, [line, line]);
        var coordinator = new EventCoordinator(new SnapshotStore(), new AppLogger(Path.Combine(tempDir, "fallback.log")));
        var first = FallbackImporter.Import(path, coordinator);
        var second = FallbackImporter.Import(path, coordinator);
        Expect(first.Added == 1 && first.Duplicate == 1, "fallback duplicate lines skipped");
        Expect(second.Added == 0 && second.Duplicate == 2, "same fallback is not imported twice");
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
        await Send(server.BoundPort, PauseJson(400, "00530170", "00530170", "0"));
        await Send(server.BoundPort, PauseJson(401, "00530171", "00530171", "0"));
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Expect(count == 2 && server.IsListening, "TCP accepts disconnect and reconnect");
    }

    private static async Task Send(int port, string text)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var bytes = Encoding.UTF8.GetBytes(text);
        await client.GetStream().WriteAsync(bytes);
        client.Client.Shutdown(SocketShutdown.Send);
    }

    private static string PauseJson(long sequence, string eip, string selected, string eflags, string instruction = "push ebp", int size = 1) =>
        SnapshotJson(sequence, "debug_paused", "paused", eip, selected, "00000010", instruction, size, eflags);

    private static string SnapshotJson(
        long sequence,
        string eventType,
        string state,
        string eip,
        string selected,
        string eax,
        string instruction = "push ebp",
        int instructionSize = 1,
        string eflags = "00000202")
    {
        var stack = string.Join(',', Enumerable.Range(0, 16).Select(i =>
            $"{{\"offset\":\"{i * 4:X8}\",\"address\":\"{0x0012FF00 + i * 4:X8}\",\"value\":\"{i:X8}\"}}"));
        var context = string.Join(',', Enumerable.Range(-20, 50).Select(i =>
        {
            var address = unchecked(Convert.ToUInt32(selected, 16) + (uint)i);
            return $"{{\"address\":\"{address:X8}\",\"module\":\"plantsvszombies\",\"module_offset\":\"{address - 0x00400000:X8}\",\"label\":\"\",\"instruction\":\"nop\",\"bytes\":\"90\",\"size\":1}}";
        }));
        return $$"""
        {"schema_version":2,"source":"x32dbg_context_v2","session_id":"TEST-SESSION","sequence":{{sequence}},"timestamp_ms":1788328712202,
         "event_type":"{{eventType}}","action":"test","window":"disassembly","debug_state":"{{state}}","registers_valid":true,"snapshot_consistent":true,
         "selected_address":"{{selected}}","previous_selected_address":"00000000","eip":"{{eip}}","selection_equals_eip":{{(selected == eip ? "true" : "false")}},
         "selected_instruction":{"address":"{{selected}}","module":"plantsvszombies","module_offset":"001317A4","label":"selected_label","instruction":"{{instruction}}","bytes":"E8 00 00 00 00","size":{{instructionSize}}},
         "current_instruction":{"address":"{{eip}}","module":"plantsvszombies","module_offset":"00130170","label":"cpu_label","instruction":"{{instruction}}","bytes":"55","size":{{instructionSize}}},
         "registers":{"EAX":"{{eax}}","EBX":"00000020","ECX":"00000030","EDX":"00000040","ESI":"00000050","EDI":"00000060","EBP":"0012FFF0","ESP":"0012FF00","EIP":"{{eip}}","EFLAGS":"{{eflags}}"},
         "stack":[{{stack}}],"disassembly_context":[{{context}}]}
        """;
    }

    private static void Expect(bool condition, string name)
    {
        if(condition)
            Console.WriteLine($"PASS {name}");
        else
        {
            Console.WriteLine($"FAIL {name}");
            _failures++;
        }
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
        for(var index = 0; index < path.Length; index++)
        {
            var match = current.Cast<ToolStripItem>().FirstOrDefault(item => item.Text == path[index]);
            if(match is null)
                return false;
            if(index == path.Length - 1)
                return true;
            if(match is not ToolStripDropDownItem dropDown)
                return false;
            current = dropDown.DropDownItems;
        }
        return false;
    }
}
