using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SnapshotAssistant;

public sealed record CallDetails(string Address, string Target, int Size, string ReturnAddress);

public static partial class SnapshotFormatter
{
    private static readonly string[] Registers =
        ["EAX", "EBX", "ECX", "EDX", "ESI", "EDI", "EBP", "ESP", "EIP"];

    public static string FormatCpu(
        SnapshotEvent? snapshot,
        bool fullStack,
        bool fullAssembly,
        SnapshotEvent? selection = null)
    {
        if(snapshot is null)
            return "===== CPU真实现场 =====\r\n\r\n暂无有效CPU现场。";

        var browseAddress = selection?.SelectedAddress;
        if(string.IsNullOrWhiteSpace(browseAddress) || browseAddress == "00000000")
            browseAddress = snapshot.SelectedAddress;
        var hasBrowse = !string.IsNullOrWhiteSpace(browseAddress) && browseAddress != "00000000";
        var relation = !hasBrowse
            ? "未知"
            : snapshot.Eip.Equals(browseAddress, StringComparison.OrdinalIgnoreCase) ? "一致" : "不一致";
        var builder = new StringBuilder();
        builder.AppendLine("===== CPU真实现场 =====").AppendLine();
        builder.AppendLine($"真实EIP：{snapshot.Eip}");
        builder.AppendLine($"浏览地址：{(hasBrowse ? browseAddress : "暂无")}");
        builder.AppendLine($"CPU/浏览：{relation}");
        if(snapshot.RegistersValid && snapshot.SnapshotConsistent)
        {
            builder.AppendLine("现场有效：是");
        }
        else
        {
            builder.AppendLine("⚠ 现场有效：否");
            if(!snapshot.RegistersValid)
                builder.AppendLine("⚠ registers_valid=false：寄存器现场无效");
            if(!snapshot.SnapshotConsistent)
                builder.AppendLine("⚠ snapshot_consistent=false：Snapshot一致性异常");
        }
        builder.AppendLine();
        builder.AppendLine($"事件：{snapshot.EventType}");
        builder.AppendLine($"Snapshot：#{snapshot.Sequence}");
        builder.AppendLine($"CPU状态：{StateName(snapshot.DebugState)}");
        builder.AppendLine($"模块/RVA：{HexUtil.ModuleRva(snapshot.CurrentInstruction)}");
        builder.AppendLine($"当前指令：{InstructionLine(snapshot.CurrentInstruction)}");

        builder.AppendLine().AppendLine("===== 寄存器 =====").AppendLine();
        foreach(var name in Registers)
            builder.AppendLine($"{name}={Register(snapshot, name)}");
        var eflags = Register(snapshot, "EFLAGS");
        builder.AppendLine($"EFLAGS={eflags}");
        builder.AppendLine($"FLAGS：{FlagSummary(eflags)}");

        builder.AppendLine().AppendLine("===== 栈槽 =====").AppendLine();
        var stackLimit = fullStack ? 16 : 4;
        foreach(var slot in snapshot.Stack.Take(stackLimit))
            builder.AppendLine($"ESP+{OffsetShort(slot.Offset)} [{slot.Address}] = {slot.Value}");
        if(snapshot.Stack.Count == 0)
            builder.AppendLine("暂无栈数据");

        builder.AppendLine().AppendLine("===== CPU附近汇编 =====").AppendLine();
        var assembly = fullAssembly
            ? snapshot.DisassemblyContext
            : SliceContext(snapshot.DisassemblyContext, snapshot.Eip, 12, 20);
        foreach(var instruction in assembly)
            builder.AppendLine($"{(instruction.Address == snapshot.Eip ? "→" : " ")} {InstructionLine(instruction)}");
        if(assembly.Count == 0)
            builder.AppendLine("暂无汇编上下文");

        if(TryGetCall(snapshot.CurrentInstruction, out var call))
        {
            builder.AppendLine().AppendLine("===== CALL现场 =====").AppendLine();
            builder.AppendLine($"CALL地址：{call.Address}");
            builder.AppendLine($"CALL目标：{call.Target}");
            builder.AppendLine($"指令长度：{call.Size}");
            builder.AppendLine($"理论返回地址：{call.ReturnAddress}");
            builder.AppendLine().AppendLine("CALL前附近汇编：");
            var callIndex = IndexOf(snapshot.DisassemblyContext, call.Address);
            if(callIndex >= 0)
            {
                foreach(var instruction in snapshot.DisassemblyContext.Skip(Math.Max(0, callIndex - 10)).Take(Math.Min(11, callIndex + 1)))
                    builder.AppendLine($"{(instruction.Address == call.Address ? "→" : " ")} {InstructionLine(instruction)}");
            }
        }
        return builder.ToString().TrimEnd();
    }

