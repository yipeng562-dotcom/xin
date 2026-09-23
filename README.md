# 调试现场助手技能

供 Codex 使用的两个个人技能，复用本机已有调试工具。

| 技能 | 配套工具 | 事件端口 |
| --- | --- | --- |
| [x32dbg-context](x32dbg-context/SKILL.md) | x32dbg / SnapshotAssistant | 8765 |
| [x64dbg-context](x64dbg-context/SKILL.md) | x64dbg / SnapshotAssistant64 | 8768 |

## 使用

将所需技能文件夹放入个人 Codex 技能目录（通常为 `~/.codex/skills/`），然后调用：

```text
使用 $x32dbg-context 帮我分析当前32位调试现场。
使用 $x64dbg-context 帮我分析当前64位调试现场。
```

技能包含操作说明、现场解读规则及本机路径参考，不包含调试器、插件、接收器二进制或它们的源码。它们不会单独提供远程调试控制接口。

目前路径按作者本机配置：32位接收器为 `E:\SnapshotAssistant.exe`，项目为 `E:\自动`；64位项目为 `E:\64DBG`。在其他电脑上使用前，请调整技能和引用文档中的工具路径并安装对应工具。

32位工具通过菜单手动读取回退日志；64位接收器支持增量补收Snapshot。32位插件还依赖保留的原始核心DLL，不能仅靠包装层源码重建该核心。
