<#
.SYNOPSIS
    Regression test for DowntimeWindows, which reads the untargetable stretches of a fight off
    FFLogs' zone handlers.

.DESCRIPTION
    fight.downtime is only the total, and the total is all the damage clock needs. The GCD columns
    need the windows themselves, and the handlers - hand-written per instance inside parser-ff.js -
    keep them in three different shapes. All of them have to be read, or the GCD columns disagree
    with the DPS columns about the same pull. Getting one wrong is worse than getting none: a
    window read as never closing swallows every real clip after it.

    Each shape is built as a real script object in a real V8 engine and read through the real
    DowntimeWindows.Read, so this covers the interop as well as the rules. The totals are checked
    against a reimplementation of the parser's own downtimeForRange - the arithmetic behind
    fight.downtime, which is what the DPS clock is built from. The two must agree.

    The same scenarios run against the JavaScript this was ported from, in
    mopimopi/tests/test-fflogs-downtime.js.

    Requires a Release build.  .\build.ps1 -SkipDeps; .\tools\Test-Downtime.ps1
#>
$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent $PSScriptRoot
$workspace = Split-Path -Parent $repo
$dir = Join-Path $workspace "out\Release\OverlayPluginAddon\net48"
if (-not (Test-Path (Join-Path $dir "OverlayPluginAddon.dll"))) {
    throw "Release build not found in $dir. Run .\build.ps1 -SkipDeps first."
}

# LoadFrom, not ReadAllBytes: ClearScript finds ClearScriptV8.win-x64.dll next to its own
# assembly, and an assembly loaded from a byte array has no location to look beside.
Add-Type -Path (Join-Path $dir "ClearScript.Core.dll")
Add-Type -Path (Join-Path $dir "ClearScript.V8.dll")
[Microsoft.ClearScript.HostSettings]::AuxiliarySearchPath = $dir
$asm = [Reflection.Assembly]::LoadFrom((Join-Path $dir "OverlayPluginAddon.dll"))
$downtimeType = $asm.GetType("OverlayPluginAddon.Fflogs.DowntimeWindows")

$failures = 0
function Check($label, $actual, $expected) {
    $ok = "$actual" -eq "$expected"
    if (-not $ok) { $script:failures++ }
    "{0} {1,-52} actual={2,-28} expected={3}" -f $(if ($ok) { "PASS" } else { "FAIL" }), $label, $actual, $expected
}
function Section($title) { ""; "=============== $title ===============" }

$engine = New-Object Microsoft.ClearScript.V8.V8ScriptEngine

# A 600s fight, still running, whose real downtime is 60s..120s. The handler is handed in as a
# script object exactly as parser-ff.js exposes it.
$FIGHT_START = 0
$LAST_LINE = 600000
function At { param([double] $seconds) return $seconds * 1000 }

function WindowsOf {
    param([string] $handlerLiteral)
    $handler = $engine.Evaluate("($handlerLiteral)")
    $windows = $downtimeType::Read($handler, [double]$LAST_LINE, $null)
    # The comma keeps PowerShell from unrolling a one-window list into a bare struct, which then
    # will not bind to the IReadOnlyList parameter of Between().
    return ,($downtimeType::Merge($windows))
}

function Describe {
    param($windows)
    if ($windows.Count -eq 0) { return "[]" }
    return ($windows | ForEach-Object { "{0}-{1}" -f ($_.Start / 1000), ($_.End / 1000) }) -join ","
}

function TotalSeconds {
    param($windows)
    return [Math]::Round($downtimeType::Between($windows, [double]$FIGHT_START, [double]$LAST_LINE) / 1000, 3)
}

<#
    parser-ff.js's downtimeForRange, which every handler's totalDowntimeForFightRange is built
    from: a start of 0 means the window never opened, an end of 0 means it has not closed and runs
    to the end of the range. This is the downtime the DPS clock takes off the fight.
#>
function ParserDowntime {
    param([double] $start, [double] $end)
    if ($start -eq 0) { return 0 }
    $from = [Math]::Max($start, $FIGHT_START)
    $to = [Math]::Min($(if ($end -eq 0) { $LAST_LINE } else { $end }), $LAST_LINE)
    return [Math]::Max(0, $to - $from) / 1000
}

