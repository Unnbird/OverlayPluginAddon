<#
.SYNOPSIS
    Regression test for GcdTracker, the xivanalysis-style GCD uptime model.

.DESCRIPTION
    Drives the compiled tracker with synthetic presses and checks the results against values
    worked out by hand: clean rotations, skill/spell speed inference, flat-recast actions (dance
    steps, Ninjutsu), haste windows, long casts and caster tax, real gaps, AoE de-duplication,
    instant vs hard casts, Dualcast alternation, one-measurement-per-GCD, timestamp jitter, the
    150ms lost-time slack, the 100% ceiling and the too-few-casts default.

    The scenarios are the GCD half of RdpsOverlay's tools/Test-Ledger.ps1, carried over verbatim.
    Run it after any change to GcdTracker, ActionData or data/actions.json. Requires a Release build.

    .\build.ps1 -SkipDeps; .\tools\Test-Gcd.ps1
#>
$ErrorActionPreference = "Stop"

# Paths are derived from this script rather than hardcoded, so the suite runs from a clone in any
# location - a CI checkout included - and not only on the machine it was written on.
$repo = Split-Path -Parent $PSScriptRoot
$workspace = Split-Path -Parent $repo
$dir = Join-Path $workspace "out\Release\OverlayPluginAddon\net48"

# The resolver below outlives this script: AppDomain handlers stay registered for the life of
# the process, so running two suites back to back leaves the first one still subscribed. It
# reads the output directory out of a global for that reason.
$global:GcdTestDir = $dir
$global:GcdTestResolved = @{}
[AppDomain]::CurrentDomain.add_AssemblyResolve([ResolveEventHandler]{ param($s,$e)
  if ($null -eq $global:GcdTestResolved) { return $null }
  $n = ($e.Name -split ',')[0]
  if ($global:GcdTestResolved.ContainsKey($n)) { return $null }
  $global:GcdTestResolved[$n] = $true
  $p = Join-Path $global:GcdTestDir "$n.dll"
  if (Test-Path $p) { return [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($p)) }
  return $null })

$asm = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $dir "OverlayPluginAddon.dll")))
$T = { param($n) $asm.GetType("OverlayPluginAddon.$n") }

$global:RdpsTestFailures = 0
function Check($label, $actual, $expected, $tol = 0.5) {
    $ok = [Math]::Abs($actual - $expected) -le $tol
    if (-not $ok) { $global:RdpsTestFailures++ }
    "{0} {1,-52} actual={2,12:N2}  expected={3,12:N2}" -f $(if($ok){"PASS"}else{"FAIL"}), $label, $actual, $expected
}
""
"########## GCD tracker (xivanalysis model) ##########"
$actions = (& $T "ActionData").GetMethod("Load").Invoke($null, @([string](Join-Path $repo "data\actions.json")))
"actions.json: $($actions.ActionCount) recast overrides, $($actions.SpeedStatusCount) haste statuses, loadError='$($actions.LoadError)'"

# Resolved once at script scope: reading $T / $actions from inside a function frame does not
# reliably find them here, and CreateInstance then quietly yields nothing.
$gcdType = & $T "GcdTracker"
$actionTable = $actions

$t0 = [DateTime]::new(2026, 1, 1, 0, 0, 0)

# Nothing outside the presses goes into the GCD numbers: the window runs from a player's first
# press to their latest one, so no encounter clock is handed in anywhere below.

# Action ids straight out of data/actions.json.
$PLAIN   = 9999901   # absent from the table -> the 2500ms / skill-speed default
$EMBOITE = 15999     # 1000ms flat, no speed attribute
$FUMA    = 2265      # 1500ms flat, no speed attribute (Ninjutsu)
$DRIP    = 34688     # 6000ms recast, 4000ms cast, spell speed (Rainbow Drip)

""
"=============== 9. clean 2.50s rotation ==============="
$g = [Activator]::CreateInstance($gcdType, @($actionTable))
for ($i = 0; $i -lt 40; $i++) { $g.Record("Alice", $PLAIN, $t0.AddSeconds(2.5 * $i), 1.0, $false, $false) }
# First press to last press: 39 closed intervals occupying 39 recasts. The 40th is still turning.
$st = $g.StatsFor("Alice")
Check "count"                 $st.Count 40 0
Check "recast"                $st.Recast 2.50 0.01
Check "speed stat = minimum"  $st.SpeedStat 420 0
Check "uptime %"              ($st.Uptime * 100) 100 0.1
Check "clip"                  $st.Clip 0 0.01

""
"=============== 10. skill speed: a 2.40s rotation infers a higher stat ==============="
$g = [Activator]::CreateInstance($gcdType, @($actionTable))
for ($i = 0; $i -lt 40; $i++) { $g.Record("Bob", $PLAIN, $t0.AddSeconds(2.4 * $i), 1.0, $false, $false) }
$st = $g.StatsFor("Bob")
Check "recast"        $st.Recast 2.40 0.02
Check "stat above min" $(if ($st.SpeedStat -gt 420) { 1 } else { 0 }) 1 0
Check "uptime %"      ($st.Uptime * 100) 100 0.5

