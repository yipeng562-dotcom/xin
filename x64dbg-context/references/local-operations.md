# 本机操作参考

新版直接读取功能及新版程序路径优先见 [live-export.md](live-export.md)。以下保留原版工具和原项目的操作信息。

2026-09-23根据本机文件核对；运行时重新检查路径和进程。

## 路径

- 项目：`E:\64DBG`
- 接收器：`E:\64DBG\SnapshotAssistant64\release\SnapshotAssistant64.exe`
- 快捷入口：`E:\64DBG\启动64位接收工具.cmd`
- 构建插件：`E:\64DBG\build\X64DbgContextV2.dp64`
- 调试器：`C:\Users\Administrator\Desktop\VIP Tools\ABC\snapshot_2022-03-26_14-14\release\x64\x64dbg.exe`
- 部署插件：调试器目录下 `plugins\X64DbgContextV2.dp64`
- 回退事件：`$env:TEMP\x64dbg_ai_events.jsonl`
- 接收器日志：`$env:LOCALAPPDATA\SnapshotAssistant64\SnapshotAssistant64.log`

存在release-next或备份不代表它们是运行版本。默认使用release，缺失时报告，不擅自切换或安装。

## 检查和启动

PowerShell只读检查，按需读取尾部避免输出整个大日志：

```powershell
Get-Process -Name SnapshotAssistant64,x64dbg -ErrorAction SilentlyContinue |
    Select-Object Id,ProcessName,Path
Get-NetTCPConnection -LocalPort 8768 -State Listen -ErrorAction SilentlyContinue |
    Select-Object LocalAddress,LocalPort,OwningProcess
Get-Content -LiteralPath "$env:LOCALAPPDATA\SnapshotAssistant64\SnapshotAssistant64.log" -Tail 60 -ErrorAction SilentlyContinue
Get-Content -LiteralPath "$env:TEMP\x64dbg_ai_events.jsonl" -Tail 20 -ErrorAction SilentlyContinue
```

无监听结果时结合命令错误与进程信息判断；端口占用时查所属进程，不结束未知进程。用户要求启动且接收器尚未运行时：

```powershell
Start-Process -FilePath 'E:\64DBG\SnapshotAssistant64\release\SnapshotAssistant64.exe' -WindowStyle Hidden
```

用户明确要求看窗口时可用正常窗口模式。启动后检查进程与8768监听；监听成功不证明收到真实调试数据。

## XREF

以下在x64dbg命令栏执行，不在PowerShell执行。替换地址占位符：

|命令|用途|
|---|---|
|`x64ctx.xrefs.selected`|当前选择地址引用|
|`x64ctx.xrefs.disasm`|CPU反汇编选择地址引用，进入xref_only复制模式|
|`x64ctx.xrefs.call`|当前CALL目标的引用|
|`x64ctx.xrefs.query ADDRESS`|指定地址或表达式的引用|
|`x64ctx.xrefs.context REFERENCE_ADDRESS`|引用指令附近上下文|

插件菜单X64DbgContextV2提供对应操作；CPU反汇编右键“发送当前地址引用到 SnapshotAssistant”进入xref_only。引用上下文默认上10/下5。参数与错误行为详见 `E:\64DBG\src\xref_query.cpp`。

“读取当前References搜索表”试验功能已撤销，不使用 `x64ctx.references.send`，不把XREF查询描述为读取当前References表。

## 链路排查

- 配套端口为127.0.0.1:8768，32位工具使用8765。历史旧64位插件曾发送8765，仅凭文件名无法确认版本配套。
- 插件逐事件短连接，暂停静置没有新消息不等于断开。TCP失败写JSONL；接收器增量补收Snapshot并标记来源，历史XREF不自动恢复。
- JSONL可能有包装对象、重复、损坏或未完成末行。解析时参考 `SnapshotAssistant64\EventJsonParser.cs` 和 `FallbackTailReader.cs`，保留来源，不能将截断行当作无事件。
- 工具的诊断菜单提供读取回退日志和打开运行日志。原始日志有事件仅证明采集/写入；已接收或显示需接收日志或UI证据。
- 历史独立Windows探针曾超时而用户实际x64dbg仍收到现场。以当前真实链路证据判断，不用探针单一失败宣称整个环境不可用。

## 开发与部署时才读取

说明文件：`E:\64DBG\README.md`、`E:\64DBG\SnapshotAssistant64\README.md`。
完整记录：`E:\64DBG\docs\X64DBG_长期项目接续记录.md`，后续章节会更正前文状态。

源码：`src\snapshot_collector.cpp`负责采集，`src\event_transport.cpp`负责传输，`src\xref_query.cpp`负责引用；接收层重点为 `SnapshotAssistant64\EventJsonParser.cs`、`SnapshotStore.cs`、`SmartCopyComposer.cs`。

修改后在项目目录分别运行 `build.ps1` 和 `test.ps1`，先检查脚本里的SDK/MSVC绝对路径和本机工具链。自动回归与真实GUI联调分别报告。

仅在部署属于当前授权范围时执行 `install.ps1`。脚本覆盖实际插件，并拒绝在x64dbg运行时安装；不为绕过检查结束用户调试器。保留可核对的部署前备份。32位项目不属于本技能修改范围。
