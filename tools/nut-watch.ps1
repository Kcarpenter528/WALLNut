<#
  nut-watch.ps1 - Keeps one connection open to a NUT server and polls the UPS,
  logging every result and exactly how/when the connection fails.

  Usage:
    .\nut-watch.ps1                         # defaults below, runs until Ctrl+C
    .\nut-watch.ps1 -IntervalSec 2 -Minutes 1
#>
param(
    [string]$NasHost     = '192.168.1.10',
    [int]   $Port        = 3493,
    [string]$UpsName     = '',      # blank = ask the server (LIST UPS) and use what it reports
    [int]   $IntervalSec = 5,       # time between status checks
    [int]   $TimeoutSec  = 10,      # how long to wait for a reply before calling it a timeout
    [int]   $RetrySec    = 10,      # wait before reconnecting after a failure
    [double]$Minutes     = 0,       # 0 = run until Ctrl+C
    [string]$LogFile     = (Join-Path $PSScriptRoot 'nut-watch.log')
)

function Log([string]$msg) {
    $line = '{0}  {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $msg
    Write-Host $line
    Add-Content -Path $LogFile -Value $line
}

# Sends one command and returns the reply lines (stops at END LIST for list commands).
function Send-Nut($conn, [string]$cmd) {
    $conn.Writer.WriteLine($cmd)
    $lines = @()
    while ($true) {
        $line = $conn.Reader.ReadLine()
        if ($null -eq $line) { throw [System.IO.EndOfStreamException]::new('Server closed the connection (clean close / FIN)') }
        $lines += $line
        if ($cmd -notlike 'LIST *' -or $line -like 'END LIST*' -or $line -like 'ERR *') { break }
    }
    return $lines
}

function Open-Nut {
    $client = [System.Net.Sockets.TcpClient]::new()
    $client.ReceiveTimeout = $TimeoutSec * 1000
    $client.SendTimeout    = $TimeoutSec * 1000
    if (-not $client.ConnectAsync($NasHost, $Port).Wait($TimeoutSec * 1000)) {
        $client.Dispose(); throw [System.TimeoutException]::new('Connect timed out')
    }
    $stream = $client.GetStream()
    $writer = [System.IO.StreamWriter]::new($stream, [System.Text.Encoding]::ASCII)
    $writer.NewLine = "`n"; $writer.AutoFlush = $true
    [pscustomobject]@{
        Client = $client
        Reader = [System.IO.StreamReader]::new($stream, [System.Text.Encoding]::ASCII)
        Writer = $writer
    }
}

function Describe-Error($err) {
    $e = $err.Exception
    while ($e.InnerException) { $e = $e.InnerException }
    $sock = $e -as [System.Net.Sockets.SocketException]
    if ($sock) {
        switch ($sock.SocketErrorCode) {
            'ConnectionReset'   { return 'Connection RESET by the NAS (or something in between)' }
            'TimedOut'          { return "No reply within ${TimeoutSec}s (timeout)" }
            'ConnectionRefused' { return 'Connection REFUSED - NUT server not listening / blocked' }
            'HostUnreachable'   { return 'Host unreachable - network problem on this PC or LAN' }
            'NetworkUnreachable'{ return 'Network unreachable - this PC has no network' }
            default             { return "Socket error $($sock.SocketErrorCode): $($sock.Message)" }
        }
    }
    if ($e -is [System.IO.IOException] -and $e.Message -match 'period of time|timed out') { return "No reply within ${TimeoutSec}s (timeout)" }
    return "$($e.GetType().Name): $($e.Message)"
}

$stopAt = if ($Minutes -gt 0) { (Get-Date).AddMinutes($Minutes) } else { [datetime]::MaxValue }
Log "=== Watching NUT server ${NasHost}:${Port} every ${IntervalSec}s (log: $LogFile) ==="

$conn = $null; $connectedAt = $null; $polls = 0; $lastStatus = $null
$sinceLastOk = [System.Diagnostics.Stopwatch]::new()

while ((Get-Date) -lt $stopAt) {
    try {
        if (-not $conn) {
            $conn = Open-Nut
            $connectedAt = Get-Date; $polls = 0; $lastStatus = $null
            $list = Send-Nut $conn 'LIST UPS'
            $names = $list | Where-Object { $_ -like 'UPS *' } | ForEach-Object { ($_ -split ' ')[1] }
            if (-not $UpsName) { $UpsName = $names | Select-Object -First 1 }
            Log ("CONNECTED.  Server reports UPS name(s): {0}.  Polling '{1}'." -f ($names -join ', '), $UpsName)
        }

        $sinceLastOk.Restart()
        $status = @(Send-Nut $conn "GET VAR $UpsName ups.status")[0]
        $charge = @(Send-Nut $conn "GET VAR $UpsName battery.charge")[0]
        $ms = $sinceLastOk.ElapsedMilliseconds
        $polls++

        if ($status -like 'ERR *') {
            Log "Server answered with an error: $status  (wrong UPS name? try -UpsName ups)"
        } else {
            $s = ($status -replace '^VAR \S+ \S+ ', '').Trim('"')
            $c = ($charge -replace '^VAR \S+ \S+ ', '').Trim('"')
            # Log every change, plus a heartbeat once a minute so the log shows it was alive.
            if ($s -ne $lastStatus -or ($polls % [math]::Max(1, [int](60 / $IntervalSec))) -eq 0) {
                $up = (Get-Date) - $connectedAt
                Log ("ok  status={0}  charge={1}%  reply={2}ms  connected for {3:hh\:mm\:ss}" -f $s, $c, $ms, $up)
            }
            if ($lastStatus -and $s -ne $lastStatus) { Log "*** UPS STATUS CHANGED: $lastStatus -> $s ***" }
            $lastStatus = $s
        }
    }
    catch {
        $why = Describe-Error $_
        if ($conn) {
            $up = (Get-Date) - $connectedAt
            Log ("DISCONNECTED after {0:hh\:mm\:ss} ({1} good polls). Reason: {2}" -f $up, $polls, $why)
            try { $conn.Client.Dispose() } catch {}
            $conn = $null
        } else {
            Log "Reconnect failed: $why"
        }
        Start-Sleep -Seconds $RetrySec
        continue
    }
    Start-Sleep -Seconds $IntervalSec
}

if ($conn) { try { $conn.Writer.WriteLine('LOGOUT') } catch {}; $conn.Client.Dispose() }
Log '=== Stopped ==='