""
"########## FFLogs downtime windows ##########"

Section "A: downtimeTracker (M8S, Necron, most of 7.2/7.3)"
Check "committed intervals" (Describe (WindowsOf '{ downtimeTracker: { committedIntervals: [{ start: 60000, end: 120000 }], pendingInterval: null } }')) "60-120"
Check "plus the open one, to the latest line" (Describe (WindowsOf '{ downtimeTracker: { committedIntervals: [{ start: 60000, end: 120000 }], pendingInterval: { start: 500000, end: 500000 } } }')) "60-120,500-600"

Section "B: downtimePeriods + downtimeStart (FRU)"
# FRU pushes each window into the list as it closes and zeroes downtimeStart, so the pair only
# ever describes the window still open. It has no downtimeEnd field at all.
Check "closed windows only" (Describe (WindowsOf '{ downtimePeriods: [{ start: 60000, end: 120000 }, { start: 300000, end: 330000 }], downtimeStart: 0 }')) "60-120,300-330"
Check "a window still open runs to the latest line" (Describe (WindowsOf '{ downtimePeriods: [{ start: 60000, end: 120000 }], downtimeStart: 500000 }')) "60-120,500-600"

Section "C: downtimeStart + downtimeEnd (P12S, M4S, Queen Eternal)"
# The regression this shape is here for: reading the start and ignoring the end leaves the window
# open for the rest of the pull, and every real clip after it disappears.
$closed = WindowsOf '{ downtimeStart: 60000, downtimeEnd: 120000 }'
Check "a closed window ends where it ended" (Describe $closed) "60-120"
Check "and not at the latest line" (TotalSeconds $closed) 60
Check "agrees with the DPS clock" (TotalSeconds $closed) (ParserDowntime 60000 120000)
Check "still open: to the latest line" (Describe (WindowsOf '{ downtimeStart: 500000, downtimeEnd: 0 }')) "500-600"
Check "never opened: nothing" (Describe (WindowsOf '{ downtimeStart: 0, downtimeEnd: 0 }')) "[]"

Section "D: first/secondDowntime (M5S Brute Abombinator)"
Check "both closed" (Describe (WindowsOf '{ firstDowntimeStart: 60000, firstDowntimeEnd: 120000, secondDowntimeStart: 300000, secondDowntimeEnd: 340000 }')) "60-120,300-340"
Check "the second still open" (Describe (WindowsOf '{ firstDowntimeStart: 60000, firstDowntimeEnd: 120000, secondDowntimeStart: 500000, secondDowntimeEnd: 0 }')) "60-120,500-600"
Check "only the first has happened" (Describe (WindowsOf '{ firstDowntimeStart: 60000, firstDowntimeEnd: 120000, secondDowntimeStart: 0, secondDowntimeEnd: 0 }')) "60-120"

Section "E: p*Downtime fields (the Omega Protocol)"
# TOP names every transition separately; totalDowntimeForFightRange adds all seven up.
$top = @'
{
  p2DowntimeStart: 60000,       p2DowntimeEnd: 100000,
  p3TransitionStart: 150000,    p3TransitionEnd: 180000,
  p4BlueScreenCast: 220000,     p5OmegaMTargetable: 260000,
  p5DeltaDynamisStart: 300000,  p5DeltaDynamisEnd: 320000,
  p5SigmaDynamisStart: 360000,  p5SigmaDynamisEnd: 380000,
  p5OmegaDynamisStart: 420000,  p5OmegaDynamisEnd: 440000,
  blindFaithCast: 500000,       targetableAfterBlindFaith: 520000
}
'@
$topWindows = WindowsOf $top
Check "every transition" (Describe $topWindows) "60-100,150-180,220-260,300-320,360-380,420-440,500-520"
$parserTotal = (ParserDowntime 60000 100000) + (ParserDowntime 150000 180000) + (ParserDowntime 220000 260000) +
               (ParserDowntime 300000 320000) + (ParserDowntime 360000 380000) + (ParserDowntime 420000 440000) +
               (ParserDowntime 500000 520000)
