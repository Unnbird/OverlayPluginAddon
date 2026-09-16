<#
.SYNOPSIS
    Regression test for ParserOutput and MeterSnapshot: reading FFLogs' parser output and matching
    it to ACT's table.

.DESCRIPTION
    A fake fight is built as real script objects in a real V8 engine, in the shape parser-ff.js
    produces, and read through the real ParserOutput.ReadFight - so this covers the interop as well
    as the rules. Then MeterSnapshot.Build folds and matches it, and the checks are on what an
    export column would actually print.

    The cases are the ones that have been got wrong before:
      - a pet the parser keeps as an actor of its own (Demi-Bahamut) folds into its owner's totals
        but stays available as a line of its own, so an overlay that sums pets into owners lands
        exactly on the parser's total and not twice it
      - a pet the parser already folded in (Carbuncle) reads zero, not ACT's figure and not empty
      - Limit Break is a row like any other: nobody owns it, and it takes and gives no buffs
      - a row FFLogs never saw reads empty, so ACT's own figure stands
      - the two clocks: damage loses the downtime, healing does not

    The same fixture drives the JavaScript this was ported from, in
    mopimopi/tests/test-fflogs-overlay.js.

    Requires a Release build.  .\build.ps1 -SkipDeps; .\tools\Test-Fflogs.ps1
#>
$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent $PSScriptRoot
$workspace = Split-Path -Parent $repo
$dir = Join-Path $workspace "out\Release\OverlayPluginAddon\net48"
if (-not (Test-Path (Join-Path $dir "OverlayPluginAddon.dll"))) {
    throw "Release build not found in $dir. Run .\build.ps1 -SkipDeps first."
}

# LoadFrom, not ReadAllBytes: ClearScript finds ClearScriptV8.win-x64.dll next to its own assembly.
Add-Type -Path (Join-Path $dir "ClearScript.Core.dll")
Add-Type -Path (Join-Path $dir "ClearScript.V8.dll")
[Microsoft.ClearScript.HostSettings]::AuxiliarySearchPath = $dir
$asm = [Reflection.Assembly]::LoadFrom((Join-Path $dir "OverlayPluginAddon.dll"))
$outputType = $asm.GetType("OverlayPluginAddon.Fflogs.ParserOutput")
$petTablesType = $asm.GetType("OverlayPluginAddon.Fflogs.ParserOutput+PetTables")
$snapshotType = $asm.GetType("OverlayPluginAddon.Fflogs.MeterSnapshot")
$fightMatchType = $asm.GetType("OverlayPluginAddon.Fflogs.FightMatch")
$windowType = $asm.GetType("OverlayPluginAddon.Fflogs.DowntimeWindow")
$noWindows = [Array]::CreateInstance($windowType, 0)

$failures = 0
function Check($label, $actual, $expected) {
    $ok = "$actual" -eq "$expected"
    if (-not $ok) { $script:failures++ }
    "{0} {1,-54} actual={2,-16} expected={3}" -f $(if ($ok) { "PASS" } else { "FAIL" }), $label, $actual, $expected
}
function Section($title) { ""; "=============== $title ===============" }

$engine = New-Object Microsoft.ClearScript.V8.V8ScriptEngine