    public static string FormatBrowse(SnapshotEvent? selection, SnapshotEvent? cpu, bool fullAssembly = false)
    {
        if(selection is null)
            return "===== 当前浏览位置【静态】 =====\r\n\r\n暂无浏览位置。";

        var cpuEip = cpu?.Eip ?? "00000000";
        var same = cpu is not null && selection.SelectedAddress == cpuEip;
        var builder = new StringBuilder();
        builder.AppendLine("===== 当前浏览位置【静态】 =====").AppendLine();
        builder.AppendLine($"来源窗口：{(selection.Window.Length > 0 ? selection.Window : "未知")}");
        builder.AppendLine($"CPU真实EIP：{(cpu is null ? "暂无有效CPU现场" : cpuEip)}");
        builder.AppendLine($"浏览地址：{selection.SelectedAddress}");
        builder.AppendLine($"一致：{(same ? "是" : "否")}");
        if(same)
            builder.AppendLine("✓ CPU与浏览位置一致");
        else if(cpu is not null)
            builder.AppendLine($"⚠ CPU没有执行到浏览地址。CPU真实停在{cpuEip}，当前只是静态浏览{selection.SelectedAddress}。禁止套用当前寄存器计算静态浏览指令的实际地址。");
        builder.AppendLine($"选中指令：{InstructionLine(selection.SelectedInstruction)}");

        builder.AppendLine().AppendLine("===== 浏览附近静态汇编 =====").AppendLine();
        var assembly = fullAssembly
            ? selection.DisassemblyContext
            : SliceContext(selection.DisassemblyContext, selection.SelectedAddress, 12, 20);
        foreach(var instruction in assembly)
            builder.AppendLine($"{(instruction.Address == selection.SelectedAddress ? "→" : " ")} {InstructionLine(instruction)}");
        if(assembly.Count == 0)
            builder.AppendLine("暂无浏览汇编上下文");
        return builder.ToString().TrimEnd();
    }

    public static string FormatCall(SnapshotEvent? cpu)
    {
        var builder = new StringBuilder();
        builder.AppendLine("===== CALL现场 =====").AppendLine();
        if(cpu is null || !TryGetCall(cpu.CurrentInstruction, out var call))
        {
            builder.AppendLine("当前 CPU 指令不是可可靠解析的直接 CALL。");
            return builder.ToString().TrimEnd();
        }
        builder.AppendLine($"CALL地址：{call.Address}");
        builder.AppendLine($"CALL目标：{call.Target}");
        builder.AppendLine($"指令长度：{call.Size}");
        builder.AppendLine($"理论返回地址：{call.ReturnAddress}");
        builder.AppendLine().AppendLine("CALL前附近汇编：");
        var index = IndexOf(cpu.DisassemblyContext, call.Address);
        if(index < 0)
        {
            builder.AppendLine("暂无 CALL 附近汇编。");
            return builder.ToString().TrimEnd();
        }
        var start = Math.Max(0, index - 10);
        foreach(var instruction in cpu.DisassemblyContext.Skip(start).Take(index - start + 1))
            builder.AppendLine($"{(instruction.Address == call.Address ? "→" : " ")} {InstructionLine(instruction)}");
        return builder.ToString().TrimEnd();
    }