""
"=============== 11. flat-recast GCDs: 1.0s dance steps inside a 2.5s rotation ==============="
$g = [Activator]::CreateInstance($gcdType, @($actionTable))
$now = $t0
for ($cycle = 0; $cycle -lt 6; $cycle++) {
  for ($i = 0; $i -lt 8; $i++) { $g.Record("Dancer", $PLAIN,   $now, 1.0, $false, $false); $now = $now.AddSeconds(2.5) }
  for ($i = 0; $i -lt 4; $i++) { $g.Record("Dancer", $EMBOITE, $now, 1.0, $false, $false); $now = $now.AddSeconds(1.0) }
}
$dur = ($now - $t0).TotalSeconds
$st = $g.StatsFor("Dancer")
Check "count"                     $st.Count 72 0
Check "steps did not skew recast" $st.Recast 2.50 0.01
Check "no phantom clip"           $st.Clip 0 0.01
Check "uptime 100%"               ($st.Uptime * 100) 100 0.1

""
"=============== 12. flat-recast GCDs: 1.5s Ninjutsu inside a 2.5s rotation ==============="
$g = [Activator]::CreateInstance($gcdType, @($actionTable))
$now = $t0
for ($cycle = 0; $cycle -lt 6; $cycle++) {
  for ($i = 0; $i -lt 8; $i++) { $g.Record("Nin", $PLAIN, $now, 1.0, $false, $false); $now = $now.AddSeconds(2.5) }
  for ($i = 0; $i -lt 3; $i++) { $g.Record("Nin", $FUMA,  $now, 1.0, $false, $false); $now = $now.AddSeconds(1.5) }
}
$st = $g.StatsFor("Nin")
Check "recast"      $st.Recast 2.50 0.01
Check "no phantom clip" $st.Clip 0 0.01
Check "uptime 100%" ($st.Uptime * 100) 100 0.1

""
"=============== 12b. Ninja: 2.12s job-haste GCD with mudras and Ninjutsu in it ==============="
# The 15% job haste is not modelled separately; the estimate absorbs it and lands within one 45ms
# batch of the 2.12s the tooltip says. Mudras (0.5s flat) and Ninjutsu (1.5s flat) sit inside
# without voting on the estimate or leaving phantom gaps - provided they are recorded at all, which
# is the next section's business.
$TEN = 2259; $CHI = 2261; $RAITON = 2267
$g = [Activator]::CreateInstance($gcdType, @($actionTable))
$now = $t0
for ($cycle = 0; $cycle -lt 6; $cycle++) {
  for ($i = 0; $i -lt 3; $i++) { $g.Record("Nin2", $PLAIN, $now, 1.0, $false, $false); $now = $now.AddSeconds(2.12) }
  $g.Record("Nin2", $TEN,    $now, 1.0, $false, $false); $now = $now.AddSeconds(0.5)
  $g.Record("Nin2", $CHI,    $now, 1.0, $false, $false); $now = $now.AddSeconds(0.5)
  $g.Record("Nin2", $RAITON, $now, 1.0, $false, $false); $now = $now.AddSeconds(1.5)
}
$g.Record("Nin2", $PLAIN, $now, 1.0, $false, $false)
$st = $g.StatsFor("Nin2")
Check "count"                        $st.Count 37 0
Check "recast 2.12s, within a batch" $st.Recast 2.12 0.03
Check "no phantom clip"              $st.Clip 0 0.01
Check "uptime 100%"                  ($st.Uptime * 100) 100 0.1
Check "only the weaponskills voted"  $st.SkillSpeedSamples 18 0

""
"=============== 12c. the mudras and Ninjutsu are GCDs although the game files them as abilities ==============="
# FFXIV's ActionCategory for Ten/Chi/Jin and every Ninjutsu is 4, "Ability". Asked alone, the
# category table saw Ten - Chi - Raiton as a 2.5s hole after Aeolian Edge and booked it as lost time
# on every Ninjutsu: a real Ninja read 77.8% where the party read 95%. actions.json carries
# xivanalysis' own onGcd flags, and EventSource.IsGcdAction asks them first.
Check "actions.json lists onGcd ids"       $(if ($actions.OnGcdCount -gt 400) { 1 } else { 0 }) 1 0
Check "Ten rolls the GCD"                  $(if ($actions.IsOnGcd([uint32]2259)) { 1 } else { 0 }) 1 0
Check "Chi too"                            $(if ($actions.IsOnGcd([uint32]2261)) { 1 } else { 0 }) 1 0
Check "and Raiton"                         $(if ($actions.IsOnGcd([uint32]2267)) { 1 } else { 0 }) 1 0
Check "Fuma Shuriken under Ten Chi Jin"    $(if ($actions.IsOnGcd([uint32]18873)) { 1 } else { 0 }) 1 0
Check "Monk's Forbidden Meditation"        $(if ($actions.IsOnGcd([uint32]36942)) { 1 } else { 0 }) 1 0
Check "Samurai's Meditate"                 $(if ($actions.IsOnGcd([uint32]7497)) { 1 } else { 0 }) 1 0
Check "Trick Attack does not"              $(if ($actions.IsOnGcd([uint32]2258)) { 1 } else { 0 }) 0 0
Check "Kunai's Bane does not"              $(if ($actions.IsOnGcd([uint32]36957)) { 1 } else { 0 }) 0 0
Check "a plain weaponskill is listed too"  $(if ($actions.IsOnGcd([uint32]2240)) { 1 } else { 0 }) 1 0