# ---------------------------------------------------------------- the parser's fight
# 515.2 seconds, of which 60 were spent on something nobody could hit.
$engine.Execute(@'
var hits = (n, c, d, cd, max) => ({ hitCount: n, criticalCount: c, directHitCount: d, criticalDirectHitCount: cd, maxHit: max, minHit: 1 });
var ability = (id, name, h) => ({ id: id, name: name, hitDetails: h });

globalThis.fight = {
  id: 2,
  state: 'inprogress',
  startTime: 1000000,
  endTime: 1000000 + 515200,
  downtime: 60000,
  zone: { id: 1, name: 'Recollection (Extreme)' },
  friendlyDamage: { actors: {
    1: { id: 1, name: 'Viper A', fullType: 'Viper', amount: 16803305, amountTaken: 1806745, singleTargetAmountTaken: 1258057, amountGiven: 0, over: 0,
         hitDetails: hits(400, 100, 120, 30, 89000),
         abilities: { 5: ability(5, 'Reawaken', hits(10, 5, 5, 2, 89000)), 6: ability(6, 'Steel Fangs', hits(390, 95, 115, 28, 40000)) } },
    2: { id: 2, name: 'Summoner S', fullType: 'Summoner', amount: 1000000, amountTaken: 50000, singleTargetAmountTaken: 0, amountGiven: 120000, over: 0,
         hitDetails: hits(200, 50, 60, 10, 30000),
         abilities: { 7: ability(7, 'Ruin III', hits(200, 50, 60, 10, 30000)) } },
    // Demi-Bahamut does not mirror its owner's buffs, so the parser books it on its own.
    3: { id: 3, name: 'Demi-Bahamut', fullType: 'Summoner', amount: 500000, amountTaken: 25000, singleTargetAmountTaken: 0, amountGiven: 0, over: 0,
         hitDetails: hits(20, 5, 5, 1, 60000),
         abilities: { 8: ability(8, 'Wyrmwave', hits(20, 5, 5, 1, 60000)) } },
    4: { id: 4, name: 'Limit Break', fullType: 'LimitBreak', amount: 999999, amountTaken: 0, singleTargetAmountTaken: 0, amountGiven: 0, over: 0,
         hitDetails: hits(1, 0, 0, 0, 999999), abilities: {} },
    5: { id: 5, name: 'My Name', fullType: 'Scholar', amount: 5538352, amountTaken: 106862, singleTargetAmountTaken: 0, amountGiven: 985383, over: 0,
         hitDetails: hits(300, 60, 0, 0, 20000),
         abilities: { 10: ability(10, 'Broil IV', hits(300, 60, 0, 0, 20000)) } },
  } },
  friendlyHealing: { actors: {
    5: { id: 5, name: 'My Name', fullType: 'Scholar', amount: 3000000, amountTaken: 0, singleTargetAmountTaken: 0, amountGiven: 0, over: 900000,
         hitDetails: hits(500, 100, 0, 0, 45000),
         abilities: { 9: ability(9, 'Adloquium', hits(500, 100, 0, 0, 45000)) } },
  } },
  deaths: { actors: {
    1: { deaths: [{ timeOffset: 1 }, { timeOffset: 2 }] },
    5: { deaths: [{ timeOffset: 3 }] },
  } },
};

// The parser's pet bookkeeping: actor 3 is a pet, and petsTable[0] names actor 2 as its owner.
globalThis.parserOutput = {
  petsIdTable: new Map([[3, 1]]),
  petsTable: [{ pet: 3, owner: 2, summon: 0 }],
  getActor: function (id) { return { unitName: '' }; },
};
'@)

$pets = $petTablesType::From($engine.Script.parserOutput)
$fight = $outputType::ReadFight($engine.Script.fight, $pets)

""
"########## FFLogs parser output -> ACT table ##########"

Section "the fight itself"
Check "id"                     $fight.Id 2
Check "state"                  $fight.State "inprogress"
Check "duration"               ([Math]::Round($fight.DurationSeconds, 1)) 515.2
Check "downtime"               $fight.DowntimeSeconds 60
Check "healing table present"  $fight.HasHealing "True"
Check "deaths table present"   $fight.HasDeaths "True"
# Demi-Bahamut folded into Summoner S, so five actors make four rows.
Check "rows"                   $fight.Damage.Count 4

Section "the two clocks"
$clocks = $fightMatchType::Clocks($fight)
Check "the whole fight"                  ([Math]::Round($clocks.Seconds, 1)) 515.2
Check "damage loses the downtime"        ([Math]::Round($clocks.Active, 1)) 455.2
Check "nonsense downtime falls back"     ([Math]::Round($fightMatchType::Clocks(515.2, 900).Active, 1)) 515.2
Check "downtime equal to the fight too"  ([Math]::Round($fightMatchType::Clocks(515.2, 515.2).Active, 1)) 515.2
Check "no downtime at all"               ([Math]::Round($fightMatchType::Clocks(515.2, 0).Active, 1)) 515.2

Section "pet folding"
$summoner = $fight.DamageOf("Summoner S")
Check "the owner's row carries the total"  $summoner.Amount 1500000
Check "its own figures do not"             $summoner.Own.Amount 1000000
Check "the pet is listed under the owner"  $summoner.Pets.Count 1
Check "and named"                          $summoner.Pets[0].Name "Demi-Bahamut"
Check "Demi-Bahamut is not a row"          $(if ($null -eq $fight.DamageOf("Demi-Bahamut")) { "absent" } else { "present" }) "absent"
Check "hit counts fold too"                $summoner.Hits.HitCount 220
Check "the biggest hit is the pet's"       $summoner.Hits.MaxHit 60000

Section "the biggest hit names its ability"
Check "Viper A"  $fight.DamageOf("Viper A").Own.MaxHitAbility "Reawaken"
Check "the pet"  $summoner.Pets[0].MaxHitAbility "Wyrmwave"

Section "deaths"
Check "Viper A"  $fight.Deaths["Viper A"] 2
Check "My Name"  $fight.Deaths["My Name"] 1
Check "nobody else" $fight.Deaths.Count 2

# ---------------------------------------------------------------- the table an overlay sees
$snapshot = $snapshotType::Build($fight, $true, "durations match", $noWindows)
$divisor = 455.2

Section "the raid's totals"
Check "damage"      $snapshot.EncounterDamage (16803305 + 1500000 + 999999 + 5538352)
Check "healing"     $snapshot.EncounterHealed (3000000 + 900000)
Check "rDPS total"  $snapshot.EncounterRdpsAmount `
    ((16803305 - 1806745 + 0) + (1500000 - 75000 + 120000) + 999999 + (5538352 - 106862 + 985383))

Section "a player's row"
$row = $snapshot.Lookup("Summoner S")
Check "damage is the player's own"   $row.Damage 1000000
Check "hits likewise"                $row.Hits.HitCount 200
Check "rDPS is the folded total"     ([Math]::Round($row.Rdps, 2)) ([Math]::Round((1500000 - 75000 + 120000) / $divisor, 2))
Check "aDPS"                         ([Math]::Round($row.Adps, 2)) ([Math]::Round(1500000 / $divisor, 2))
Check "nDPS"                         ([Math]::Round($row.Ndps, 2)) ([Math]::Round((1500000 - 75000) / $divisor, 2))
Check "cDPS"                         ([Math]::Round($row.Cdps, 2)) ([Math]::Round((1500000 + 120000) / $divisor, 2))
Check "rDPS delta is given - taken"  ([Math]::Round($row.RdpsDelta, 2)) ([Math]::Round((120000 - 75000) / $divisor, 2))

Section "rDPS % is a share of the table"
$total = 0.0
foreach ($name in @("Viper A", "Summoner S", "Limit Break", "My Name")) { $total += $snapshot.Lookup($name).RdpsPct }
Check "adds up to 100" ([Math]::Round($total, 4)) 100

Section "Limit Break is a row like any other"
$lb = $snapshot.Lookup("Limit Break")
Check "damage"                     $lb.Damage 999999
Check "takes and gives no buffs"   ([Math]::Round($lb.Rdps, 2)) ([Math]::Round(999999 / $divisor, 2))

Section "healing keeps overheal the way ACT counts it"
$me = $snapshot.Lookup("My Name")
Check "healed includes overheal"  $me.Healed (3000000 + 900000)
Check "overheal on its own"       $me.OverHeal 900000
Check "heal count"                $me.HealHits.HitCount 500
Check "biggest heal's ability"    $me.MaxHealAbility "Adloquium"
Check "deaths"                    $me.Deaths 1

Section "pets get their own share, never ACT's"
$demi = $snapshot.Lookup("Demi-Bahamut (Summoner S)")
Check "the pet the parser kept apart"  $demi.Damage 500000
Check "and its hits"                   $demi.Hits.HitCount 20
Check "no rDPS of its own"             $demi.Rdps 0
# Carbuncle mirrors its owner's buffs, so the parser folded it in and there is nothing left to give
# this row. Zero, not ACT's figure: mopimopi sums pet rows into the owner, and the owner's row
# already has it.
$carbuncle = $snapshot.Lookup("Carbuncle (Summoner S)")
Check "one the parser already folded in" $carbuncle.Damage 0
Check "which is not the same as absent"  $(if ($null -eq $carbuncle) { "null" } else { "zero" }) "zero"
Check "a pet of nobody FFLogs knows"     $(if ($null -eq $snapshot.Lookup("Eos (Some NPC)")) { "null" } else { "zero" }) "null"

Section "rows FFLogs never saw keep ACT's figures"
Check "an NPC" $(if ($null -eq $snapshot.Lookup("Striking Dummy")) { "null" } else { "found" }) "null"
Check "an empty name" $(if ($null -eq $snapshot.Lookup("")) { "null" } else { "found" }) "null"

Section "a fight that is not this encounter"
$unapplied = $snapshotType::Build($fight, $false, "fight does not match the encounter", $noWindows)
Check "every row reads empty" $(if ($null -eq $unapplied.Lookup("Summoner S")) { "null" } else { "found" }) "null"
Check "and it says why"       $unapplied.Reason "fight does not match the encounter"
# The GCD half does not care which encounter ACT thinks is running, so the windows survive.
Check "downtime still travels" $unapplied.Downtime.Count 0

Section "matching a fight to ACT's encounter"
Check "durations within tolerance"    $fightMatchType::DurationsMatch(515.2, 512.0, 90) "True"
Check "a new pull after a wipe"       $fightMatchType::DurationsMatch(12.0, 515.2, 90) "False"
# M8S: ACT opens a second encounter at the phase transition while the parser keeps one fight.
Check "encounter opened mid-fight"    $fightMatchType::EncounterWithinFight(1300000, 1000000, 1515200, $true, 90) "True"
Check "before the fight started"      $fightMatchType::EncounterWithinFight(800000, 1000000, 1515200, $true, 90) "False"
Check "after a finished fight"        $fightMatchType::EncounterWithinFight(1600000, 1000000, 1515200, $false, 90) "False"
Check "a hair after, still the same"  $fightMatchType::EncounterWithinFight(1517000, 1000000, 1515200, $false, 90) "True"

Section "an empty fight"
$engine.Execute("globalThis.empty = { id: 1, state: 'inprogress', startTime: 0, endTime: 1000, downtime: 0, friendlyDamage: { actors: {} } };")
$emptyFight = $outputType::ReadFight($engine.Script.empty, $pets)
Check "no rows"            $emptyFight.Damage.Count 0
Check "no healing table"   $emptyFight.HasHealing "False"
Check "no deaths table"    $emptyFight.HasDeaths "False"
$emptySnapshot = $snapshotType::Build($emptyFight, $true, "durations match", $noWindows)
Check "nothing to look up" $(if ($null -eq $emptySnapshot.Lookup("Viper A")) { "null" } else { "found" }) "null"

$engine.Dispose()

""
""
if ($failures -gt 0) {
    Write-Host "==> $failures check(s) FAILED" -ForegroundColor Red
    exit 1
}
Write-Host "==> all checks passed" -ForegroundColor Green
