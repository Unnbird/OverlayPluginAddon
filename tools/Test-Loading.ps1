<#
.SYNOPSIS
    Checks that the packaged addon can actually start ClearScript when it is loaded the way ACT
    loads it.

.DESCRIPTION
    ACT loads a plugin from bytes it read itself, so the runtime never learns which folder the dll
    came from and will not probe it for anything the plugin depends on. Everything the addon needs
    beyond ACT and OverlayPlugin therefore has to ship in the package *and* be handed over by
    PrivateAssemblies at resolve time.

    A file missing from build.ps1's list does not degrade gracefully. It throws FileNotFoundException
    on the parser's own thread, and an unhandled exception on a background thread takes the whole of
    ACT down - which is what happened the first time this shipped. That is why this test exists and
    why it checks the packaged folder rather than the build output, which has every dependency in it
    whether the package ships it or not.

    Two cases, each in a child process so nothing this one has already loaded can mask a missing
    file:

      1. with the resolver   - the parser must start
      2. without it          - the parser must fail and the process must survive

    Requires a package.  .\build.ps1 -SkipDeps; .\tools\Test-Loading.ps1
#>
$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent $PSScriptRoot
$workspace = Split-Path -Parent $repo
$pkg = Join-Path $workspace "out\Release\package-OverlayPluginAddon\OverlayPluginAddon"
if (-not (Test-Path (Join-Path $pkg "OverlayPluginAddon.dll"))) {
    throw "Package not found in $pkg. Run .\build.ps1 -SkipDeps first."
}

$failures = 0
function Check($label, $actual, $expected) {
    $ok = "$actual" -eq "$expected"
    if (-not $ok) { $script:failures++ }
    "{0} {1,-46} actual={2,-10} expected={3}" -f $(if ($ok) { "PASS" } else { "FAIL" }), $label, $actual, $expected
}

# The child. Deliberately tiny and free of Add-Type: pre-loading anything would defeat the point.
$child = @'
param([string] $stage, [string] $withResolver)
$ErrorActionPreference = "Stop"
try {
    $asm = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes("$stage\OverlayPluginAddon.dll"))
    if ($withResolver -eq "yes") {
        # What InitPlugin does once ACT has told it where the dll came from.
        $asm.GetType("OverlayPluginAddon.PrivateAssemblies")::ResolveFrom($stage)
    }
    $hostType = $asm.GetType("OverlayPluginAddon.Fflogs.ParserHost")
    $parser = [Activator]::CreateInstance($hostType, @([string]"$stage\data\parser-ff.js", $null, [string]$stage))
    $started = $parser.Start([long]1767225600000, 1)
    $lines = 0
    if ($started) {
        $parser.Feed("00|2026-01-01T00:00:00.0000000+00:00|0|Test|")
        Start-Sleep -Milliseconds 900
        $lines = $parser.LinesParsed
    }
    $err = $parser.LastError
    $parser.Dispose()
    "RESULT started=$started lines=$lines"
    "ERROR $err"
}
catch {
    "RESULT threw=$($_.Exception.GetType().Name)"
    "ERROR $($_.Exception.Message)"
}
'@
$childPath = Join-Path $env:TEMP "OverlayPluginAddon-loadtest-$PID.ps1"
Set-Content -Path $childPath -Value $child -Encoding UTF8

function Run-Child { param([string] $resolver)
    # Run from a directory that is not the package, so nothing resolves by accident.
    $out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $childPath $pkg $resolver 2>&1
    return @{ Text = ($out | Out-String); Exit = $LASTEXITCODE }
}

""
"########## loading the package the way ACT does ##########"
"package: $pkg"

""
"=============== with the resolver: everything must load ==============="
$withResolver = Run-Child "yes"
$started = $withResolver.Text -match 'started=True'
$parsed = $withResolver.Text -match 'lines=[1-9]'
Check "the child process exited cleanly" $withResolver.Exit 0
Check "the parser started" $started "True"
Check "and parsed a line" $parsed "True"
if (-not $started) {
    Write-Host "  -- child output --" -ForegroundColor Yellow
    $withResolver.Text.Trim() -split "`n" | ForEach-Object { "     $_" }
    Write-Host "  A missing file here is almost certainly one build.ps1 does not copy." -ForegroundColor Yellow
}

""
"=============== without it: must fail, must not take the process down ==============="
# The regression this half guards is not the failure - it is what the failure used to do. Reporting
# a ClearScript problem must not itself need ClearScript, or the catch block throws on a background
# thread and ACT dies with it.
$without = Run-Child "no"
$refused = $without.Text -match 'started=False'
$survived = $without.Exit -eq 0
Check "the parser refused to start" $refused "True"
Check "the process survived" $survived "True"
Check "and said why" ($without.Text -match 'ClearScript') "True"

Remove-Item $childPath -Force -ErrorAction SilentlyContinue

""
""
if ($failures -gt 0) {
    Write-Host "==> $failures check(s) FAILED" -ForegroundColor Red
    exit 1
}
Write-Host "==> all checks passed" -ForegroundColor Green
