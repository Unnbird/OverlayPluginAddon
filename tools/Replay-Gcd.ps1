<#
.SYNOPSIS
    Replays a saved ACT network log through the addon's GCD half and prints what the GCD columns
    would have shown, downtime windows included.

.DESCRIPTION
    Replay-Log.ps1 covers the FFLogs half. This is its twin for the GCD columns: the same
    StatusTracker, ActionCategories, ActionData and GcdTracker the addon runs, fed the same log
    lines the same way the event source feeds them (cast bars paired with their effects, haste
    read at the press, players told from mobs by their actor id, the record starting over when
    the parser opens a new fight), and then handed the pull's downtime windows exactly as the
    addon hands them over - read out of the FFLogs parser running on the same lines.

    That last step is the point. The two halves keep time in different clocks unless somebody
    makes sure they do not, and a window that never meets a gap is indistinguishable from no
    window at all until a real pull with a real transition is replayed and the transition is
    still charged as lost time. So the trace printed here carries a "down" column: gap 47.67,
    down 0.00, lost 45.21 is that bug; gap 47.67, down 45.30, lost 0.00 is the pull as played.

        .\tools\Replay-Gcd.ps1 <Network_*.log> [-From 2026-09-22T23:24] [-To 2026-09-22T23:41]
                               [-Player Bird] [-GapOver 5]

    Slice to one pull, and let the slice begin early enough to hold two things: the 01 zone line
    of the instance (without it the parser has no zone handler and there are no windows), and the
    03 AddCombatant lines of the boss (they map its actor to the NPC the handler watches; after a
    wipe they come with the arena reset, seconds before the next pull). Earlier pulls in the slice
    are fine - the record starts over when the parser opens the next fight, as it does live.
    -Player prints one player's long gaps (longer than -GapOver seconds) with the down column.

    Requires a Release build.  .\build.ps1 -SkipDeps; .\tools\Replay-Gcd.ps1 <log> -From .. -To ..
#>
param(
    [Parameter(Mandatory = $true, Position = 0)] [string] $Log,
    [string] $From = "",
    [string] $To = "9999",
    [string] $Player = "",
    [double] $GapOver = 5
)
$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent $PSScriptRoot
$workspace = Split-Path -Parent $repo
$dir = Join-Path $workspace "out\Release\OverlayPluginAddon\net48"
if (-not (Test-Path (Join-Path $dir "OverlayPluginAddon.dll"))) {
    throw "Release build not found in $dir. Run .\build.ps1 -SkipDeps first."
}
if (-not (Test-Path $Log)) { throw "Log not found: $Log" }

Add-Type -Path (Join-Path $dir "ClearScript.Core.dll")
Add-Type -Path (Join-Path $dir "ClearScript.V8.dll")
[Microsoft.ClearScript.HostSettings]::AuxiliarySearchPath = $dir
$asm = [Reflection.Assembly]::LoadFrom((Join-Path $dir "OverlayPluginAddon.dll"))

# The action category table lives in FFXIV_ACT_Plugin.Resource, which ActionCategories.Load()
# expects to find already loaded - the parser plugin does that in ACT; here we do it ourselves.
$resourceDll = Join-Path $workspace "OverlayPlugin\Thirdparty\FFXIV_ACT_Plugin\SDK\FFXIV_ACT_Plugin.Resource.dll"
if (-not (Test-Path $resourceDll)) {
    $resourceDll = Join-Path $workspace "FF14ACT\FFXIV_ACT_Plugin\bin\Release\FFXIV_ACT_Plugin.Resource.dll"
}
if (-not (Test-Path $resourceDll)) { throw "FFXIV_ACT_Plugin.Resource.dll not found beside the workspace." }
[void][Reflection.Assembly]::Load([IO.File]::ReadAllBytes($resourceDll))

