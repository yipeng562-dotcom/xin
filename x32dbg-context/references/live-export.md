# 直接读取新版接收器

本机程序：`E:\CodexDbgReceivers\x32\SnapshotAssistant.exe`。
数据文件：`%LOCALAPPDATA%\CodexDbg\x32\live\latest.json`。
新版日志：同目录 `receiver.log`。旧版程序、源码和调试器插件保持原样。

每秒导出一次，写临时文件后原子替换。心跳来自接收器，不会主动运行/暂停/单步调试目标；只有请求技能时才读取，不是对话外持续监控。

schema版本1：`export_schema_version`、`target_architecture`、`exported_at_utc`、`receiver`、`state`。receiver记录PID、程序路径、实例ID、启动时间、running、listening、port。state记录session_id、debug_state、last_event_at、last_accepted_event、cpu、cpu_observation、browse、xrefs、xref_context、xref_copy_pending及当前引用目标。内部属性名使用snake_case；寄存器字典保留EIP/RIP等原名。事件保留raw_json、origin和received_at；CPU/browse还有目标timestamp_ms与sequence。

导出不自动修复旧32位状态逻辑。不同会话的CPU和browse不得合并；最后收到的事件不必是最高序号。XREF协议没有可靠会话ID，不能把引用结果自动视为当前CPU所属会话。

正常关闭时running=false；强制结束无法写退出状态，所以必须同时核对PID、程序路径与心跳。心跳超过10秒的文件作为过期数据报告，不能默默当成实时现场。导出重启时先清空上次进程数据；64位原有回退补收可能随后加载历史Snapshot，仍需检查origin和目标时间。

`--export-dir PATH`可覆盖导出目录；`--port NUMBER`保留原参数。非默认端口默认导出到相应 `port-NUMBER` 目录，避免测试覆盖正式文件。文件写入失败记录日志并继续接收，不能因此把残留文件认定为最新。writer.lock防止多个新版写同一文件；不要删除活动锁来绕过它。

原版本相关路径、命令与维护信息仍见local-operations.md；正常使用新版以本文路径为准。32位仍手动读取回退日志，64位保留原有自动补收。