""
"=============== 13. haste: a 0.80 modifier shortens the GCD, not the score ==============="
$g = [Activator]::CreateInstance($gcdType, @($actionTable))
$now = $t0
for ($i = 0; $i -lt 20; $i++) { $g.Record("Whm", $PLAIN, $now, 1.0, $false, $false); $now = $now.AddSeconds(2.5) }
for ($i = 0; $i -lt 20; $i++) { $g.Record("Whm", $PLAIN, $now, 0.8, $false, $false); $now = $now.AddSeconds(2.0) }
$st = $g.StatsFor("Whm")
# Presence of Mind intervals normalise back to 2.5s, so the stat is unchanged by the burst.
Check "haste did not skew recast" $st.Recast 2.50 0.01
Check "no phantom clip"           $st.Clip 0 0.01
Check "uptime 100%"               ($st.Uptime * 100) 100 0.5

""
"=============== 13b. Inspiration hastens aetherhue spells, not the hammers ==============="
# "Reduces cast time and recast time of aetherhue spells and Star Prism by 25%." The hammer combo
# is what a Pictomancer presses inside Starry Muse, so a 0.75 applied to every GCD expected 1.875s,
# saw 2.5s, and booked 0.6s of lost time on each of them - a 74% uptime on a 92-GCD kill.
$INSPIRATION = [uint32]3689
Check "Fire in Red is hastened"        $actionTable.SpeedModifierOf($INSPIRATION, [uint32]34650) 0.75 0.001
Check "Thunder II in Magenta too"      $actionTable.SpeedModifierOf($INSPIRATION, [uint32]34661) 0.75 0.001
Check "Star Prism too"                 $actionTable.SpeedModifierOf($INSPIRATION, [uint32]34681) 0.75 0.001
Check "Hammer Stamp is not"            $actionTable.SpeedModifierOf($INSPIRATION, [uint32]34678) 1.0 0.001
Check "Polishing Hammer is not"        $actionTable.SpeedModifierOf($INSPIRATION, [uint32]34680) 1.0 0.001
Check "Comet in Black is not"          $actionTable.SpeedModifierOf($INSPIRATION, [uint32]34663) 1.0 0.001
Check "Rainbow Drip is not"            $actionTable.SpeedModifierOf($INSPIRATION, [uint32]34688) 1.0 0.001
Check "an unlimited haste still applies to anything" $actionTable.SpeedModifierOf([uint32]157, [uint32]34678) 0.8 0.001
# What the tracker sees once the event source passes the per-action modifier: hammers at their
# real 2.5s under Inspiration are a clean rotation, not a run of 0.6s gaps.
$g = [Activator]::CreateInstance($gcdType, @($actionTable))
$now = $t0
for ($i = 0; $i -lt 12; $i++) { $g.Record("Pct", [uint32]34678, $now, 1.0, $false, $false); $now = $now.AddSeconds(2.5) }
$st = $g.StatsFor("Pct")
Check "hammers under Inspiration lose nothing" $st.Clip 0 0.01
Check "uptime stays 100%"                      ($st.Uptime * 100) 100 0.5

""
"=============== 14. long cast: recast and caster tax ==============="
$g = [Activator]::CreateInstance($gcdType, @($actionTable))
$now = $t0
for ($i = 0; $i -lt 20; $i++) { $g.Record("Pct", $PLAIN, $now, 1.0, $false, $false); $now = $now.AddSeconds(2.5) }
$g.Record("Pct", $DRIP, $now, 1.0, $true, $true); $now = $now.AddSeconds(6.0)
$g.Record("Pct", $PLAIN, $now, 1.0, $false, $false); $now = $now.AddSeconds(2.5)
$st = $g.StatsFor("Pct")
# Rainbow Drip occupies its 6s recast, so following it 6s later is not a clip.
Check "long recast is not clip" $st.Clip 0 0.01
Check "uptime 100%"             ($st.Uptime * 100) 100 0.5

""
"=============== 15. a real gap is still caught ==============="
$g = [Activator]::CreateInstance($gcdType, @($actionTable))
$now = $t0
for ($i = 0; $i -lt 20; $i++) { $g.Record("Carl", $PLAIN, $now, 1.0, $false, $false); $now = $now.AddSeconds(2.5) }
$now = $now.AddSeconds(1.5)                                        # 4.0s gap: 1.5s lost
for ($i = 0; $i -lt 20; $i++) { $g.Record("Carl", $PLAIN, $now, 1.0, $false, $false); $now = $now.AddSeconds(2.5) }
$st = $g.StatsFor("Carl")
Check "recast"        $st.Recast 2.50 0.01
Check "clip = 1.5s"   $st.Clip 1.5 0.05
Check "uptime < 100%" $(if ($st.Uptime -lt 1.0) { 1 } else { 0 }) 1 0