# The GCD classes sit in the root namespace (the Gcd folder is only a folder); the FFLogs ones in
# OverlayPluginAddon.Fflogs.
$T = { param($n) $asm.GetType("OverlayPluginAddon.$n") }
$categories = (& $T "ActionCategories")::Load()
if (-not $categories.Available) { throw "Action categories did not load: $($categories.LoadError)" }
$actions = (& $T "ActionData")::Load([string](Join-Path $repo "data\actions.json"))
$statuses = [Activator]::CreateInstance((& $T "StatusTracker"))
$gcds = [Activator]::CreateInstance((& $T "GcdTracker"), @($actions))

# ---------------------------------------------------------------- the log
$lines = [System.IO.File]::ReadAllLines($Log) | Where-Object {
    $bar = $_.IndexOf('|')
    if ($bar -lt 1) { return $false }
    $stamp = $_.Substring($bar + 1, [Math]::Min(19, $_.Length - $bar - 1))
    $stamp -ge $From -and $stamp -lt $To
}
if ($lines.Count -eq 0) { throw "No lines in [$From, $To)." }
"{0:N0} lines" -f $lines.Count

# ---------------------------------------------------------------- the FFLogs half, for its fights and windows
$hostType = & $T "Fflogs.ParserHost"
$pipelineType = & $T "Fflogs.MeterPipeline"
$decisionType = & $T "Fflogs.AppliedDecision"
$takeEveryFight = [System.Delegate]::CreateDelegate($decisionType, $pipelineType, "TakeEveryFight")
$pipeline = [Activator]::CreateInstance($pipelineType, @($takeEveryFight, $null))
$parser = [Activator]::CreateInstance($hostType, @([string](Join-Path $repo "data\parser-ff.js"), $null, [string]$dir))
$collected = $hostType.GetEvent("Collected")
$collected.AddEventHandler($parser, [System.Delegate]::CreateDelegate(
    $collected.EventHandlerType, $pipeline, $pipelineType.GetMethod("Accept")))
$firstMs = $hostType::TimestampOf($lines[0])
if (-not $parser.Start([long]($firstMs - 1000), 1)) { throw "Parser failed to start: $($parser.LastError)" }

# The host drains on its own timer, so feeding runs ahead of parsing. Wait for the queue to empty
# and for a collect to have happened since, so the fight id read afterwards is current.
function Wait-Collect {
    $before = $parser.Collections
    $deadline = [DateTime]::UtcNow.AddSeconds(120)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($parser.QueueLength -eq 0 -and $parser.Collections -gt $before + 1) { return }
        Start-Sleep -Milliseconds 50
    }
    Write-Host "  ! timed out waiting for the parser" -ForegroundColor Red
}

# ---------------------------------------------------------------- the GCD half, as the event source feeds it
$pending = @{}          # source name -> @{ ActionId; StartedAt; CastMs }
$maxCastAge = [TimeSpan]::FromSeconds(15)
$script:recorded = 0
$script:hard = 0
$culture = [Globalization.CultureInfo]::InvariantCulture
$hexStyle = [Globalization.NumberStyles]::HexNumber

function SpeedModifierOn($actor, [uint32] $actionId) {
    $m = 1.0
    foreach ($s in $statuses.ActiveOn($actor)) { $m *= $actions.SpeedModifierOf($s.StatusId, $actionId) }
    return $m
}