    public static string FormatXrefs(XrefsResultEvent? xrefs, string filter, XrefContextEvent? context = null)
    {
        if(xrefs is null)
            return "===== XREF / 引用 =====\r\n\r\n尚未收到XREF查询结果。";
        var normalized = string.IsNullOrWhiteSpace(filter) ? "全部" : filter;
        var references = normalized == "全部"
            ? xrefs.References
            : xrefs.References.Where(item => item.Type.Equals(normalized, StringComparison.OrdinalIgnoreCase)).ToArray();
        var builder = new StringBuilder();
        builder.AppendLine("===== XREF / 引用 =====").AppendLine();
        builder.AppendLine($"目标：{xrefs.TargetAddress}");
        builder.AppendLine($"查询：{xrefs.Query}");
        builder.AppendLine($"总数：{xrefs.Count}");
        builder.AppendLine($"筛选：{normalized}（显示{references.Count}条）");
        if(xrefs.Error.Length > 0)
            builder.AppendLine($"错误：{xrefs.Error}");
        builder.AppendLine();
        foreach(var reference in references)
        {
            var module = reference.Module.Length > 0 ? $" {reference.Module}+{reference.Rva}" : "";
            builder.AppendLine($"{reference.Address}  {reference.Type,-7} {reference.Instruction}{module}");
        }
        if(references.Count == 0)
            builder.AppendLine("没有符合条件的引用。 ");

        if(context is not null && context.TargetAddress == xrefs.TargetAddress)
        {
            builder.AppendLine().AppendLine($"===== 引用上下文 {context.ReferenceAddress} =====").AppendLine();
            if(context.Error.Length > 0)
                builder.AppendLine($"错误：{context.Error}");
            foreach(var line in context.Context)
                builder.AppendLine($"{(line.Current ? "→" : " ")} {line.Address} {line.Instruction}");
        }
        return builder.ToString().TrimEnd();
    }

    public static string FormatComparison(SnapshotEvent? current, SnapshotEvent? previous)
    {
        var builder = new StringBuilder();
        builder.AppendLine("===== 与上一CPU Snapshot变化 =====").AppendLine();
        if(current is null || previous is null)
        {
            builder.AppendLine("暂无可比较的上一CPU Snapshot。");
            return builder.ToString().TrimEnd();
        }

        var changed = 0;
        foreach(var name in Registers.Append("EFLAGS"))
        {
            var before = Register(previous, name);
            var after = Register(current, name);
            if(before == after)
                continue;
            changed++;
            builder.AppendLine($"{name}：");
            builder.AppendLine(before);
            builder.AppendLine($"→ {after}").AppendLine();
        }

        var beforeFlags = DecodeFlags(Register(previous, "EFLAGS"));
        var afterFlags = DecodeFlags(Register(current, "EFLAGS"));
        foreach(var name in new[] { "CF", "ZF", "SF", "OF", "PF" })
        {
            if(beforeFlags[name] == afterFlags[name])
                continue;
            changed++;
            builder.AppendLine($"{name}：{beforeFlags[name]} → {afterFlags[name]}");
        }
        if(changed == 0)
            builder.AppendLine("没有发生变化的寄存器或默认标志位。");
        return builder.ToString().TrimEnd();
    }

    public static string FormatCpuAndBrowse(SnapshotEvent? cpu, SnapshotEvent? selection) =>
        FormatCpu(cpu, false, false) + "\r\n\r\n" + FormatBrowse(selection, cpu);

    public static string FormatComplete(
        SnapshotEvent? cpu,
        SnapshotEvent? selection,
        SnapshotEvent? previousCpu,
        XrefsResultEvent? xrefs,
        XrefContextEvent? context)
    {
        var current = selection ?? cpu;
        var builder = new StringBuilder();
        builder.AppendLine("===== x32dbg Context =====").AppendLine();
        builder.AppendLine($"Snapshot：#{current?.Sequence ?? 0}");
        builder.AppendLine($"事件：{current?.EventType ?? "暂无"}");
        builder.AppendLine($"CPU状态：{StateName(current?.DebugState ?? "unknown")}");
        builder.AppendLine($"真实EIP：{cpu?.Eip ?? "暂无有效CPU现场"}");
        builder.AppendLine($"浏览地址：{selection?.SelectedAddress ?? "暂无"}");
        builder.AppendLine($"一致：{(cpu is not null && selection is not null && cpu.Eip == selection.SelectedAddress ? "是" : "否")}");
        builder.AppendLine().AppendLine(FormatCpu(cpu, true, true));
        builder.AppendLine().AppendLine(FormatBrowse(selection, cpu, true));
        builder.AppendLine().AppendLine(FormatXrefs(xrefs, "全部", context));
        builder.AppendLine().AppendLine(FormatComparison(cpu, previousCpu));
        return builder.ToString().TrimEnd();
    }

