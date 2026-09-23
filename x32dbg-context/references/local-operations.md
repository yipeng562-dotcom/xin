# 本机操作参考

新版直接读取功能及新版程序路径优先见 [live-export.md](live-export.md)。以下保留原版工具和原项目的操作信息。

## 路径与配套关系

- 用户程序：`E:\SnapshotAssistant.exe`
- 接收器项目：`E:\自动\SnapshotAssistant\SnapshotAssistant.csproj`
- 项目发布程序：`E:\自动\SnapshotAssistant\release\SnapshotAssistant.exe`
- 插件包装层/XREF源码：`E:\自动\src`
- 接收器测试：`E:\自动\SnapshotAssistant.Tests`
- 另一份项目：`C:\Users\Administrator\Desktop\自动`。默认维护E盘项目，不同时修改两份副本。
- 调试器：`C:\Users\Administrator\Desktop\VIP Tools\ABC\snapshot_2022-03-26_14-14\release\x32\x32dbg.exe`
- 插件部署目录：上述调试器目录下的 `plugins`。
- 回退日志：`$env:TEMP\x32dbg_ai_events.jsonl`
- 运行日志：`$env:LOCALAPPDATA\SnapshotAssistant\SnapshotAssistant.log`

创建技能时核对：用户程序与两个项目release中的程序SHA256均为 `FA73CBD1D281F5025106C0A2D769B55614E0D0D4A68FC9830AD2D0194C6E8A00`。这是当时的配套证据；后续更新应重新核对，不强制还原旧哈希。

接收器是C#/.NET 8 Windows Forms，发布脚本使用win-x64，接收的数据来自32位目标。接收器进程架构不等于目标架构；不能因win-x64将它当作64DBG。框架依赖发布需要对应.NET 8 Windows Desktop Runtime。

## 检查与启动

PowerShell只读检查：

```powershell
Get-Process -Name SnapshotAssistant,x32dbg -ErrorAction SilentlyContinue |
    Select-Object Id,ProcessName,Path
Get-NetTCPConnection -LocalPort 8765 -State Listen -ErrorAction SilentlyContinue |
    Select-Object LocalAddress,LocalPort,OwningProcess
Get-Content -LiteralPath "$env:LOCALAPPDATA\SnapshotAssistant\SnapshotAssistant.log" -Tail 60 -ErrorAction SilentlyContinue
Get-Content -LiteralPath "$env:TEMP\x32dbg_ai_events.jsonl" -Tail 20 -ErrorAction SilentlyContinue
```

按需读尾部，不无差别输出整个日志。端口占用时查所属进程，不自动结束占用者。无结果需结合命令错误和进程信息判断。

用户要求启动、文件存在且尚未运行接收器时：

```powershell
Start-Process -FilePath 'E:\SnapshotAssistant.exe' -WindowStyle Hidden
```

用户明确要求看窗口时可用正常窗口模式。启动后检查进程与8765监听，监听成功不证明收到真实现场。用户程序缺失时检查项目release并说明所选路径，不擅自复制覆盖。

当前版本通过“工具 > 诊断 > 读取回退日志”手动导入；不要承诺启动后自动增量补收。读取日志做分析与通过GUI导入改变显示是不同操作。解析包装数据参考 `EventJsonParser.cs`、`FallbackImporter.cs`；导入后核对时间与会话，避免把旧现场当作实时状态。

端口为127.0.0.1:8765。64DBG为8768；历史其他插件也可能使用8765，要核对实际占用者及事件source。

## XREF操作

以下命令在x32dbg命令栏执行，不能在PowerShell执行；替换地址占位符：

|命令|作用|
|---|---|
|`x32ctx.xrefs.selected`|当前选择地址的引用|
|`x32ctx.xrefs.disasm`|CPU反汇编选择地址引用，xref_only模式|
|`x32ctx.xrefs.call`|当前CALL目标引用|
|`x32ctx.xrefs.query ADDRESS`|指定地址/表达式引用|
|`x32ctx.xrefs.context REFERENCE_ADDRESS`|指定引用指令附近上下文|

CPU反汇编右键“发送当前地址引用到 SnapshotAssistant”进入xref_only。x32dbg自带References查找界面不会因此自动发送其表格内容。参数和错误处理见 `E:\自动\src\xref_query.cpp`。

## 开发与部署

按需读取 `E:\自动\SnapshotAssistant\README.md` 与 `E:\自动\X32DbgContextV2_项目接续记录.md`。历史部署文件大小和状态需重新检查。

接收器逻辑在 `MainForm.cs`、`SnapshotStore.cs`、`EventJsonParser.cs`、`SnapshotFormatter.cs`、`SmartCopyComposer.cs`。

- 接收器修改：运行 `E:\自动\SnapshotAssistant\build.ps1`，脚本包含还原、编译、测试和发布到release。不会自动更新根目录的 `E:\SnapshotAssistant.exe`，是否替换依当前任务范围决定。
- 包装层/XREF修改：运行项目根目录 `build.ps1` 和 `test.ps1`；先检查SDK/MSVC路径及备份依赖。
- 部署仅在当前请求授权范围内执行根目录 `install.ps1`。脚本拒绝x32dbg运行时安装，不为绕过检查结束用户调试器，部署前保留可核对的备份。

32位插件包含 `X32DbgContextV2.before_objective_collector.dp32` 包装层和 `X32DbgContextV2.original_core.dll`。包装层调用保留的原始核心；根构建脚本从 `backups\20260902_before_xref\X32DbgContextV2.before_objective_collector.dp32` 复制核心，安装脚本验证它与备份哈希一致。

已找到的是接收器及包装层/XREF源码；本项目不包含重新实现原始Snapshot采集核心的源码。不能宣称可仅凭现有C++源码重建核心，不能删除核心DLL或套用64位单体插件架构。不要修改64DBG项目。