""
"=============== 16. AoE hitting 8 targets counts once ==============="
$g = [Activator]::CreateInstance($gcdType, @($actionTable))
for ($i = 0; $i -lt 10; $i++) {
  $when = $t0.AddSeconds(2.5 * $i)
  for ($k = 0; $k -lt 8; $k++) { $g.Record("Dave", $PLAIN, $when, 1.0, $false, $false) }
}
$st = $g.StatsFor("Dave")
Check "count deduped" $st.Count 10 0
Check "recast"        $st.Recast 2.50 0.01

""
"=============== 14b. cast equals recast: the tax is part of the GCD ==============="
# A Black Mage spamming Blizzard I (2.5s cast on a 2.5s recast) at 2.35s spell speed: every cast
# runs 2.35s and the next press lands at 2.45s because caster tax follows a cast that gates the
# GCD. Deciding the tax by comparing the *adjusted* cast against a flat 2500 meant it was never
# paid once the player had any speed, so each cast occupied 2.35s of a 2.45s gap and a flawless
# rotation read 96%. Seen on a dummy: 32 casts, 0.8s lost, 93.7% uptime.
$BLIZZARD_I = 142
$g = [Activator]::CreateInstance($gcdType, @($actionTable))
$now = $t0
for ($i = 0; $i -lt 20; $i++) { $g.Record("Blm", $BLIZZARD_I, $now, 1.0, $true, $true, 2350.0); $now = $now.AddSeconds(2.45) }
$st = $g.StatsFor("Blm")
Check "recast estimated from the cast bar" $st.Recast 2.35 0.02
Check "tax counts toward occupancy: no lost" $st.Clip 0 0.01
Check "uptime 100%"                          ($st.Uptime * 100) 100 0.5

# Inside Ley Lines the same spell is 0.85 of that, tax unchanged: 2.00s cast, 2.10s cadence.
for ($i = 0; $i -lt 10; $i++) { $g.Record("Blm", $BLIZZARD_I, $now, 0.85, $true, $true, 1998.0); $now = $now.AddSeconds(2.10) }
$st = $g.StatsFor("Blm")
Check "Ley Lines: estimate unchanged" $st.Recast 2.35 0.02
Check "Ley Lines: uptime stays 100%"  ($st.Uptime * 100) 100 0.5

# Blizzard III under Astral Fire III: the table says 3.5s, the bar ran 1.49s (halved, then 0.85).
# The recast gates instead, the press lands at 2.10s, and the cast must not vote on speed.
$BLIZZARD_III = 154
$g.Record("Blm", $BLIZZARD_III, $now, 0.85, $true, $true, 1487.0); $now = $now.AddSeconds(2.10)
$g.Record("Blm", $BLIZZARD_I, $now, 0.85, $true, $true, 1998.0); $now = $now.AddSeconds(2.10)
$st = $g.StatsFor("Blm")
Check "halved cast does not skew the estimate" $st.Recast 2.35 0.02
Check "halved cast: no lost time"              $st.Clip 0 0.01
Check "halved cast: uptime stays 100%"         ($st.Uptime * 100) 100 0.5

# Fire III cast in full (3.5s x 0.94 = 3.29s) is followed by tax; a 3.39s press is clean.
$FIRE_III = 152
$g.Record("Blm", $FIRE_III, $now, 1.0, $true, $true, 3290.0); $now = $now.AddSeconds(3.39)
$g.Record("Blm", $BLIZZARD_I, $now, 1.0, $true, $true, 2350.0); $now = $now.AddSeconds(2.45)
$st = $g.StatsFor("Blm")
Check "full-length long cast pays tax, no lost" $st.Clip 0 0.01
Check "full-length long cast: uptime 100%"      ($st.Uptime * 100) 100 0.5

""
"=============== 14c. a break stays a break, even under a stale table ==============="
# What the dummy showed: Blizzard I's real cast is 2.0s (1.966s at this speed) while the table
# still says 2.5s, so the bar-over-table factor fell under 0.80, every interval was refused, and
# the estimate sat at the 2.50s default against a true 2.45s recast. Occupancy was then credited
# at 2.50 per cast for a 2.45 gap, and twenty casts of that surplus swallowed a five second pause -
# keep attacking after a break and the reading climbed back to 100%.
$BLIZZARD_I = 142
$g = [Activator]::CreateInstance($gcdType, @($actionTable))
$now = $t0
for ($i = 0; $i -lt 20; $i++) { $g.Record("Blm2", $BLIZZARD_I, $now, 1.0, $true, $true, 1966.0); $now = $now.AddSeconds(2.45) }
$st = $g.StatsFor("Blm2")
Check "short cast: the recast gates, and votes"  $st.Recast 2.45 0.02
Check "short cast: no tax, no lost"              $st.Clip 0 0.01
Check "short cast: uptime 100%"                  ($st.Uptime * 100) 100 0.5
$now = $now.AddSeconds(5.0)                                        # a 7.45s gap: 5.0s lost
for ($i = 0; $i -lt 20; $i++) { $g.Record("Blm2", $BLIZZARD_I, $now, 1.0, $true, $true, 1966.0); $now = $now.AddSeconds(2.45) }
$st = $g.StatsFor("Blm2")
$span = 40 * 2.45 + 5.0 - 2.45
Check "the pause is charged in full"             $st.Clip 5.0 0.05
Check "and stays charged after 20 more casts"    ($st.Uptime * 100) ((1 - 5.0 / $span) * 100) 0.5
Check "uptime is 1 - lost/span, not occupancy"   ($st.Uptime * 100) (100 - $st.Clip / $span * 100) 0.01

