namespace SnapshotAssistant;

public enum ProcessResult
{
    Added,
    Duplicate,
    Invalid
}

public sealed class EventCoordinator
{
    private readonly object _gate = new();
    private ReceivedEvent? _lastAcceptedEvent;
    public SnapshotStore Store { get; }
    public AppLogger Logger { get; }
    public event Action<ReceivedEvent>? EventAccepted;

    public EventCoordinator(SnapshotStore store, AppLogger logger)
    {
        Store = store;
        Logger = logger;
    }

    public ProcessResult ProcessRaw(string raw, EventOrigin origin)
    {
        ReceivedEvent? received;
        string error;
        lock(_gate)
        {
            if(!EventJsonParser.TryParse(raw, origin, out received, out error) || received is null)
            {
                Logger.Warning($"JSON解析失败：{Short(error)}");
                return ProcessResult.Invalid;
            }

            if(Store.Add(received) == AddResult.Duplicate)
                return ProcessResult.Duplicate;

            _lastAcceptedEvent = received;
            switch(received)
            {
            case SnapshotEvent snapshot when !snapshot.SnapshotConsistent && snapshot.RegistersValid:
                Logger.Warning($"Snapshot一致性异常：#{snapshot.Sequence} {snapshot.EventType}");
                break;
            case SnapshotEvent snapshot when Store.IsCpuSnapshot(snapshot):
                Logger.Info($"收到CPU Snapshot：#{snapshot.Sequence} {snapshot.EventType} RIP={snapshot.Eip}");
                break;
            case SnapshotEvent snapshot:
                Logger.Info($"收到状态：#{snapshot.Sequence} {snapshot.EventType} CPU={snapshot.DebugState} 浏览={snapshot.SelectedAddress} registers_valid={snapshot.RegistersValid} snapshot_consistent={snapshot.SnapshotConsistent}");
                break;
            case XrefsResultEvent xrefs:
                Logger.Info($"收到XREF：{xrefs.TargetAddress}，{xrefs.Count}条");
                break;
            }
        }
        EventAccepted?.Invoke(received);
        return ProcessResult.Added;
    }

    public object CaptureLiveState()
    {
        lock(_gate)
            return new
            {
                Store.SessionId, Store.DebugState, Store.LastEventAt,
                LastAcceptedEvent = (object?)_lastAcceptedEvent,
                Cpu = Store.LastCpuSnapshot,
                CpuObservation = Store.LatestCpuObservation,
                Browse = Store.CurrentSelectionSnapshot,
                Xrefs = Store.LatestXrefs,
                XrefContext = Store.LatestXrefContext,
                Store.XrefCopyPending,
                Store.CurrentExplicitXrefTarget
            };
    }

    private static string Short(string value) => value.Length <= 200 ? value : value[..200];
}
