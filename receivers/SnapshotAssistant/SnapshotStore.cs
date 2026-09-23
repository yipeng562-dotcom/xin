namespace SnapshotAssistant;

public enum AddResult
{
    Added,
    Duplicate
}

public sealed class SnapshotStore
{
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    public SnapshotEvent? LastCpuSnapshot { get; private set; }
    public SnapshotEvent? LatestCpuObservation { get; private set; }
    public SnapshotEvent? PreviousCpu { get; private set; }
    public SnapshotEvent? CurrentSelectionSnapshot { get; private set; }
    public XrefsResultEvent? LatestXrefs { get; private set; }
    public XrefContextEvent? LatestXrefContext { get; private set; }
    public bool XrefCopyPending { get; private set; }
    public string CurrentExplicitXrefTarget { get; private set; } = "00000000";
    public string SessionId { get; private set; } = "-";
    public string DebugState { get; private set; } = "unknown";
    public DateTime? LastEventAt { get; private set; }

    public AddResult Add(ReceivedEvent received)
    {
        if(received.Origin == EventOrigin.Fallback && !_seen.Add(received.DedupKey))
            return AddResult.Duplicate;
        _seen.Add(received.DedupKey);
        LastEventAt = received.ReceivedAt;

        switch(received)
        {
        case SnapshotEvent snapshot:
            AddSnapshot(snapshot);
            break;
        case XrefsResultEvent xrefs:
            LatestXrefs = xrefs;
            CurrentExplicitXrefTarget = xrefs.TargetAddress;
            XrefCopyPending = xrefs.Error.Length == 0 && HasAddress(xrefs.TargetAddress);
            break;
        case XrefContextEvent context:
            LatestXrefContext = context;
            break;
        }
        return AddResult.Added;
    }

    public bool RearmXrefsForCopy()
    {
        if(LatestXrefs is null || LatestXrefs.Error.Length > 0 || !HasAddress(LatestXrefs.TargetAddress))
            return false;
        XrefCopyPending = true;
        return true;
    }

    public bool ConsumeXrefsAfterSuccessfulCopy(XrefsResultEvent copiedXrefs)
    {
        if(!XrefCopyPending || !ReferenceEquals(LatestXrefs, copiedXrefs))
            return false;
        XrefCopyPending = false;
        return true;
    }

    public void ClearXrefs()
    {
        LatestXrefs = null;
        LatestXrefContext = null;
        CurrentExplicitXrefTarget = "00000000";
        XrefCopyPending = false;
    }

    public bool IsCpuSnapshot(SnapshotEvent snapshot)
    {
        if(!snapshot.RegistersValid || snapshot.EventType.Equals("selection_changed", StringComparison.OrdinalIgnoreCase))
            return false;
        var state = snapshot.DebugState.ToLowerInvariant();
        var type = snapshot.EventType.ToLowerInvariant();
        if(type is "debug_resumed" or "debug_stopped")
            return false;
        return state == "paused" || type is "debug_paused" or "step_completed" or "breakpoint_hit" or "exception";
    }

    public SnapshotEvent? PreviousCpuSnapshot(SnapshotEvent current) =>
        ReferenceEquals(current, LastCpuSnapshot) ? PreviousCpu : null;

    private void AddSnapshot(SnapshotEvent snapshot)
    {
        if(snapshot.SessionId.Length > 0)
            SessionId = snapshot.SessionId;
        if(snapshot.DebugState.Length > 0)
            DebugState = snapshot.DebugState;

        if(IsCpuObservation(snapshot))
            LatestCpuObservation = snapshot;

        if(IsCpuSnapshot(snapshot))
        {
            PreviousCpu = LastCpuSnapshot;
            LastCpuSnapshot = snapshot;
        }

        if(snapshot.EventType.Equals("selection_changed", StringComparison.OrdinalIgnoreCase) ||
           snapshot.SelectedAddress != "00000000")
            CurrentSelectionSnapshot = snapshot;
    }

    private static bool IsCpuObservation(SnapshotEvent snapshot)
    {
        if(snapshot.EventType.Equals("selection_changed", StringComparison.OrdinalIgnoreCase))
            return false;
        var state = snapshot.DebugState.ToLowerInvariant();
        var type = snapshot.EventType.ToLowerInvariant();
        if(type is "debug_resumed" or "debug_stopped")
            return false;
        return state == "paused" || type is "debug_paused" or "step_completed" or "breakpoint_hit" or "exception";
    }

    private static bool HasAddress(string? value) =>
        !string.IsNullOrWhiteSpace(value) && !value.Equals("00000000", StringComparison.OrdinalIgnoreCase);
}