# The same rotation before the estimate has converged (three casts): a 2.50 default against 2.45
# gaps must not print a surplus that a later break can be paid from.
$g = [Activator]::CreateInstance($gcdType, @($actionTable))
$now = $t0
for ($i = 0; $i -lt 3; $i++) { $g.Record("Blm3", $BLIZZARD_I, $now, 1.0, $true, $true, 1966.0); $now = $now.AddSeconds(2.45) }
$now = $now.AddSeconds(3.0)
for ($i = 0; $i -lt 30; $i++) { $g.Record("Blm3", $BLIZZARD_I, $now, 1.0, $true, $true, 1966.0); $now = $now.AddSeconds(2.45) }
$st = $g.StatsFor("Blm3")
Check "early break survives thirty more casts" $(if ($st.Uptime -lt 0.97) { 1 } else { 0 }) 1 0

# Slidecast slack only for a cast that gated the GCD. Blizzard I's bar ends long before its
# recast, so presses 0.27s and 0.50s late are idle - the dummy log showed both waved through.
$g = [Activator]::CreateInstance($gcdType, @($actionTable))
$now = $t0
for ($i = 0; $i -lt 10; $i++) { $g.Record("Blm4", $BLIZZARD_I, $now, 1.0, $true, $true, 1966.0); $now = $now.AddSeconds(2.45) }
$g.Record("Blm4", $BLIZZARD_I, $now, 1.0, $true, $true, 1966.0); $now = $now.AddSeconds(2.72)
$g.Record("Blm4", $BLIZZARD_I, $now, 1.0, $true, $true, 1966.0); $now = $now.AddSeconds(2.95)
for ($i = 0; $i -lt 10; $i++) { $g.Record("Blm4", $BLIZZARD_I, $now, 1.0, $true, $true, 1966.0); $now = $now.AddSeconds(2.45) }
$st = $g.StatsFor("Blm4")
Check "late presses after a short cast are lost" $st.Clip (0.27 + 0.50) 0.05
# A full-length Fire III does gate the GCD, and a press 0.31s after it is inside the slidecast slack.
$g.Record("Blm4", $FIRE_III, $now, 1.0, $true, $true, 3290.0); $now = $now.AddSeconds(3.70)
$g.Record("Blm4", $BLIZZARD_I, $now, 1.0, $true, $true, 1966.0)
$st = $g.StatsFor("Blm4")
Check "gating cast keeps its slidecast slack" $st.Clip (0.27 + 0.50) 0.05

""
"=============== 16b. instant casts do not pay their base cast time ==============="
# Red Mage: Verthunder's data says a 5s cast, but Dualcast fires it instantly. Treating the base
# cast time as real normalised a genuine 2.5s interval to (2500-100)/2 = 1200ms, so the estimator
# settled near 1.0s and uptime read about 40% of the truth.
$VERTHUNDER = 7505   # recast 2500, castTime 5000, SPELL_SPEED
$g = [Activator]::CreateInstance($gcdType, @($actionTable))
for ($i = 0; $i -lt 30; $i++) { $g.Record("Rdm", $VERTHUNDER, $t0.AddSeconds(2.5 * $i), 1.0, $false, $true) }
$st = $g.StatsFor("Rdm")
Check "instant: recast stays 2.50s" $st.Recast 2.50 0.02
Check "instant: uptime 100%"        ($st.Uptime * 100) 100 0.5

# The same spell actually cast: 5s cast plus caster tax gates the next press, so a 5.1s cadence
# is the clean rotation rather than four seconds of clip.
$g = [Activator]::CreateInstance($gcdType, @($actionTable))
for ($i = 0; $i -lt 30; $i++) { $g.Record("Rdm2", $VERTHUNDER, $t0.AddSeconds(5.1 * $i), 1.0, $true, $true) }
$st = $g.StatsFor("Rdm2")
Check "hard cast: recast stays 2.50s" $st.Recast 2.50 0.05
Check "hard cast: no phantom clip"    $st.Clip 0 0.5

