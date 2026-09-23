# 调试现场助手：直接读取版

两个Codex技能与配套接收器源码。接收器收到插件数据后，每秒把完整状态保存为JSON；调用技能即可读取，无需智能复制粘贴。原有界面、智能复制、XREF、CPU/浏览分离功能保留。

|技能|配套目标|端口|默认导出文件|
|---|---|---|---|
|[x32dbg-context](x32dbg-context/SKILL.md)|x32dbg / SnapshotAssistant|8765|`%LOCALAPPDATA%\CodexDbg\x32\live\latest.json`|
|[x64dbg-context](x64dbg-context/SKILL.md)|x64dbg / SnapshotAssistant64|8768|`%LOCALAPPDATA%\CodexDbg\x64\live\latest.json`|

## 下载新版

- [32位目标接收器](downloads/x32/SnapshotAssistant.exe)（打开链接后选择下载原始文件）
- [64位目标接收器](downloads/x64/SnapshotAssistant64.exe)
- [SHA256校验值](downloads/SHA256SUMS.txt)

两个EXE均需要.NET 8 Windows Desktop Runtime。它们是本仓库源码编译的独立副本，使用原来的调试器插件即可。

## 使用

1. 将两个技能目录复制到个人Codex技能目录，通常为 `~/.codex/skills/`。
2. 启动已有调试器和配套插件，使用本仓库构建的新版接收器。同一架构的新旧接收器共用端口，请只运行其中一个。
3. 对Codex说“使用 $x32dbg-context 看当前现场”或“使用 $x64dbg-context 看当前现场”。

本机新版安装在 `E:\CodexDbgReceivers\x32` 和 `E:\CodexDbgReceivers\x64`。两个目录各有 `启动接收器.cmd`，也可直接打开其中的EXE。原来的 `E:\SnapshotAssistant.exe`、`E:\自动`、`E:\64DBG` 和调试器插件保持不动。在其他电脑使用时调整技能中的程序路径。

新版仅增加本机文件导出，不新增监听端口，不主动运行/单步/修改目标。Codex在任务中读取文件，不会在对话外持续自动接收。CPU和浏览分别保存，完整栈与汇编不受UI折叠影响，读取不消费一次性XREF复制状态。

## 构建

Windows + .NET 8 SDK：

```powershell
.\receivers\build.ps1
# 或指定独立SDK
.\receivers\build.ps1 -Dotnet 'C:\path\to\dotnet.exe'
```

脚本运行两套回归测试，再输出 `dist\SnapshotAssistant\SnapshotAssistant.exe` 和 `dist\SnapshotAssistant64\SnapshotAssistant64.exe`。运行需要x64的.NET 8 Windows Desktop Runtime。接收器进程为x64，不代表32位工具的调试目标变为64位。

本仓库包含独立复制的接收器源码、原回归测试及新增导出测试，不包含调试器插件和原始采集核心。32位插件仍依赖现有的原始核心DLL，不需要重装或重建插件。

## 读取与故障判断

每个技能的 `scripts/read-live.ps1` 会检查格式、进程和10秒心跳，输出 `available`、`waiting_for_event`、`offline`、`stale` 或 `not_listening`。心跳新鲜只说明接收器在运行，不证明目标刚执行；还需检查事件时间、会话、有效性和TCP/Fallback来源。

32位原有状态逻辑可能保留旧会话CPU；技能会标注会话不一致。XREF协议没有可靠的会话ID，需核对目标与时间。32位回退仍手动导入，64位保留自动补收。

导出日志在 `latest.json` 同目录的 `receiver.log`。文件写入失败会保留旧文件并记录错误，接收和复制继续工作。正常退出标记offline；强制结束通过进程和心跳识别。多个新版接收器不能同时写同一导出目录。

开发测试可使用 `--port NUMBER --export-dir PATH`，把测试数据与正式导出隔离。实际现场文件、日志和临时构建目录不提交到Git；downloads仅保存经过测试的两个发布程序和启动入口。

## 本次验证

两套原有接收器回归测试和新增导出测试通过。新增覆盖CPU/浏览分离、完整地址、XREF复制状态、回退来源、并发读取、写入失败、重启清空与关闭状态。`receivers/test-live.ps1` 使用临时端口和隔离目录验证实际EXE接收TCP、导出文件、技能读取、过期识别和正常退出。

这些是合成事件测试；未操作用户的调试目标，也未将它们描述为真实断点/单步联调。
