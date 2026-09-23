using System.Diagnostics;
using System.Net.Sockets;

namespace SnapshotAssistant;

public sealed class MainForm : Form
{
    private static readonly Color WindowBack = Color.FromArgb(30, 34, 39);
    private static readonly Color PanelBack = Color.FromArgb(36, 41, 47);
    private static readonly Color EditorBack = Color.FromArgb(23, 26, 30);
    private static readonly Color Border = Color.FromArgb(67, 76, 87);
    private static readonly Color Fore = Color.FromArgb(225, 231, 237);
    private static readonly Color Accent = Color.FromArgb(50, 156, 224);
    private static readonly Color Warning = Color.FromArgb(255, 190, 70);

    private readonly LiveStateExporter _liveExporter;
    private DateTime _lastExportErrorAt;
    private readonly int _port;
    private readonly AppLogger _logger;
    private readonly SnapshotStore _store;
    private readonly EventCoordinator _coordinator;
    private readonly TcpEventServer _server;
    private readonly Label _connectionStatus = new();
    private readonly Label _cpuStatus = new();
    private readonly Label _sessionStatus = new();
    private readonly Label _timeStatus = new();
    private readonly Label _summary = new();
    private readonly RichTextBox _cpuText = CreateViewer();
    private readonly RichTextBox _browseText = CreateViewer();
    private readonly RichTextBox _xrefText = CreateViewer();
    private readonly RichTextBox _callText = CreateViewer();
    private readonly RichTextBox _compareText = CreateViewer();
    private readonly CheckBox _fullStack = NewCheckBox("展开16项栈");
    private readonly CheckBox _fullAssembly = NewCheckBox("展开完整汇编");
    private readonly CheckBox _collapseMatchingBrowse = NewCheckBox("位置一致时折叠");
    private readonly ComboBox _xrefFilter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    private readonly Label _xrefCopyState = new() { AutoSize = true };
    private readonly Button _rearmXrefButton = new() { Text = "重新附带到下次复制", AutoSize = true };
    private readonly Button _clearXrefButton = new() { Text = "清空XREF", AutoSize = true };
    private readonly Button _smartCopyButton = new();
    private readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 1000 };
    private SplitContainer? _mainSplit;
    private SplitContainer? _leftSplit;
    private DateTime? _lastTcpAt;
    private DateTime _lastConnectionLogAt;
    private DateTime _lastListenAttempt;
    private DateTime _lastListenErrorAt;
    private string _lastListenError = "";
    private readonly FallbackTailReader _fallbackReader = new();
    private bool _readingFallback;
    private bool _recoveredFallback;

    public MainForm(int port = 8768, string? exportDirectory = null)
    {
        _port = port;
        _liveExporter = new LiveStateExporter("x64", port, exportDirectory);
        _logger = new AppLogger(Path.Combine(Path.GetDirectoryName(_liveExporter.OutputPath)!, "receiver.log"));
        _store = new SnapshotStore();
        _coordinator = new EventCoordinator(_store, _logger);
        _server = new TcpEventServer(port);

        Text = "x64dbg Snapshot Assistant";
        Width = 1380;
        Height = 920;
        MinimumSize = new Size(1080, 720);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = WindowBack;
        ForeColor = Fore;
        Font = new Font("Microsoft YaHei UI", 9F);

        BuildInterface();
        WireEvents();
        _statusTimer.Tick += (_, _) => ExportLiveState();
        RenderLive();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        BeginInvoke(ApplyInitialSplitRatios);
        TryStartServer();
        PollFallback();
        ExportLiveState();
        _statusTimer.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _statusTimer.Stop();
        _server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        ExportLiveState(running: false);
        _liveExporter.Dispose();
        base.OnFormClosed(e);
    }

    private void ExportLiveState(bool running = true)
    {
        try
        {
            _liveExporter.Write(_coordinator, running, _server.IsListening);
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // An inaccessible export must not stop receiving or copying data.
            if(DateTime.UtcNow - _lastExportErrorAt > TimeSpan.FromSeconds(30))
            {
                _lastExportErrorAt = DateTime.UtcNow;
                try { _logger.Warning($"现场导出失败：{ex.Message}"); } catch(IOException) { }
                catch(UnauthorizedAccessException) { }
            }
        }
    }

    private void BuildInterface()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = WindowBack,
            Padding = new Padding(10)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var menu = BuildMenu();
        MainMenuStrip = menu;
        root.Controls.Add(menu, 0, 0);
        root.Controls.Add(BuildHeader(), 0, 1);
        root.Controls.Add(BuildStatusArea(), 0, 2);
        root.Controls.Add(BuildWorkspace(), 0, 3);
        Controls.Add(root);
    }

    private MenuStrip BuildMenu()
    {
        var menu = new MenuStrip
        {
            Dock = DockStyle.Fill,
            BackColor = PanelBack,
            ForeColor = Fore,
            GripStyle = ToolStripGripStyle.Hidden,
            Padding = new Padding(4, 1, 0, 1)
        };
        var tools = new ToolStripMenuItem("工具") { ForeColor = Fore };
        var diagnostics = new ToolStripMenuItem("诊断");
        diagnostics.DropDownItems.Add("读取回退日志", null, (_, _) => ImportFallback());
        diagnostics.DropDownItems.Add("打开运行日志", null, (_, _) => OpenLog());
        tools.DropDownItems.Add(diagnostics);
        menu.Items.Add(tools);
        return menu;
    }

    private Control BuildHeader()
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = WindowBack };
        var title = new Label
        {
            Text = "x64dbg Snapshot Assistant",
            AutoSize = true,
            Location = new Point(8, 2),
            Font = new Font("Microsoft YaHei UI", 16F, FontStyle.Bold),
            ForeColor = Color.FromArgb(104, 190, 255)
        };
        var note = new Label
        {
            Text = "固定 CPU Snapshot · 浏览与执行严格分离 · XREF 独立接收",
            AutoSize = true,
            Location = new Point(10, 39),
            Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Bold),
            ForeColor = Warning
        };
        panel.Controls.Add(title);
        panel.Controls.Add(note);
        return panel;
    }

    private Control BuildStatusArea()
    {
        var outer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
            BackColor = PanelBack,
            Padding = new Padding(10, 5, 10, 5),
            CellBorderStyle = TableLayoutPanelCellBorderStyle.Single
        };
        outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 29));
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var status = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = PanelBack };
        ConfigureStatusLabel(_connectionStatus, 330);
        ConfigureStatusLabel(_cpuStatus, 125);
        ConfigureStatusLabel(_sessionStatus, 180);
        ConfigureStatusLabel(_timeStatus, 180);
        status.Controls.AddRange([_connectionStatus, _cpuStatus, _sessionStatus, _timeStatus]);

        _summary.Dock = DockStyle.Fill;
        _summary.TextAlign = ContentAlignment.MiddleLeft;
        _summary.ForeColor = Color.FromArgb(125, 205, 255);
        _summary.BackColor = Color.FromArgb(25, 55, 70);
        _summary.Padding = new Padding(8, 0, 0, 0);
        outer.Controls.Add(status, 0, 0);
        outer.Controls.Add(_summary, 0, 1);
        return outer;
    }

    private Control BuildWorkspace()
    {
        _mainSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 760,
            BackColor = Border,
            Margin = new Padding(0, 10, 0, 4)
        };

        _leftSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 470,
            BackColor = Border
        };
        _leftSplit.Panel1.Controls.Add(BuildCpuGroup());
        _leftSplit.Panel2.Controls.Add(BuildBrowseGroup());
        _mainSplit.Panel1.Controls.Add(_leftSplit);
        _mainSplit.Panel2.Controls.Add(BuildRightTabs());
        return _mainSplit;
    }

    private Control BuildCpuGroup()
    {
        var controls = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = PanelBack };
        controls.Controls.Add(_fullStack);
        controls.Controls.Add(_fullAssembly);
        return BuildTextGroup("固定 CPU Snapshot【只读真实现场】", _cpuText, controls);
    }

    private Control BuildBrowseGroup()
    {
        var controls = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = PanelBack };
        controls.Controls.Add(_collapseMatchingBrowse);
        return BuildTextGroup("当前浏览位置【不改变固定 CPU Snapshot】", _browseText, controls);
    }

    private Control BuildRightTabs()
    {
        var host = new Panel { Dock = DockStyle.Fill, BackColor = PanelBack };
        var tabs = new DarkTabControl
        {
            Dock = DockStyle.Fill,
            ItemSize = new Size(92, 31),
            SizeMode = TabSizeMode.FillToRight,
            BackColor = PanelBack,
            ForeColor = Fore
        };
        tabs.TabPages.Add(BuildXrefTab());
        tabs.TabPages.Add(BuildPlainTab("CALL 现场", _callText));
        tabs.TabPages.Add(BuildPlainTab("Snapshot 变化", _compareText));

        _smartCopyButton.Text = "等待 x64dbg...";
        _smartCopyButton.Enabled = false;
        _smartCopyButton.Height = 28;
        _smartCopyButton.FlatStyle = FlatStyle.Flat;
        _smartCopyButton.BackColor = Color.FromArgb(30, 116, 174);
        _smartCopyButton.ForeColor = Fore;
        _smartCopyButton.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        _smartCopyButton.FlatAppearance.BorderColor = Accent;
        _smartCopyButton.Click += (_, _) => SmartCopy();

        host.Controls.Add(tabs);
        host.Controls.Add(_smartCopyButton);
        void PositionSmartButton()
        {
            const int leftAfterTabs = 92 * 3 + 8;
            var available = Math.Max(150, host.ClientSize.Width - leftAfterTabs - 6);
            _smartCopyButton.SetBounds(leftAfterTabs, 2, Math.Min(220, available), 28);
            _smartCopyButton.BringToFront();
        }
        host.Resize += (_, _) => PositionSmartButton();
        host.HandleCreated += (_, _) => PositionSmartButton();
        return host;
    }

    private TabPage BuildXrefTab()
    {
        var tab = new TabPage("XREF / 引用") { BackColor = PanelBack, ForeColor = Fore, Padding = new Padding(8) };
        var controls = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, WrapContents = false, BackColor = PanelBack };
        controls.Controls.Add(new Label { Text = "类型筛选：", AutoSize = true, ForeColor = Fore, Margin = new Padding(2, 8, 2, 0) });
        _xrefFilter.Items.AddRange(["全部", "CALL", "JMP", "Jcc", "DATA", "UNKNOWN"]);
        _xrefFilter.SelectedIndex = 0;
        controls.Controls.Add(_xrefFilter);
        ConfigureSmallButton(_rearmXrefButton);
        ConfigureSmallButton(_clearXrefButton);
        controls.Controls.Add(_rearmXrefButton);
        controls.Controls.Add(_clearXrefButton);
        _xrefCopyState.ForeColor = Warning;
        _xrefCopyState.Margin = new Padding(8, 8, 2, 0);
        controls.Controls.Add(_xrefCopyState);
        tab.Controls.Add(_xrefText);
        tab.Controls.Add(controls);
        controls.BringToFront();
        return tab;
    }

    private static TabPage BuildPlainTab(string title, RichTextBox viewer)
    {
        var tab = new TabPage(title) { BackColor = PanelBack, ForeColor = Fore, Padding = new Padding(8) };
        tab.Controls.Add(viewer);
        return tab;
    }

    private static Control BuildTextGroup(string title, RichTextBox viewer, FlowLayoutPanel controls)
    {
        var group = new GroupBox
        {
            Text = title,
            Dock = DockStyle.Fill,
            BackColor = PanelBack,
            ForeColor = Color.FromArgb(105, 195, 255),
            Padding = new Padding(10)
        };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = PanelBack };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 31));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(controls, 0, 0);
        layout.Controls.Add(viewer, 0, 1);
        group.Controls.Add(layout);
        return group;
    }

    private void WireEvents()
    {
        _coordinator.EventAccepted += received => Ui(() => OnEventAccepted(received));
        _server.RawMessageReceived += raw =>
        {
            _lastTcpAt = DateTime.Now;
            _coordinator.ProcessRaw(raw, EventOrigin.Tcp);
        };
        _server.ClientConnected += () =>
        {
            _lastTcpAt = DateTime.Now;
            if(DateTime.Now - _lastConnectionLogAt > TimeSpan.FromSeconds(10))
            {
                _lastConnectionLogAt = DateTime.Now;
                _logger.Info($"插件连接成功：127.0.0.1:{_port}");
            }
        };
        _server.ServerError += message => _logger.Warning($"TCP接收异常：{message}");
        _statusTimer.Tick += (_, _) => { UpdateStatus(); PollFallback(); };
        _fullStack.CheckedChanged += (_, _) => RenderLive();
        _fullAssembly.CheckedChanged += (_, _) => RenderLive();
        _collapseMatchingBrowse.CheckedChanged += (_, _) => RenderLive();
        _xrefFilter.SelectedIndexChanged += (_, _) => RenderXrefs();
        _rearmXrefButton.Click += (_, _) => RearmXrefsForCopy();
        _clearXrefButton.Click += (_, _) => ClearXrefs();
    }

    private void TryStartServer()
    {
        if(_server.IsListening || DateTime.Now - _lastListenAttempt < TimeSpan.FromSeconds(3))
            return;
        _lastListenAttempt = DateTime.Now;
        try
        {
            _server.Start();
            _logger.Info($"开始监听 127.0.0.1:{_server.BoundPort}");
            _lastListenError = "";
        }
        catch(SocketException ex)
        {
            var message = ex.SocketErrorCode == SocketError.AddressAlreadyInUse
                ? $"端口 {_port} 已被占用，正在自动重试"
                : $"监听失败：{ex.Message}";
            if(message != _lastListenError || DateTime.Now - _lastListenErrorAt > TimeSpan.FromSeconds(30))
            {
                _lastListenError = message;
                _lastListenErrorAt = DateTime.Now;
                _logger.Warning(message);
            }
        }
    }

    private void OnEventAccepted(ReceivedEvent received)
    {
        RenderLive();
        UpdateStatus();
    }

    private async void PollFallback()
    {
        if(_readingFallback || IsDisposed) return;
        _readingFallback = true;
        try
        {
            var count = await Task.Run(() => _fallbackReader.ReadAvailable(FallbackImporter.DefaultPath, _coordinator));
            if(count > 0 && !IsDisposed)
            {
                _recoveredFallback = true;
                _logger.Info($"自动补收暂停/浏览日志：{count}条");
                RenderLive();
            }
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warning($"等待回退日志：{ex.Message}");
        }
        finally { _readingFallback = false; }
    }

    private void RenderLive()
    {
        var cpu = _store.LatestCpuObservation ?? _store.LastCpuSnapshot;
        var selection = _store.CurrentSelectionSnapshot;
        _cpuText.Text = SnapshotFormatter.FormatCpu(cpu, _fullStack.Checked, _fullAssembly.Checked, selection);
        var same = cpu is not null && selection is not null && cpu.Eip == selection.SelectedAddress;
        _browseText.Visible = !(_collapseMatchingBrowse.Checked && same);
        _browseText.Text = SnapshotFormatter.FormatBrowse(selection, cpu, _fullAssembly.Checked);
        RenderXrefs();
        _callText.Text = SnapshotFormatter.FormatCall(cpu);
        _compareText.Text = SnapshotFormatter.FormatComparison(cpu, cpu is null ? null : _store.PreviousCpuSnapshot(cpu));
        UpdateSmartCopy();
        UpdateAutomaticSummary();
    }

    private void RenderXrefs()
    {
        var filter = _xrefFilter.SelectedItem?.ToString() ?? "全部";
        _xrefText.Text = SnapshotFormatter.FormatXrefs(_store.LatestXrefs, filter, _store.LatestXrefContext);
        var hasUsableXrefs = _store.LatestXrefs is { Error.Length: 0 } xrefs &&
                             HexUtil.HasAddress(xrefs.TargetAddress);
        _rearmXrefButton.Enabled = hasUsableXrefs && !_store.XrefCopyPending;
        _clearXrefButton.Enabled = _store.LatestXrefs is not null;
        _xrefCopyState.Text = _store.LatestXrefs is null
            ? ""
            : _store.XrefCopyPending
                ? "下次复制：将附带"
                : "下次复制：不附带（列表保留）";
    }

    private void UpdateStatus()
    {
        if(!_server.IsListening)
            TryStartServer();
        // The plugin opens one short-lived connection per event. Silence while
        // paused is not evidence of disconnection; report listener readiness.
        if(_server.IsListening && _lastTcpAt.HasValue)
        {
            _connectionStatus.Text = $"● 监听正常，已接收 x64dbg 数据  {_port}";
            _connectionStatus.ForeColor = Color.LightGreen;
        }
        else if(_server.IsListening)
        {
            _connectionStatus.Text = _recoveredFallback
                ? $"● 已补收日志，等待实时连接  {_port}"
                : $"● x64dbg 未连接  监听 {_port}";
            _connectionStatus.ForeColor = Warning;
        }
        else
        {
            _connectionStatus.Text = $"● 端口 {_port} 暂不可用，正在重试";
            _connectionStatus.ForeColor = Color.OrangeRed;
        }
        UpdateAutomaticSummary();
    }

    private void UpdateAutomaticSummary()
    {
        var cpu = _store.LatestCpuObservation ?? _store.LastCpuSnapshot;
        var browse = _store.CurrentSelectionSnapshot;
        var cpuAddress = cpu?.Eip ?? "-";
        var browseAddress = browse is not null && HexUtil.HasAddress(browse.SelectedAddress) ? browse.SelectedAddress : "-";
        var relation = cpu is null || browseAddress == "-"
            ? "—"
            : cpu.Eip.Equals(browseAddress, StringComparison.OrdinalIgnoreCase)
                ? "✓ CPU与浏览一致"
                : "⚠ CPU与浏览不同";
        var displayedXrefs = _store.LatestXrefs;
        var xrefSummary = displayedXrefs is null
            ? "无"
            : $"目标 {displayedXrefs.TargetAddress} / {displayedXrefs.Count}条引用 / " +
              (_store.XrefCopyPending ? "等待下次复制" : "仅界面保留");

        _cpuStatus.Text = $"CPU：{StateDisplay(_store.DebugState)}";
        _sessionStatus.Text = $"真实RIP：{cpuAddress}";
        _timeStatus.Text = $"浏览：{browseAddress}";
        _summary.Text = $"关系：{relation}    XREF：{xrefSummary}";
    }

    private void UpdateSmartCopy()
    {
        var decision = SmartCopyComposer.Compose(_store);
        _smartCopyButton.Text = decision.ButtonText;
        _smartCopyButton.Enabled = decision.Enabled;
        _smartCopyButton.BackColor = decision.ButtonText.StartsWith("⚠", StringComparison.Ordinal)
            ? Color.FromArgb(153, 92, 22)
            : Color.FromArgb(30, 116, 174);
    }

    private void SmartCopy()
    {
        var decision = SmartCopyComposer.Compose(_store);
        if(decision.Enabled && decision.Text.Length > 0 && CopyText(decision.Text))
        {
            if(decision.IncludedXrefs is not null)
                _store.ConsumeXrefsAfterSuccessfulCopy(decision.IncludedXrefs);
            RenderLive();
        }
    }

    private void RearmXrefsForCopy()
    {
        if(_store.RearmXrefsForCopy())
            RenderLive();
    }

    private void ClearXrefs()
    {
        _store.ClearXrefs();
        RenderLive();
    }

    private void ImportFallback()
    {
        var path = FallbackImporter.DefaultPath;
        if(!File.Exists(path))
        {
            MessageBox.Show(this, $"回退日志尚不存在：\r\n{path}", "读取回退日志", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            var result = FallbackImporter.Import(path, _coordinator);
            _logger.Info($"读取回退日志：新增{result.Added}，重复{result.Duplicate}，无效{result.Invalid}");
            MessageBox.Show(this,
                $"读取完成。\r\n新增：{result.Added}\r\n跳过重复：{result.Duplicate}\r\n无效：{result.Invalid}",
                "读取回退日志", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch(Exception ex)
        {
            _logger.Error($"读取回退日志失败：{ex.Message}");
            MessageBox.Show(this, ex.Message, "读取失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenLog()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_logger.LogPath) { UseShellExecute = true });
        }
        catch(Exception ex)
        {
            MessageBox.Show(this, ex.Message, "打开日志失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private bool CopyText(string text)
    {
        try
        {
            PlainTextClipboard.SetText(text);
            return true;
        }
        catch(Exception ex)
        {
            _logger.Warning($"复制失败：{ex.Message}");
            MessageBox.Show(this, ex.Message, "复制失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    private static void ConfigureSmallButton(Button button)
    {
        button.Height = 25;
        button.Margin = new Padding(6, 3, 0, 0);
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = Color.FromArgb(48, 57, 67);
        button.ForeColor = Fore;
        button.FlatAppearance.BorderColor = Border;
    }

    private void Ui(Action action)
    {
        if(IsDisposed)
            return;
        if(InvokeRequired)
            BeginInvoke(action);
        else
            action();
    }

    private void ApplyInitialSplitRatios()
    {
        if(_mainSplit is { ClientSize.Width: > 0 })
            _mainSplit.SplitterDistance = Math.Clamp((int)(_mainSplit.ClientSize.Width * 0.58), 420, _mainSplit.ClientSize.Width - 300);
        if(_leftSplit is { ClientSize.Height: > 0 })
            _leftSplit.SplitterDistance = Math.Clamp((int)(_leftSplit.ClientSize.Height * 0.64), 260, _leftSplit.ClientSize.Height - 150);
    }

    private static RichTextBox CreateViewer() => new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = EditorBack,
        ForeColor = Fore,
        Font = new Font("Consolas", 10F),
        WordWrap = false,
        ScrollBars = RichTextBoxScrollBars.ForcedBoth,
        DetectUrls = false
    };

    private static CheckBox NewCheckBox(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = Fore,
        BackColor = PanelBack,
        Margin = new Padding(5, 7, 10, 0)
    };

    private static void ConfigureStatusLabel(Label label, int width)
    {
        label.Width = width;
        label.Height = 25;
        label.TextAlign = ContentAlignment.MiddleLeft;
        label.ForeColor = Fore;
        label.BackColor = PanelBack;
    }

    private static string StateDisplay(string state) => state.ToLowerInvariant() switch
    {
        "paused" => "Paused",
        "running" => "Running",
        _ => "Unknown"
    };
}