""
"=============== 16c. Dualcast: alternating instant and hard cast ==============="
# The pattern a Red Mage actually produces, and what a real log showed: 20 cast bars against 39
# effects. Timed off when the damage lands, this rotation collapses - an instant that follows a
# 2.5s cast lands at the same instant the cast does, reading as a zero-length GCD, while the cast
# before it reads as double length. Timed off when the button went down, it is a flat 2.5s.
#
# Presses are every 2.5s. Odd ones are hard casts whose effect lands 2.5s later.
# Verthunder's data says a 5s cast because it is only ever meant to be fired through Dualcast.
# Jolt is the hard cast that grants it, and at 2s its cast is shorter than the GCD, so the recast
# governs and no caster tax applies.
$VERTHUNDER = 7505      # in the table: recast 2500, castTime 5000, SPELL_SPEED
$JOLT       = 9999902   # not in the table: plain 2500ms default, category says spell
$g = [Activator]::CreateInstance($gcdType, @($actionTable))
for ($i = 0; $i -lt 30; $i++) {
  $press = $t0.AddSeconds(2.5 * $i)
  if ($i % 2 -eq 0) {
    # Jolt, hard cast. The caller passes the cast-bar start, not the effect timestamp.
    $g.Record("Rdm3", $JOLT, $press, 1.0, $true, $true)
  } else {
    # Verthunder, made instant by Dualcast: press and effect are the same moment.
    $g.Record("Rdm3", $VERTHUNDER, $press, 1.0, $false, $true)
  }
}
$st = $g.StatsFor("Rdm3")
Check "alternating: recast 2.50s"  $st.Recast 2.50 0.05
Check "alternating: no phantom clip" $st.Clip 0 0.5
Check "alternating: uptime 100%"   ($st.Uptime * 100) 100 0.5

""
"=============== 16d. Dualcast: a hard cast starting must not spike uptime ==============="
# What a real Red Mage dummy log showed. Casts are recorded when the button goes down; Jolt's
# damage landed 1.47s after its cast bar started. The old denominator was ACT's encounter
# duration, and ACT's clock only moves when damage lands - so the moment Jolt started, the
# Verthunder before it closed and was charged 2.5s while the denominator still ended at
# Verthunder's hit. Uptime jumped, then fell back at the next press. Every hard cast after an
# instant (Dualcast, Swiftcast, Acceleration) made that sawtooth.
#
# Measured from press to press, both halves move at the same moments: the reading after a hard
# cast starts is the same 100% as the reading after the instant before it.
$JOLT3 = 0x908C          # 2.0s cast, 2.5s recast; not in the table, category says spell
$VT3   = 0x64FF          # Verthunder III: in the table, 5.0s cast, only ever fired via Dualcast
$g = [Activator]::CreateInstance($gcdType, @($actionTable))
for ($i = 0; $i -lt 20; $i++) {
  $press = $t0.AddSeconds(2.5 * $i)
  if ($i % 2 -eq 0) { $g.Record("Dual", $JOLT3, $press, 1.0, $true,  $true) }
  else              { $g.Record("Dual", $VT3,   $press, 1.0, $false, $true) }
  if ($i -ge 2) {
    $st = $g.StatsFor("Dual")
    $kind = if ($i % 2 -eq 0) { "hard cast starts" } else { "instant lands" }
    Check "100% after press $i ($kind)" ($st.Uptime * 100) 100 0.1
  }
}
Check "no phantom clip" $st.Clip 0 0.01

""
"=============== 16d2. the number moves only when a GCD is pressed ==============="
# Between presses nothing is re-read: the recast still turning and the gap that is not yet a
# clip stay out of both halves until the press that closes them. Then the idle is charged in
# full by that press.
$g = [Activator]::CreateInstance($gcdType, @($actionTable))
for ($i = 0; $i -lt 20; $i++) { $g.Record("Live", $PLAIN, $t0.AddSeconds(2.5 * $i), 1.0, $false, $false) }
$lastPress = 19 * 2.5
$st = $g.StatsFor("Live")
Check "uptime 100% after the press"     ($st.Uptime * 100) 100 0.1
Check "clip 0 after the press"          $st.Clip 0 0.01
Check "in-flight cast is not occupied"  $st.OccupiedSeconds (19 * 2.5) 0.01
$g.Record("Live", $PLAIN, $t0.AddSeconds($lastPress + 30.0), 1.0, $false, $false)
$span = $lastPress + 30.0
$st = $g.StatsFor("Live")
Check "the next press charges the idle" $st.Clip 27.5 0.1
Check "and drops uptime with it"        ($st.Uptime * 100) (20 * 2.5 / $span * 100) 0.5
Check "occupied + clip = span"          ($st.OccupiedSeconds + $st.Clip) $span 0.01

""
"=============== 16e. jitter: a press 40ms early still cost a whole GCD ==============="
# Log timestamps are batched (~45ms), so a clean 2.5s rotation shows gaps of 2.46 to 2.54. The
# xivanalysis model charges each cast its full recast regardless of the gap; the earlier
# min(recast, gap) version quietly shaved every early-looking press and could never read 100%.
$g = [Activator]::CreateInstance($gcdType, @($actionTable))
$now = $t0
$rng = New-Object System.Random(7)
for ($i = 0; $i -lt 40; $i++) {
  $g.Record("Jit", $PLAIN, $now, 1.0, $false, $false)
  $now = $now.AddSeconds(2.5 + ($rng.NextDouble() - 0.5) * 0.08)   # +/- 40ms
}
$span = ($now - $t0).TotalSeconds
$st = $g.StatsFor("Jit")
Check "jitter: recast still 2.50s"   $st.Recast 2.50 0.02
Check "jitter: no lost time"         $st.Clip 0 0.01
Check "jitter: uptime reads full"    ($st.Uptime * 100) 100 1.0

