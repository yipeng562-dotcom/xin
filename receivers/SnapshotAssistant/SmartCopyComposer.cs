namespace SnapshotAssistant;

public sealed record SmartCopyDecision(
    string ButtonText,
    bool Enabled,
    string Text,
    XrefsResultEvent? IncludedXrefs)
{
    public bool IncludesXref => IncludedXrefs is not null;
}

public static class SmartCopyComposer
{
    public static SmartCopyDecision Compose(SnapshotStore store)
    {
        var cpu = store.LatestCpuObservation ?? store.LastCpuSnapshot;
        var browse = HasAddress(store.CurrentSelectionSnapshot?.SelectedAddress)
            ? store.CurrentSelectionSnapshot
            : null;
        var xrefs = MatchingXrefs(store, cpu, browse);
        var abnormal = cpu is not null && (!cpu.RegistersValid || !cpu.SnapshotConsistent);
        var running = store.DebugState.Equals("running", StringComparison.OrdinalIgnoreCase);

        if(cpu is null && browse is null && xrefs is null)
            return new SmartCopyDecision("等待 x32dbg...", false, "", null);

        if(xrefs is not null && xrefs.CopyMode.Equals("xref_only", StringComparison.OrdinalIgnoreCase))
        {
            var xrefText = SnapshotFormatter.FormatXrefs(xrefs, "全部", MatchingContext(store, xrefs));
            return new SmartCopyDecision("复制 XREF", true, xrefText, xrefs);
        }

        var sections = new List<string>();
        if(running && cpu is not null)
            sections.Add("===== 状态提醒 =====\r\n\r\nCPU正在运行，以下CPU内容来自最后一次暂停现场。");

        var includeBrowse = browse is not null && (cpu is null || running || !SameAddress(cpu.Eip, browse.SelectedAddress));
        if(cpu is not null)
            sections.Add(SnapshotFormatter.FormatCpu(cpu, false, false, browse));
        if(includeBrowse)
            sections.Add(SnapshotFormatter.FormatBrowse(browse, cpu));
        if(xrefs is not null)
            sections.Add(SnapshotFormatter.FormatXrefs(xrefs, "全部", MatchingContext(store, xrefs)));

        var buttonText = ButtonText(cpu, browse, includeBrowse, running, xrefs is not null);
        if(abnormal)
            buttonText = $"⚠ {buttonText}";
        return new SmartCopyDecision(buttonText, true, string.Join("\r\n\r\n", sections), xrefs);
    }

    public static XrefsResultEvent? MatchingXrefs(
        SnapshotStore store,
        SnapshotEvent? cpu = null,
        SnapshotEvent? browse = null)
    {
        var xrefs = store.LatestXrefs;
        if(!store.XrefCopyPending || xrefs is null || xrefs.Error.Length > 0 || !HasAddress(xrefs.TargetAddress))
            return null;

        cpu ??= store.LatestCpuObservation ?? store.LastCpuSnapshot;
        browse ??= store.CurrentSelectionSnapshot;
        if(HasAddress(browse?.SelectedAddress) && SameAddress(xrefs.TargetAddress, browse!.SelectedAddress))
            return xrefs;
        if(cpu is not null && SnapshotFormatter.TryGetCall(cpu.CurrentInstruction, out var call) &&
           SameAddress(xrefs.TargetAddress, call.Target))
            return xrefs;
        if(HasAddress(store.CurrentExplicitXrefTarget) && SameAddress(xrefs.TargetAddress, store.CurrentExplicitXrefTarget))
            return xrefs;
        return null;
    }

    private static string ButtonText(
        SnapshotEvent? cpu,
        SnapshotEvent? browse,
        bool includeBrowse,
        bool running,
        bool includesXref)
    {
        string text;
        if(cpu is null)
            text = browse is null
                ? includesXref ? "复制 XREF" : "等待 x32dbg..."
                : "复制浏览位置";
        else if(running)
            text = includeBrowse ? "复制最后暂停现场 + 浏览" : "复制最后暂停现场";
        else
            text = includeBrowse ? "复制 CPU + 浏览" : "复制 CPU 现场";
        return includesXref && text != "复制 XREF" ? $"{text} + XREF" : text;
    }

    private static XrefContextEvent? MatchingContext(SnapshotStore store, XrefsResultEvent xrefs) =>
        store.LatestXrefContext is { } context && SameAddress(context.TargetAddress, xrefs.TargetAddress)
            ? context
            : null;

    private static bool HasAddress(string? value) =>
        !string.IsNullOrWhiteSpace(value) && !value.Equals("00000000", StringComparison.OrdinalIgnoreCase);

    private static bool SameAddress(string left, string right) =>
        left.Equals(right, StringComparison.OrdinalIgnoreCase);
}
