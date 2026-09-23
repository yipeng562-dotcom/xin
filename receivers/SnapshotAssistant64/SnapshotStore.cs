namespace SnapshotAssistant;

public enum AddResult
{
    Added,
    Duplicate
}

public sealed class SnapshotStore
{
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private long _latestTimestamp;
    private long _latestSequence = -1;
    private long _latestCpuSequence = -1;
    private long _latestBrowseSequence = -1;

    public SnapshotEvent? LastCpuSnapshot { get; private set; }
    public SnapshotEvent? LatestCpuObservation { get; private set; }
    public SnapshotEvent? PreviousCpu { get; private set; }
    public SnapshotEvent? CurrentSelectionSnapshot { get; private set; }
    public XrefsResultEvent? LatestXrefs { get; private set; }
    public XrefContextEvent? LatestXrefContext { get; private set; }
    public bool XrefCopyPending { get; private set; }
    public string CurrentExplicitXrefTarget { get; private set; } = HexUtil.Zero;
    public string SessionId { get; private set; } = "-";
    public string DebugState { get; private set; } = "unknown";
    public DateTime? LastEventAt { get; private set; }

    public AddResult Add(ReceivedEvent received)
    {
        if(received is SnapshotEvent incoming)
        {
            if(incoming.SessionId != SessionId && incoming.TimestampMs < _latestTimestamp)
                return AddResult.Duplicate;
            if(SessionId != "-" && incoming.SessionId != SessionId)
            {
                LastCpuSnapshot = null;
                LatestCpuObservation = null;
                PreviousCpu = null;
                CurrentSelectionSnapshot = null;
                ClearXrefs();
                _seen.Clear();
                _latestSequence = _latestCpuSequence = _latestBrowseSequence = -1;
                DebugState = "unknown";
            }
            _latestTimestamp = Math.Max(_latestTimestamp, incoming.TimestampMs);
            if(!_seen.Add(incoming.DedupKey)) return AddResult.Duplicate;
        }
        else if(received.Origin == EventOrigin.Fallback && !_seen.Add(received.DedupKey))
            return AddResult.Duplicate;
        _seen.Add(received.DedupKey);
        LastEventAt = received.ReceivedAt;

        switch(received)
        {
        case SnapshotEvent snapshot:
            if(!AddSnapshot(snapshot)) return AddResult.Duplicate;
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
        CurrentExplicitXrefTarget = HexUtil.Zero;
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

    private bool AddSnapshot(SnapshotEvent snapshot)
    {
        // CPU, browsing and lifecycle events can arrive out of order because
        // callback captures and TCP connections complete independently.
        var newerState = snapshot.Sequence > _latestSequence;
        var newerBrowse = snapshot.Sequence > _latestBrowseSequence &&
            snapshot.Window is "disassembly" or "graph" && HexUtil.HasAddress(snapshot.SelectedAddress);
        var newerCpu = IsCpuObservation(snapshot) && snapshot.Sequence > _latestCpuSequence;
        // A newer paused selection at another RIP already proves that this CPU
        // observation belongs to an earlier pause. Do not rewind to it.
        if(LastCpuSnapshot is { EventType: "selection_changed" } recovered &&
           recovered.Sequence > snapshot.Sequence && recovered.Eip != snapshot.Eip)
            newerCpu = false;
        // A valid paused selection can bootstrap a receiver which missed the pause event.
        var recoverPause = newerState && snapshot.EventType == "selection_changed" && snapshot.DebugState == "paused" &&
            snapshot.RegistersValid && snapshot.SnapshotConsistent &&
            (LastCpuSnapshot is null || DebugState == "running" || LastCpuSnapshot.Eip != snapshot.Eip);
        if(snapshot.SessionId.Length > 0)
            SessionId = snapshot.SessionId;
        if(newerState)
        {
            _latestSequence = snapshot.Sequence;
            if(snapshot.DebugState.Length > 0) DebugState = snapshot.DebugState;
        }

        if(newerCpu || recoverPause)
            LatestCpuObservation = snapshot;
        if(newerCpu) _latestCpuSequence = snapshot.Sequence;

        if((newerCpu && IsCpuSnapshot(snapshot) && snapshot.SnapshotConsistent) || recoverPause)
        {
            PreviousCpu = LastCpuSnapshot;
            LastCpuSnapshot = snapshot;
        }

        if(newerBrowse)
        {
            _latestBrowseSequence = snapshot.Sequence;
            CurrentSelectionSnapshot = snapshot;
        }
        return newerState || newerBrowse || newerCpu || recoverPause;
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

    private static bool HasAddress(string? value) => HexUtil.HasAddress(value);

}