""
"=============== 16f. lost time has 150ms of slack (GCD_ERROR_OFFSET) ==============="
$g = [Activator]::CreateInstance($gcdType, @($actionTable))
$now = $t0
for ($i = 0; $i -lt 20; $i++) { $g.Record("Slack", $PLAIN, $now, 1.0, $false, $false); $now = $now.AddSeconds(2.5) }
$now = $now.AddSeconds(0.12)                                       # 2.62s gap: inside the slack
for ($i = 0; $i -lt 20; $i++) { $g.Record("Slack", $PLAIN, $now, 1.0, $false, $false); $now = $now.AddSeconds(2.5) }
$now = $now.AddSeconds(0.30)                                       # 2.80s gap: 300ms past recast, over the slack
for ($i = 0; $i -lt 20; $i++) { $g.Record("Slack", $PLAIN, $now, 1.0, $false, $false); $now = $now.AddSeconds(2.5) }
$span = ($now - $t0).TotalSeconds
$st = $g.StatsFor("Slack")
Check "120ms over is not lost"       $st.Clip 0.30 0.02   # only the 300ms gap counts, in full

""
"=============== 17a. uptime can never exceed 100% ==============="
# The bug this guards: counting the final cast's full recast against a window that stops at
# that cast produced n/(n-1), which is 114% over eight GCDs. The cast in flight is in neither half.
$g = [Activator]::CreateInstance($gcdType, @($actionTable))
for ($i = 0; $i -lt 8; $i++) { $g.Record("Short", $PLAIN, $t0.AddSeconds(2.5 * $i), 1.0, $false, $false) }
$st = $g.StatsFor("Short")
Check "8-GCD pull is exactly 100%" ($st.Uptime * 100) 100 0.1
Check "occupied + clip = span"     ($st.OccupiedSeconds + $st.Clip) (7 * 2.5) 0.01

""
"=============== 17. too few casts falls back to the 2.5s default ==============="
$g = [Activator]::CreateInstance($gcdType, @($actionTable))
$g.Record("Eve", $PLAIN, $t0, 1.0, $false, $false)
$g.Record("Eve", $PLAIN, $t0.AddSeconds(2.1), 1.0, $false, $false)
$st = $g.StatsFor("Eve")
Check "recast defaults"    $st.Recast 2.50 0.01
Check "flagged as default" $(if ($st.RecastEstimated) { 1 } else { 0 }) 0 0

""
"=============== 18. downtime: a boss nobody can hit is not clipping ==============="
# The windows come from the FFLogs parser's zone handler; the tracker only needs the intervals.
# Time inside one is taken out of both halves - it is not lost GCD time, and it is not in the span
# the lost time is measured against. This is the difference between M8S' minute-long transition
# reading as a minute of clipping and reading as nothing at all.
$windowType = $asm.GetType("OverlayPluginAddon.Fflogs.DowntimeWindow")
function Windows { param([double[][]] $pairs)
  $arr = [Array]::CreateInstance($windowType, $pairs.Count)
  for ($i = 0; $i -lt $pairs.Count; $i++) {
    $arr[$i] = [Activator]::CreateInstance($windowType, @([double]$pairs[$i][0], [double]$pairs[$i][1]))
  }
  return ,$arr
}
# The windows come in the parser's clock: Unix epoch milliseconds, stamped off the log lines. The
# tracker has to keep its presses in the same clock or no window ever meets a gap - which is what
# happened in the field, with three windows kept and a minute-long transition still charged as
# clipping, while this very test passed by building its windows on the tracker's old clock.
function At { param([double] $seconds) return [double]([DateTimeOffset]::new($t0.AddSeconds($seconds)).ToUnixTimeMilliseconds()) }

$probe = [Activator]::CreateInstance($gcdType, @($actionTable))
$probe.Record("Clock", $PLAIN, $t0.AddSeconds(30), 1.0, $false, $false)
Check "the tracker's presses are in Unix milliseconds" $probe.StatsFor("Clock", $true).Trace[0].TimeMs (At 30) 0.001
Check "not in .NET ticks" $(if ([Math]::Abs($probe.StatsFor("Clock", $true).Trace[0].TimeMs - $t0.AddSeconds(30).Ticks / 10000.0) -gt 1e12) { 1 } else { 0 }) 1 0

# A clean 2.5s rotation, except that nothing was hittable from 60s to 107s and nobody pressed
# anything in that window.
$g = [Activator]::CreateInstance($gcdType, @($actionTable))
for ($s = 0.0; $s -lt 200; $s += 2.5) {
  if ($s -ge 60 -and $s -lt 107) { continue }
  $g.Record("Wolf", $PLAIN, $t0.AddSeconds($s), 1.0, $false, $false)
}
$st = $g.StatsFor("Wolf")
# The gap is 50s and the cast that opened it was charged its 2.5s recast, so 47.5s is lost.
Check "without windows the gap is all clip" $st.Clip 47.5 0.1
Check "and uptime suffers"                  ($st.Uptime * 100) 75.95 0.2

$changed = $g.SetDowntimeWindows((Windows @(,@((At 58), (At 107)))))
Check "windows changed"                     $(if ($changed) { 1 } else { 0 }) 1 0
$st = $g.StatsFor("Wolf")
Check "inside a window nothing is lost"     $st.Clip 0 0.01
Check "uptime back to 100%"                 ($st.Uptime * 100) 100 0.01
Check "span has the window taken out"       $st.ActiveSeconds (197.5 - 49) 0.01