function Handle-Line([string] $line) {
    $bar = $line.IndexOf('|')
    if ($bar -lt 1 -or $bar -gt 3) { return }
    $type = 0
    if (-not [int]::TryParse($line.Substring(0, $bar), [ref]$type)) { return }
    switch ($type) {
        1  { $statuses.Clear() }
        2  { $statuses.HandlePrimaryPlayer($line.Split('|')) }
        3  { $statuses.HandleAddCombatant($line.Split('|')) }
        4  { $f = $line.Split('|'); if ($f.Length -gt 3) { $statuses.RemoveActor($f[3]) } }
        25 { $f = $line.Split('|'); if ($f.Length -gt 3) { $statuses.RemoveActor($f[3]) } }
        26 { [void]$statuses.HandleStatusLine($true, $line.Split('|')) }
        30 { [void]$statuses.HandleStatusLine($false, $line.Split('|')) }
        20 {
            $f = $line.Split('|')
            if ($f.Length -le 4) { break }
            $actionId = [uint32]0
            if (-not [uint32]::TryParse($f[4], $hexStyle, $culture, [ref]$actionId)) { break }
            $source = $f[3]
            if ([string]::IsNullOrEmpty($source)) { break }
            $when = [DateTimeOffset]::Parse($f[1], $culture).LocalDateTime
            $castMs = 0.0
            $castSeconds = 0.0
            if ($f.Length -gt 8 -and [double]::TryParse($f[8], [Globalization.NumberStyles]::Float, $culture, [ref]$castSeconds) -and $castSeconds -gt 0) {
                $castMs = $castSeconds * 1000.0
            }
            $pending[$source] = @{ ActionId = $actionId; StartedAt = $when; CastMs = $castMs }
            if (($actions.IsOnGcd($actionId) -or $categories.IsGcd($actionId)) -and $statuses.IsPlayer($source)) {
                $gcds.Record($source, $actionId, $when, (SpeedModifierOn $source $actionId), $true, $categories.IsSpell($actionId), $castMs)
                $script:recorded++; $script:hard++
            }
        }
        23 {
            $f = $line.Split('|')
            if ($f.Length -le 4) { break }
            $source = $f[3]
            if ([string]::IsNullOrEmpty($source) -or -not $pending.ContainsKey($source)) { break }
            $p = $pending[$source]
            $pending.Remove($source)
            $gcds.RemoveLast($source, [uint32]$p.ActionId)
        }
        { $_ -eq 21 -or $_ -eq 22 } {
            $f = $line.Split('|')
            if ($f.Length -lt 6) { break }
            $source = $f[3]
            $statuses.NotePlayer($f[2], $source)
            if ([string]::IsNullOrEmpty($source) -or [string]::IsNullOrEmpty($f[2]) -or $f[2][0] -ne '1') { break }
            $actionId = [uint32]0
            if (-not [uint32]::TryParse($f[4], $hexStyle, $culture, [ref]$actionId)) { break }
            # xivanalysis' onGcd list first, the category table second - the same rule as
            # EventSource.IsGcdAction. The category alone files a Ninja's mudras and Ninjutsu as
            # abilities and reads every Raiton as a 2.5s hole.
            if (-not ($actions.IsOnGcd($actionId) -or $categories.IsGcd($actionId))) { break }
            $when = [DateTimeOffset]::Parse($f[1], $culture).LocalDateTime
            $isHard = $false
            $castMs = 0.0
            if ($pending.ContainsKey($source)) {
                $p = $pending[$source]
                if ([uint32]$p.ActionId -eq $actionId -and ($when - $p.StartedAt) -le $maxCastAge) {
                    $isHard = $true
                    $when = $p.StartedAt
                    $castMs = $p.CastMs
                    $pending.Remove($source)
                }
            }
            $gcds.Record($source, $actionId, $when, (SpeedModifierOn $source $actionId), $isHard, $categories.IsSpell($actionId), $castMs)
            $script:recorded++
            if ($isHard) { $script:hard++ }
        }
    }
}

# ---------------------------------------------------------------- one pass, both halves
# Every so many lines the parser is caught up and asked which fight it is on. A new fight is a new
# pull and the GCD record starts over - PullBoundary in the addon, which reads the same answer off
# the same collects, a collect or two late in exactly the same way.
$syncEvery = 400
$fightId = [long]0
$resets = 0
for ($i = 0; $i -lt $lines.Count; $i++) {
    $parser.Feed($lines[$i])
    Handle-Line $lines[$i]
    if ($i % $syncEvery -ne 0 -and $i -ne $lines.Count - 1) { continue }
    Wait-Collect
    $current = [long]$pipeline.Current.FightId
    if ($current -eq 0 -or $current -eq $fightId) { continue }
    # The first fight too: PullBoundary.NoteFight returns true for it as well, and the presses of
    # a false start before it are exactly what that first reset throws away live.
    $gcds.Clear()
    $pending.Clear()
    $script:recorded = 0
    $script:hard = 0
    $resets++
    "  fight {0} opened by line {1:N0} ({2}): GCD record starts over" -f $current, $i, $lines[$i].Split('|')[1].Substring(11, 8)
    $fightId = $current
}