    public static bool TryGetCall(InstructionInfo? instruction, out CallDetails details)
    {
        details = new CallDetails("00000000", "00000000", 0, "00000000");
        if(instruction is null)
            return false;
        var match = DirectCallRegex().Match(instruction.Instruction);
        if(!match.Success || !HexUtil.TryParse(instruction.Address, out var address) ||
           !HexUtil.TryParse(match.Groups[1].Value, out var target) || instruction.Size <= 0)
            return false;
        details = new CallDetails(
            address.ToString("X8", CultureInfo.InvariantCulture),
            target.ToString("X8", CultureInfo.InvariantCulture),
            instruction.Size,
            unchecked(address + (uint)instruction.Size).ToString("X8", CultureInfo.InvariantCulture));
        return true;
    }

    public static IReadOnlyDictionary<string, int> DecodeFlags(string eflags)
    {
        HexUtil.TryParse(eflags, out var value);
        return new Dictionary<string, int>
        {
            ["CF"] = (int)(value & 1),
            ["PF"] = (int)((value >> 2) & 1),
            ["ZF"] = (int)((value >> 6) & 1),
            ["SF"] = (int)((value >> 7) & 1),
            ["OF"] = (int)((value >> 11) & 1)
        };
    }

    public static IReadOnlyList<InstructionInfo> SliceContext(
        IReadOnlyList<InstructionInfo> context,
        string currentAddress,
        int before,
        int after)
    {
        var index = IndexOf(context, currentAddress);
        if(index < 0)
            return context.Take(before + 1 + after).ToArray();
        var start = Math.Max(0, index - before);
        var end = Math.Min(context.Count - 1, index + after);
        return context.Skip(start).Take(end - start + 1).ToArray();
    }

    private static int IndexOf(IReadOnlyList<InstructionInfo> context, string address)
    {
        for(var i = 0; i < context.Count; i++)
        {
            if(context[i].Address.Equals(address, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    private static string Register(SnapshotEvent snapshot, string name)
    {
        if(name == "EIP" && snapshot.Registers.TryGetValue(name, out var valueEip))
            return valueEip;
        return snapshot.Registers.TryGetValue(name, out var value) ? value : "00000000";
    }

    private static string FlagSummary(string eflags)
    {
        var flags = DecodeFlags(eflags);
        return string.Join("  ", new[] { "CF", "ZF", "SF", "OF", "PF" }.Select(name => $"{name}={flags[name]}"));
    }

    private static string InstructionLine(InstructionInfo? instruction)
    {
        if(instruction is null)
            return "暂无";
        var bytes = instruction.Bytes.Length > 0 ? $" {instruction.Bytes}" : "";
        var label = instruction.Label.Length > 0 ? $" <{instruction.Label}>" : "";
        return $"{instruction.Address}{bytes} {instruction.Instruction}{label}".TrimEnd();
    }

    private static string OffsetShort(string offset)
    {
        return HexUtil.TryParse(offset, out var value) ? value.ToString("X2", CultureInfo.InvariantCulture) : offset;
    }

    private static string StateName(string state) => state.ToLowerInvariant() switch
    {
        "paused" => "Paused",
        "running" => "Running",
        "stopped" => "Stopped",
        _ => state
    };

    [GeneratedRegex(@"^\s*call\s+(?:0x)?([0-9A-Fa-f]{1,8})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DirectCallRegex();
}