Check "setting the same windows again changes nothing" `
  $(if ($g.SetDowntimeWindows((Windows @(,@((At 58), (At 107)))))) { 1 } else { 0 }) 0 0

""
"=============== 18a. a real clip outside the window still counts ==============="
$g = [Activator]::CreateInstance($gcdType, @($actionTable))
for ($s = 0.0; $s -lt 200; $s += 2.5) {
  if ($s -ge 60 -and $s -lt 107) { continue }   # the untargetable stretch
  if ($s -ge 150 -and $s -lt 160) { continue }  # ten seconds of standing still
  $g.Record("Wolf", $PLAIN, $t0.AddSeconds($s), 1.0, $false, $false)
}
$g.SetDowntimeWindows((Windows @(,@((At 58), (At 107))))) | Out-Null
$st = $g.StatsFor("Wolf")
Check "only the real pause is charged" $st.Clip 10 0.01
Check "uptime reflects it"             ($st.Uptime * 100) 93.27 0.2

""
"=============== 18b. overlapping windows are counted once ==============="
$g = [Activator]::CreateInstance($gcdType, @($actionTable))
for ($s = 0.0; $s -lt 100; $s += 2.5) { $g.Record("Dup", $PLAIN, $t0.AddSeconds($s), 1.0, $false, $false) }
$g.SetDowntimeWindows((Windows @(@((At 10), (At 20)), @((At 15), (At 25)), @((At 30), (At 35))))) | Out-Null
Check "merged, not summed"   ($g.DowntimeBetween((At 0), (At 100)) / 1000) 20 0.001
Check "partial overlap"      ($g.DowntimeBetween((At 12), (At 18)) / 1000) 6 0.001
Check "window outside range" ($g.DowntimeBetween((At 0), (At 5)) / 1000) 0 0.001
Check "empty range"          ($g.DowntimeBetween((At 50), (At 10)) / 1000) 0 0.001

$emptyWindows = [Array]::CreateInstance($windowType, 0)
Check "cleared" $(if ($g.SetDowntimeWindows($emptyWindows)) { $g.DowntimeBetween((At 0), (At 100)) } else { -1 }) 0 0.001

""
"=============== 18c. presses after the fight ended are not the fight's ==============="
# The parser keeps reporting a finished fight until the next one opens, and the record is only
# reset then - so whatever is pressed in between lands on the pull that just ended. A real one:
# a 9/25 M8S wipe, one meditation six seconds after the parser closed the fight, 18s "lost".
$g = [Activator]::CreateInstance($gcdType, @($actionTable))
for ($s = 0.0; $s -lt 100; $s += 2.5) { $g.Record("Monk", $PLAIN, $t0.AddSeconds($s), 1.0, $false, $false) }
$before = $g.StatsFor("Monk")
# The wipe at 101s, then two presses well after it - recorded before anyone has said the fight ended.
$g.Record("Monk", $PLAIN, $t0.AddSeconds(120), 1.0, $false, $false)
$g.Record("Monk", $PLAIN, $t0.AddSeconds(121), 1.0, $false, $false)
$st = $g.StatsFor("Monk")
Check "before the end is known, the gap is charged"  $st.Clip 20.0 0.1
$changed = $g.SetFightEnd([double](At 101))
Check "the fight's end changes the measurement"      $(if ($changed) { 1 } else { 0 }) 1 0
$st = $g.StatsFor("Monk")
Check "the late presses are not counted"             $st.Count $before.Count 0
Check "and are reported as such"                     $st.AfterEnd 2 0
Check "nothing is lost at the end of the pull"       $st.Clip 0 0.01
Check "uptime is the pull's"                         ($st.Uptime * 100) 100 0.01
Check "the span ends at the last press in the fight" $st.ActiveSeconds $before.ActiveSeconds 0.01
Check "the trace stops there too"                    $g.StatsFor("Monk", $true).Trace.Count $before.Count 0
Check "setting the same end again changes nothing"   $(if ($g.SetFightEnd([double](At 101))) { 1 } else { 0 }) 0 0
# A press at the very moment the fight ends is still the fight's.
$g2 = [Activator]::CreateInstance($gcdType, @($actionTable))
for ($s = 0.0; $s -le 100; $s += 2.5) { $g2.Record("Edge", $PLAIN, $t0.AddSeconds($s), 1.0, $false, $false) }
[void]$g2.SetFightEnd([double](At 100))
Check "a press at the very end is counted"           $g2.StatsFor("Edge").Count 41 0
# The next fight opens: no end again, and the presses count (the reset then throws them away).
Check "no end again is a change"                     $(if ($g.SetFightEnd($null)) { 1 } else { 0 }) 1 0
$st = $g.StatsFor("Monk")
Check "and the late presses count again"             $st.Count ($before.Count + 2) 0

""
""
if ($global:RdpsTestFailures -gt 0) {
    Write-Host "==> $($global:RdpsTestFailures) check(s) FAILED" -ForegroundColor Red
    exit 1
}
Write-Host "==> all checks passed" -ForegroundColor Green
