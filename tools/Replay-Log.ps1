<#
.SYNOPSIS
    Replays a saved ACT network log through the addon's real chain and prints the table it would
    have published.

.DESCRIPTION
    Everything the addon does to a log line happens here: the same ParserHost hosting the same
    parser-ff.js, the same ParserOutput, the same downtime windows, the same MeterSnapshot. What is
    missing is ACT, so there is no encounter to match against and every fight the parser opens is
    taken as the one being reported (MeterPipeline.TakeEveryFight).

    This is how the figures get checked. Replay a pull, open the same pull's report on fflogs.com,
    and the rDPS column, the damage, the healing, the death counts and the downtime should line up.
    No unit test can do that: the parser is FFLogs' own and only their report says what it should
    have produced.

        .\tools\Replay-Log.ps1 <Network_*.log> [-From 2026-09-13T13:39] [-To 2026-09-13T13:58]
                               [-Every 30] [-Top 12]

    -From / -To are ISO prefixes in the log's own zone; a whole day is slow, so slice first.
    -Every prints a progress line every N seconds of log time; 0 turns progress off.

    Requires a Release build.  .\build.ps1 -SkipDeps; .\tools\Replay-Log.ps1 <log>
#>
param(
    [Parameter(Mandatory = $true, Position = 0)] [string] $Log,
    [string] $From = "",
    [string] $To = "9999",
    [int] $Every = 30,
    [int] $Top = 12
)
$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent $PSScriptRoot
$workspace = Split-Path -Parent $repo
$dir = Join-Path $workspace "out\Release\OverlayPluginAddon\net48"
if (-not (Test-Path (Join-Path $dir "OverlayPluginAddon.dll"))) {
    throw "Release build not found in $dir. Run .\build.ps1 -SkipDeps first."
}
if (-not (Test-Path $Log)) { throw "Log not found: $Log" }

# LoadFrom, not ReadAllBytes: ClearScript finds ClearScriptV8.win-x64.dll next to its own assembly.
Add-Type -Path (Join-Path $dir "ClearScript.Core.dll")
Add-Type -Path (Join-Path $dir "ClearScript.V8.dll")
[Microsoft.ClearScript.HostSettings]::AuxiliarySearchPath = $dir
$asm = [Reflection.Assembly]::LoadFrom((Join-Path $dir "OverlayPluginAddon.dll"))
$hostType = $asm.GetType("OverlayPluginAddon.Fflogs.ParserHost")
$pipelineType = $asm.GetType("OverlayPluginAddon.Fflogs.MeterPipeline")
$decisionType = $asm.GetType("OverlayPluginAddon.Fflogs.AppliedDecision")

# ---------------------------------------------------------------- the log
$lines = [System.IO.File]::ReadAllLines($Log) | Where-Object {
    $bar = $_.IndexOf('|')
    if ($bar -lt 1) { return $false }
    $stamp = $_.Substring($bar + 1, [Math]::Min(19, $_.Length - $bar - 1))
    $stamp -ge $From -and $stamp -lt $To
}
if ($lines.Count -eq 0) { throw "No lines in [$From, $To)." }