$snapshot = $pipeline.Current
$windows = $snapshot.Downtime
"parser: fight {0} {1}, {2:N1}s, downtime {3:N1}s in {4} window(s)" -f `
    $snapshot.FightId, $snapshot.FightState, $snapshot.Clocks.Seconds, $snapshot.Clocks.Downtime, $windows.Count
foreach ($w in $windows) {
    "   {0:HH:mm:ss.fff} -> {1:HH:mm:ss.fff}  {2,6:N1}s" -f `
        [DateTimeOffset]::FromUnixTimeMilliseconds([long]$w.Start).LocalDateTime,
        [DateTimeOffset]::FromUnixTimeMilliseconds([long]$w.End).LocalDateTime, $w.Seconds
}
"gcd: {0:N0} presses recorded in the last pull, {1:N0} of them hard casts, {2} players, {3} reset(s)" -f `
    $script:recorded, $script:hard, @($gcds.Players).Count, $resets

# ---------------------------------------------------------------- before and after
function Summary($label) {
    ""
    $label
    "{0,-24} {1,6} {2,9} {3,8} {4,9} {5,8}" -f "name", "casts", "uptime%", "lost s", "active s", "recast"
    "-" * 71
    foreach ($name in ($gcds.Players | Sort-Object)) {
        $st = $gcds.StatsFor($name)
        if ($st.Count -eq 0) { continue }
        "{0,-24} {1,6} {2,9:N1} {3,8:N1} {4,9:N1} {5,8:N2}" -f $name, $st.Count, ($st.Uptime * 100), $st.Clip, $st.ActiveSeconds, $st.Recast
    }
}

Summary "-- without the windows: every gap charged --"
[void]$gcds.SetDowntimeWindows($windows)
# And the fight's end, once the parser has closed it. The record is not reset until the next fight
# opens, so without this a Monk meditating for chakra after the wipe ends the pull with a 20s gap.
$endNote = ""
if ($snapshot.FightEnded) {
    [void]$gcds.SetFightEnd([double]$snapshot.FightEndMs)
    $endNote = ", measured up to the {0} at {1:HH:mm:ss.fff}" -f $snapshot.FightState,
        [DateTimeOffset]::FromUnixTimeMilliseconds([long]$snapshot.FightEndMs).LocalDateTime
}
Summary "-- with the parser's $($windows.Count) window(s)$endNote, as the addon reads them --"

if ($Player) {
    ""
    "-- $Player, gaps longer than ${GapOver}s --"
    "{0,8} {1,7} {2,5} {3,7} {4,7} {5,7} {6,7}" -f "t(s)", "action", "cast", "gap", "down", "occup", "lost"
    $st = $gcds.StatsFor($Player, $true)
    if ($st.AfterEnd -gt 0) { "  ({0} press(es) after the fight ended, not counted)" -f $st.AfterEnd }
    if ($null -eq $st.Trace -or $st.Trace.Count -eq 0) { "  (no presses for $Player - check the spelling against the names above)" }
    else {
        $first = $st.Trace[0].TimeMs
        foreach ($c in $st.Trace) {
            if ($c.GapMs / 1000.0 -le $GapOver) { continue }
            "{0,8:N2} {1,7:X} {2,5} {3,7:N2} {4,7:N2} {5,7:N2} {6,7:N2}" -f `
                (($c.TimeMs - $first) / 1000.0), $c.ActionId, $(if ($c.HardCast) { "hard" } else { "inst" }),
                ($c.GapMs / 1000.0), ($c.DownMs / 1000.0), ($c.OccupiedMs / 1000.0), ($c.LostMs / 1000.0)
        }
    }
}

$parser.Dispose()
