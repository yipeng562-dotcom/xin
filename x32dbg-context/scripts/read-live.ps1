param([string]$Path)
$ErrorActionPreference = 'Stop'
$targetArchitecture = 'x32'
if(-not $Path) { $Path = Join-Path $env:LOCALAPPDATA "CodexDbg\$targetArchitecture\live\latest.json" }
if(-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
    throw "No live export at $Path. Start the new $targetArchitecture receiver; the original receiver does not export this file."
}
$stream = [IO.FileStream]::new($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read,
    ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
$reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8)
try { $document = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
if($document.export_schema_version -ne 1 -or $document.target_architecture -ne $targetArchitecture) {
    throw 'Unexpected export schema or target architecture.'
}
$heartbeatAge = ([DateTimeOffset]::UtcNow - [DateTimeOffset]$document.exported_at_utc).TotalSeconds
$receiverProcess = Get-Process -Id $document.receiver.process_id -ErrorAction SilentlyContinue
$sameProcess = $false
if($receiverProcess) {
    try { $sameProcess = $receiverProcess.Path -eq $document.receiver.executable -and
        $receiverProcess.StartTime.ToUniversalTime() -le ([DateTimeOffset]$document.receiver.started_at_utc).UtcDateTime }
    catch { $sameProcess = $false }
}
$warnings = [Collections.Generic.List[string]]::new()
$status = 'available'
if(-not $document.receiver.running -or -not $sameProcess) {
    $status = 'offline'; $warnings.Add('Receiver is stopped or its process identity cannot be verified; this is saved data.')
} elseif($heartbeatAge -gt 10 -or $heartbeatAge -lt -5) {
    $status = 'stale'; $warnings.Add('Export heartbeat is stale or the system clock changed; do not claim live data.')
} elseif(-not $document.receiver.listening) {
    $status = 'not_listening'; $warnings.Add('Receiver is running but is not listening on its event port.')
} elseif(-not $document.state.last_accepted_event) { $status = 'waiting_for_event' }
if($document.state.cpu -and $document.state.cpu.session_id -ne $document.state.session_id) {
    $warnings.Add('CPU belongs to a different session; do not combine it with current browsing or state.')
}
if($document.state.cpu -and $document.state.debug_state -ne 'paused') {
    $warnings.Add('CPU data is a retained pause snapshot, not current running registers.')
}
if($document.state.cpu -and (-not $document.state.cpu.registers_valid -or -not $document.state.cpu.snapshot_consistent)) {
    $warnings.Add('CPU validity or consistency check failed.')
}
if($document.state.xrefs) { $warnings.Add('XREF has no guaranteed session identity; check target and received_at before associating it.') }
$warnings.Add('Heartbeat time is not target event time. Check each snapshot timestamp_ms, received_at, session_id and origin; Fallback may be historical.')
[pscustomobject]@{
    read_status = $status
    heartbeat_age_seconds = [Math]::Round($heartbeatAge, 2)
    warnings = $warnings.ToArray()
    export = $document
} | ConvertTo-Json -Depth 80
