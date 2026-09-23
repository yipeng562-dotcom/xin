param([string]$BinaryRoot = (Join-Path (Split-Path $PSScriptRoot -Parent) 'dist'))
$ErrorActionPreference = 'Stop'
if(-not ('ReceiverSmokeWindow' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class ReceiverSmokeWindow {
    delegate bool Callback(IntPtr window, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumWindows(Callback callback, IntPtr data);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window, uint message, IntPtr w, IntPtr l);
    public static bool Close(int processId) {
        bool sent = false;
        EnumWindows((window, _) => {
            GetWindowThreadProcessId(window, out uint owner);
            if(owner == processId) sent |= PostMessage(window, 0x10, IntPtr.Zero, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
        return sent;
    }
}
'@
}
$repoRoot = Split-Path $PSScriptRoot -Parent
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('CodexDbgSmoke-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
foreach($entry in @(@('SnapshotAssistant','x32'), @('SnapshotAssistant64','x64'))) {
    $name, $architecture = $entry
    $testDirectory = Join-Path $testRoot $architecture
    New-Item -ItemType Directory -Path $testDirectory | Out-Null
    $probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $probe.Start(); $testPort = $probe.LocalEndpoint.Port; $probe.Stop()
    $exe = Join-Path $BinaryRoot "$name\$name.exe"
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $exe
    $start.Arguments = "--port $testPort --export-dir `"$testDirectory`""
    $start.UseShellExecute = $false
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.EnvironmentVariables['TEMP'] = $testDirectory
    $start.EnvironmentVariables['TMP'] = $testDirectory
    $child = [Diagnostics.Process]::Start($start)
    $exportPath = Join-Path $testDirectory 'latest.json'
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(10)
        while(-not (Test-Path -LiteralPath $exportPath) -and [DateTime]::UtcNow -lt $deadline -and -not $child.HasExited) {
            Start-Sleep -Milliseconds 150
        }
        if(-not (Test-Path -LiteralPath $exportPath)) { throw "$architecture receiver did not publish startup state" }
        $readScript = Join-Path $repoRoot "$($architecture)dbg-context\scripts\read-live.ps1"
        $initial = (& $readScript -Path $exportPath) | ConvertFrom-Json
        if($initial.read_status -ne 'waiting_for_event') { throw "$architecture unexpected startup state: $($initial.read_status)" }
        $ip = if($architecture -eq 'x32') { '00530170' } else { '00007FF612345670' }
        $fixture = @{
            schema_version=2; source='isolated-smoke-test'; session_id='SMOKE-TEST'; sequence=1
            timestamp_ms=[DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds(); event_type='debug_paused'
            window='disassembly'; debug_state='paused'; registers_valid=$true; snapshot_consistent=$true
            eip=$ip; rip=$ip; selected_address=$ip; registers=@{}; stack=@()
        } | ConvertTo-Json -Compress
        $client = [Net.Sockets.TcpClient]::new()
        try {
            $client.Connect('127.0.0.1', $testPort)
            $bytes = [Text.Encoding]::UTF8.GetBytes($fixture)
            $client.GetStream().Write($bytes, 0, $bytes.Length)
        } finally { $client.Dispose() }
        $deadline = [DateTime]::UtcNow.AddSeconds(6)
        do {
            Start-Sleep -Milliseconds 200
            $result = (& $readScript -Path $exportPath) | ConvertFrom-Json
        } while(-not $result.export.state.cpu -and [DateTime]::UtcNow -lt $deadline)
        if($result.read_status -ne 'available' -or $result.export.state.cpu.eip -ne $ip) {
            throw "$architecture executable -> TCP -> export -> skill read failed"
        }
        $stalePath = Join-Path $testDirectory 'stale.json'
        $result.export.exported_at_utc = [DateTimeOffset]::UtcNow.AddSeconds(-30).ToString('O')
        $result.export | ConvertTo-Json -Depth 80 | Set-Content -LiteralPath $stalePath -Encoding utf8
        $stale = (& $readScript -Path $stalePath) | ConvertFrom-Json
        if($stale.read_status -ne 'stale') { throw 'Stale heartbeat was not detected' }
        if(-not [ReceiverSmokeWindow]::Close($child.Id)) { throw 'Could not request test receiver shutdown' }
        if(-not $child.WaitForExit(5000)) { throw 'Test receiver shutdown timed out' }
        $closed = (& $readScript -Path $exportPath) | ConvertFrom-Json
        if($closed.read_status -ne 'offline' -or $closed.export.receiver.running) { throw 'Shutdown state was not exported' }
        Write-Output "PASS $architecture published EXE -> isolated TCP -> latest.json -> skill reader; stale detection; clean shutdown"
    } finally {
        if(-not $child.HasExited) { $child.Kill(); $child.WaitForExit(5000) | Out-Null }
        $child.Dispose()
    }
}
Write-Output "Smoke test artifacts: $testRoot"