$firstMs = $hostType::TimestampOf($lines[0])
$lastMs = $hostType::TimestampOf($lines[$lines.Count - 1])
"{0:N0} lines, log time {1:HH:mm:ss} -> {2:HH:mm:ss} ({3:N0}s)" -f `
    $lines.Count,
    [DateTimeOffset]::FromUnixTimeMilliseconds($firstMs).LocalDateTime,
    [DateTimeOffset]::FromUnixTimeMilliseconds($lastMs).LocalDateTime,
    (($lastMs - $firstMs) / 1000)

# ---------------------------------------------------------------- the chain
$takeEveryFight = [System.Delegate]::CreateDelegate($decisionType, $pipelineType, "TakeEveryFight")

# Both callbacks are deliberately null. ParserHost and MeterPipeline call them from the parser's
# own thread, and a delegate made from a PowerShell script block has no runspace there - it takes
# the whole process down with it, with no error and no stack. What they would have reported is read
# back off LastError at the end instead.
$pipeline = [Activator]::CreateInstance($pipelineType, @($takeEveryFight, $null))
$parser = [Activator]::CreateInstance($hostType, @([string](Join-Path $repo "data\parser-ff.js"), $null))

$collected = $hostType.GetEvent("Collected")
$collected.AddEventHandler($parser, [System.Delegate]::CreateDelegate(
    $collected.EventHandlerType, $pipeline, $pipelineType.GetMethod("Accept")))

# The parser ignores lines older than this, so a replay starts its clock just before the first line.
if (-not $parser.Start([long]($firstMs - 1000), 1)) { throw "Parser failed to start: $($parser.LastError)" }

# ---------------------------------------------------------------- feed it
# The host drains on its own timer, so feeding runs ahead of parsing. Wait for the queue to empty
# and for a collect to have happened since, or the snapshot printed is the one from before.
function Wait-ForParser {
    $deadline = [DateTime]::UtcNow.AddSeconds(180)
    $before = $parser.Collections
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($parser.QueueLength -eq 0 -and $parser.Collections -gt $before + 1) { return }
        Start-Sleep -Milliseconds 50
    }
    Write-Host "  ! timed out waiting for the parser to catch up" -ForegroundColor Red
}

$nextReport = if ($Every -gt 0) { $firstMs } else { [long]::MaxValue }
foreach ($line in $lines) {
    $parser.Feed($line)
    $ms = $hostType::TimestampOf($line)
    if ($ms -lt $nextReport) { continue }
    $nextReport = $ms + $Every * 1000

    Wait-ForParser
    $snapshot = $pipeline.Current
    "{0:HH:mm:ss}  fight {1,3} {2,-11} windows {3,2}  damage {4,13:N0}  downtime {5,5:N0}s" -f `
        [DateTimeOffset]::FromUnixTimeMilliseconds($ms).LocalDateTime,
        $snapshot.FightId, $snapshot.FightState, $snapshot.Downtime.Count,
        $snapshot.EncounterDamage, $snapshot.Clocks.Downtime
}

Wait-ForParser
$snapshot = $pipeline.Current

# ---------------------------------------------------------------- the table
""
"lines parsed {0:N0}, line errors {1:N0}, collects {2:N0}" -f $parser.LinesParsed, $parser.LineErrors, $parser.Collections
if ($parser.LastError) { "last error: $($parser.LastError)" }
""
"fight {0}  state {1}  duration {2:N1}s  downtime {3:N1}s  damage clock {4:N1}s  ({5})" -f `
    $snapshot.FightId, $snapshot.FightState, $snapshot.Clocks.Seconds, $snapshot.Clocks.Downtime,
    $snapshot.Clocks.Active, $snapshot.Reason
""

$rows = @()
foreach ($name in $snapshot.Names) {
    $figures = $snapshot.Lookup($name)
    if ($null -eq $figures) { continue }
    $rows += [pscustomobject]@{ Name = $name; Figures = $figures }
}

if ($rows.Count -eq 0) {
    Write-Host "No rows. The parser opened no fight in this slice - check the range, or that the log is a network log." -ForegroundColor Yellow
}
else {
    "{0,-24} {1,13} {2,10} {3,10} {4,7} {5,12} {6,7}" -f "name", "damage", "rDPS", "aDPS", "rDPS%", "healed", "deaths"
    "-" * 92
    $rows | Sort-Object { - $_.Figures.Rdps } | Select-Object -First $Top | ForEach-Object {
        "{0,-24} {1,13:N0} {2,10:N1} {3,10:N1} {4,6:N1}% {5,12:N0} {6,7}" -f `
            $_.Name, $_.Figures.Damage, $_.Figures.Rdps, $_.Figures.Adps,
            $_.Figures.RdpsPct, $_.Figures.Healed, $_.Figures.Deaths
    }
    ""
    "Damage is each player without the pets FFLogs keeps as rows of their own; rDPS is the folded"
    "total. Compare against the same pull's report on fflogs.com."
}

$parser.Dispose()
