<#
.SYNOPSIS
    Checks ActionCategories against the real parser resource assembly.

.DESCRIPTION
    GCD uptime reads its weaponskill/spell list out of FFXIV_ACT_Plugin.Resource, which ships
    Costura-compressed inside the parser and is only unpacked when something asks for it. Two
    things can go wrong and both show up as a column of zeroes:

      - the assembly is not in the AppDomain yet, and we give up instead of asking for it
      - the resource inside it is found but parsed into nothing

    This covers the second directly, and the first by asserting the failure is reported rather
    than swallowed.

.EXAMPLE
    .\build.ps1 -SkipDeps; .\tools\Test-ActionCategories.ps1
#>
$ErrorActionPreference = "Stop"

# Paths are derived from this script rather than hardcoded, so the suite runs from a clone in any
# location - a CI checkout included - and not only on the machine it was written on.
$repo = Split-Path -Parent $PSScriptRoot
$workspace = Split-Path -Parent $repo
$dir = Join-Path $workspace "out\Release\OverlayPluginAddon\net48"

# The parser resource assembly. fetch_deps.ps1 unpacks the FFXIV_ACT_Plugin SDK beside
# OverlayPlugin, which is where a CI checkout finds it; a local ACT plugin build is the fallback.
$resourceDll = Join-Path $workspace "OverlayPlugin\Thirdparty\FFXIV_ACT_Plugin\SDK\FFXIV_ACT_Plugin.Resource.dll"
if (-not (Test-Path $resourceDll)) {
    $resourceDll = Join-Path $workspace "FF14ACT\FFXIV_ACT_Plugin\bin\Release\FFXIV_ACT_Plugin.Resource.dll"
}

# The resolver below outlives this script: AppDomain handlers stay registered for the life of
# the process, so running two suites back to back leaves the first one still subscribed. It
# reads the output directory out of a global for that reason - a script-scoped $dir is gone
# by then, and the stale handler would fire with a null path.
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
$type = $asm.GetType("OverlayPluginAddon.ActionCategories")

$failures = 0
function Check($label, $ok, $detail) {
    if (-not $ok) { $script:failures++ }
    "{0} {1,-52} {2}" -f $(if($ok){"PASS"}else{"FAIL"}), $label, $detail
}

"=============== resource assembly absent ==============="
# Nothing has loaded FFXIV_ACT_Plugin.Resource in this process and there is no Costura resolver,
# so this must fail loudly rather than pretending to have an empty table.
$absent = $type.GetMethod("Load").Invoke($null, @())
Check "reports unavailable" (-not $absent.Available) "Available=$($absent.Available)"
Check "explains why"        ($absent.LoadError -ne $null -and $absent.LoadError.Length -gt 0) "'$($absent.LoadError)'"
Check "no phantom actions"  ($absent.Count -eq 0) "Count=$($absent.Count)"
""

"=============== resource assembly present ==============="
# Standing in for what Costura does at runtime.
[void][Reflection.Assembly]::Load([IO.File]::ReadAllBytes($resourceDll))
$present = $type.GetMethod("Load").Invoke($null, @())
Check "loads"                $present.Available "Available=$($present.Available), error='$($present.LoadError)'"
Check "found the GCD list"   ($present.Count -gt 30000) "Count=$($present.Count)"
""

"=============== classification spot checks ==============="
# Ids taken from ActionCategoryList itself; see the category numbers in ActionCategories.cs.
$isGcd = $type.GetMethod("IsGcd")
$cases = @(
    @{ id = 0x3E75; name = "Cascade (DNC weaponskill)";     gcd = $true  },
    @{ id = 0x3E76; name = "Fountain (DNC weaponskill)";    gcd = $true  },
    @{ id = 0xDF9;  name = "Fire IV (BLM spell)";           gcd = $true  },
    @{ id = 0x6503; name = "Glare III (WHM spell)";         gcd = $true  },
    @{ id = 0x3E87; name = "Fan Dance (oGCD ability)";      gcd = $false },
    @{ id = 0xDE5;  name = "Battle Litany (oGCD ability)";  gcd = $false },
    @{ id = 0x8D2;  name = "Trick Attack (oGCD ability)";   gcd = $false },
    @{ id = 0x7;    name = "attack (auto-attack)";          gcd = $false }
)
foreach ($c in $cases) {
    $actual = $isGcd.Invoke($present, @([uint32]$c.id))
    Check $c.name ($actual -eq $c.gcd) "IsGcd=$actual expected=$($c.gcd)"
}

""
"=============== what the category table gets wrong, and actions.json puts right ==============="
# FFXIV files a Ninja's mudras and every Ninjutsu under category 4, "Ability" - correctly, they
# are not weaponskills - yet they roll the GCD (0.5s, 1.5s). The category table alone therefore
# cannot be the GCD test; EventSource.IsGcdAction asks xivanalysis' onGcd list first. This section
# pins the disagreement so a resource update that changes it is noticed.
$actionData = $asm.GetType("OverlayPluginAddon.ActionData").GetMethod("Load").Invoke($null, @([string](Join-Path $repo "data\actions.json")))
$disagreements = @(
    @{ id = 0x8D3;  name = "Ten (mudra)" },
    @{ id = 0x8D5;  name = "Chi (mudra)" },
    @{ id = 0x8DB;  name = "Raiton (Ninjutsu)" },
    @{ id = 0x49B9; name = "Fuma Shuriken (Ten Chi Jin)" },
    @{ id = 0x904E; name = "Forbidden Meditation (Monk)" }
)
foreach ($d in $disagreements) {
    $byCategory = $isGcd.Invoke($present, @([uint32]$d.id))
    $byXiva = $actionData.IsOnGcd([uint32]$d.id)
    Check "$($d.name): category says oGCD" (-not $byCategory) "IsGcd=$byCategory"
    Check "$($d.name): actions.json says GCD" $byXiva "IsOnGcd=$byXiva"
}

""
if ($failures -gt 0) {
    Write-Host "==> $failures check(s) FAILED" -ForegroundColor Red
    exit 1
}
Write-Host "==> all checks passed" -ForegroundColor Green