Check "the total agrees with the DPS clock" (TotalSeconds $topWindows) $parserTotal
Check "before its first transition" (Describe (WindowsOf '{ p2DowntimeStart: 0, p2DowntimeEnd: 0 }')) "[]"

Section "nothing to read"
Check "a handler that books no downtime" (Describe (WindowsOf '{ somethingElse: 1 }')) "[]"
Check "no handler at all" (Describe ($downtimeType::Merge($downtimeType::Read($null, [double]$LAST_LINE, $null)))) "[]"

Section "overlaps are merged, not summed"
$overlapping = WindowsOf '{ downtimePeriods: [{ start: 10000, end: 20000 }, { start: 15000, end: 25000 }, { start: 30000, end: 35000 }] }'
Check "merged" (Describe $overlapping) "10-25,30-35"
Check "counted once" (TotalSeconds $overlapping) 20

# ---------------------------------------------------------------- the pull keeps its windows
Section "windows outlive the handler that reported them"
# The parser drops its zone handler the moment the fight ends, so the last collect of a pull - the
# one whose figures everybody actually reads - arrives with no windows at all. Without keeping them
# per fight, every GCD figure was re-measured at that moment with nothing to exclude, and a
# transition that had been correctly ignored all fight turned back into a minute of clipping.
$pipelineType = $asm.GetType("OverlayPluginAddon.Fflogs.MeterPipeline")
$collectionType = $asm.GetType("OverlayPluginAddon.Fflogs.ParserCollection")
$decisionType = $asm.GetType("OverlayPluginAddon.Fflogs.AppliedDecision")
$takeEveryFight = [System.Delegate]::CreateDelegate($decisionType, $pipelineType, "TakeEveryFight")
$pipeline = [Activator]::CreateInstance($pipelineType, @($takeEveryFight, $null))

$engine.Execute(@'
globalThis.pipelineFight = {
  id: 9, state: 'inprogress', startTime: 0, endTime: 600000, downtime: 60000,
  zone: { id: 1, name: 'Zone' },
  friendlyDamage: { actors: { 1: { id: 1, name: 'A', fullType: 'Viper',
    amount: 100, amountTaken: 0, singleTargetAmountTaken: 0, amountGiven: 0, over: 0,
    hitDetails: { hitCount: 1, criticalCount: 0, directHitCount: 0, criticalDirectHitCount: 0, maxHit: 100, minHit: 100 },
    abilities: {} } } },
  friendlyHealing: { actors: {} },
  deaths: { actors: {} },
};
globalThis.pipelineMeters = { fights: [globalThis.pipelineFight] };
globalThis.pipelineOutput = { petsIdTable: new Map(), petsTable: [], getActor: function () { return { unitName: '' }; } };
globalThis.liveHandler = { downtimeTracker: { committedIntervals: [{ start: 60000, end: 120000 }], pendingInterval: null } };
'@)

$withHandler = [Activator]::CreateInstance($collectionType,
    @($engine.Script.pipelineMeters, $engine.Script.pipelineOutput, $engine.Script.liveHandler, [long]$LAST_LINE))
$pipeline.Accept($withHandler)
Check "read while the fight runs" $pipeline.Current.Downtime.Count 1
Check "the fight's downtime agrees" $pipeline.Current.Clocks.Downtime 60

# The fight ends. The parser drops meterFight, and with it the only handler there was to read.
$engine.Execute("globalThis.pipelineFight.state = 'kill';")
$noHandler = [Activator]::CreateInstance($collectionType,
    @($engine.Script.pipelineMeters, $engine.Script.pipelineOutput, $null, [long]$LAST_LINE))
$pipeline.Accept($noHandler)
Check "kept when the handler is gone" $pipeline.Current.Downtime.Count 1
Check "unchanged" (Describe $pipeline.Current.Downtime) "60-120"

# A new pull. Its windows are its own, and the last one's do not follow it.
$engine.Execute("globalThis.pipelineFight.id = 10;")
$pipeline.Accept($noHandler)
Check "a new fight starts with none" $pipeline.Current.Downtime.Count 0

$engine.Dispose()

""
""
if ($failures -gt 0) {
    Write-Host "==> $failures check(s) FAILED" -ForegroundColor Red
    exit 1
}
Write-Host "==> all checks passed" -ForegroundColor Green
