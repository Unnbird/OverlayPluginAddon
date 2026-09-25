param (
    # Configuration to package. Debug is only useful for local troubleshooting.
    [string]$Configuration = "Release",

    # Continuous integration mode: build Debug first so debug-only breakage gets caught too.
    [switch]$ci = $false,

    # Git ref of OverlayPlugin/OverlayPlugin to clone when no source tree is present yet.
    # Defaults to whatever GitHub currently reports as the latest release.
    [string]$OverlayPluginRef = "",

    # Assume OverlayPlugin's Thirdparty/ and the stripped FFXIVClientStructs are already in place.
    [switch]$SkipDeps = $false
)

$ErrorActionPreference = "Stop"

$OVERLAYPLUGIN_API = "https://api.github.com/repos/OverlayPlugin/OverlayPlugin/releases/latest"
$OVERLAYPLUGIN_URL = "https://github.com/OverlayPlugin/OverlayPlugin.git"

# OverlayPluginAddon.csproj references ..\..\OverlayPlugin\* and writes to ..\..\out\, so this
# repository is expected to sit beside the OverlayPlugin checkout:
#   <workspace>/OverlayPluginAddon                    <- this repository
#   <workspace>/OverlayPluginAddon/OverlayPluginAddon  <- plugin source (this csproj's folder)
#   <workspace>/OverlayPluginAddon/data                 <- action recast table shipped beside the dll
#   <workspace>/OverlayPlugin                    <- OverlayPlugin source
#   <workspace>/out                              <- build output
$repo = $PSScriptRoot
$workspace = Split-Path -Parent $repo
$opDir = Join-Path $workspace "OverlayPlugin"
$project = Join-Path $repo "OverlayPluginAddon\OverlayPluginAddon.csproj"

# The FFXIV_ACT_Plugin SDK pinned by OverlayPlugin v0.19.105's DEPS.json is gone: ravahn keeps only
# a rolling window of SDK archives in that repo's Releases/ folder and has since deleted 2.0.6.1,
# so its URL now 404s. fetch_deps.ps1 downloads with Invoke-WebRequest -OutFile, which surfaces
# that as "The request was aborted: The connection was closed unexpectedly" rather than as a plain
# 404 - the error every fresh checkout and every CI run hits at "[2/6]: FFXIV_ACT_Plugin".
#
# Repin it to an archive that still exists. Only FFXIV_ACT_Plugin.Common.dll is ever referenced
# (OverlayPlugin.Core and this addon both HintPath it out of SDK/), and 3.0.2.8 lays its zip out
# exactly the way 2.0.6.1 did, so the entry's "strip": 0 and dest still land it in
# Thirdparty/FFXIV_ACT_Plugin/SDK/.
#
# The rewrite is keyed on the dead version string, so it stops applying by itself the day
# OverlayPlugin repins upstream. Delete this function then.
$BROKEN_SDK = "FFXIV_ACT_Plugin_SDK_2.0.6.1.zip"
$BROKEN_SDK_HASH = "ed98fa01ec2c7ed2ac843fa8ea2eec1d66f86fad4c6864de4d73d5cfbf163dce"
$PINNED_SDK = "FFXIV_ACT_Plugin_SDK_3.0.2.8.zip"
$PINNED_SDK_HASH = "1c7f9425dc4fdd25cc368256dc72968ad9551158941600fd8dadfb5b74f94bbf"

function Repair-BrokenSdkPin($depsPath) {
    if (-not (Test-Path $depsPath)) { return }

    $deps = [System.IO.File]::ReadAllText($depsPath)
    if (-not $deps.Contains($BROKEN_SDK)) { return }

    echo "==> Repinning the dead FFXIV_ACT_Plugin SDK to $PINNED_SDK..."
    $deps = $deps.Replace($BROKEN_SDK, $PINNED_SDK).Replace($BROKEN_SDK_HASH, $PINNED_SDK_HASH)
    # Written without a BOM: fetch_deps.ps1 hands the text straight to Newtonsoft, which rejects one.
    [System.IO.File]::WriteAllText($depsPath, $deps, (New-Object System.Text.UTF8Encoding($false)))
}

function Get-AssemblyVersion($propsPath) {
    [xml]$props = Get-Content -Path $propsPath
    $version = ($props.Project.PropertyGroup.AssemblyVersion | Out-String).Trim()
    if (-not $version) { throw "No AssemblyVersion found in $propsPath" }
    return $version
}

Push-Location $workspace
try {
    # An existing checkout is left alone - it may hold local work.
    if (-not (Test-Path (Join-Path $opDir "OverlayPlugin.sln"))) {
        if (-not $OverlayPluginRef) {
            if ($env:OVERLAYPLUGIN_REF) {
                $OverlayPluginRef = $env:OVERLAYPLUGIN_REF
            }
            else {
                echo "==> Resolving latest OverlayPlugin release..."
                $OverlayPluginRef = (Invoke-RestMethod -Uri $OVERLAYPLUGIN_API -Headers @{ "User-Agent" = "OverlayPluginAddon-build" }).tag_name
                if (-not $OverlayPluginRef) { throw "Failed to resolve latest OverlayPlugin release" }
            }
        }
        echo "==> Cloning OverlayPlugin $OverlayPluginRef..."
        git clone --depth 1 --branch $OverlayPluginRef $OVERLAYPLUGIN_URL $opDir
        if ($LASTEXITCODE -ne 0) { throw "Failed to clone OverlayPlugin" }
    }
    else {
        echo "==> Using existing OverlayPlugin checkout"
    }

    if (-not $SkipDeps) {
        Push-Location $opDir
        try {
            # Rewriting DEPS.json before the fetch is also what makes the fetch happen:
            # fetch_deps compares the two files, so the changed hash marks that one entry
            # outdated and it re-downloads instead of trusting a stale Thirdparty/.
            Repair-BrokenSdkPin (Join-Path $opDir "DEPS.json")

            # Downloads the ACT / FFXIV_ACT_Plugin SDK / FFXIVClientStructs archives our
            # HintPaths point at. No-ops while DEPS.cache still matches DEPS.json.
            echo "==> Fetching OverlayPlugin dependencies..."
            & .\tools\fetch_deps.ps1

            # OverlayPlugin.Core compiles the *stripped* copies of FFXIVClientStructs;
            # without them Core doesn't build, and Core is our ProjectReference.
            $structsBase = Get-Item "OverlayPlugin.Core\Thirdparty\FFXIVClientStructs\Base" -ErrorAction SilentlyContinue
            $structsOut = Get-Item "OverlayPlugin.Core\Thirdparty\FFXIVClientStructs\Transformed" -ErrorAction SilentlyContinue
            if ($structsBase -and (-not $structsOut -or $structsOut.LastWriteTime -lt $structsBase.LastWriteTime)) {
                echo "==> Stripping FFXIVClientStructs..."
                & .\tools\strip-clientstructs.ps1
            }
        }
        finally {
            Pop-Location
        }
    }

    # FetchDeps/StripClientStructs are MSBuild targets of OverlayPlugin's VSBuildDeps
    # project. They rely on $(SolutionDir), which is undefined when building a bare csproj
    # like ours, so they run above instead and stay out of the build itself.
    $buildArgs = @("-p:FetchDeps=false", "-p:StripClientStructs=false")

    if ($ci) {
        echo "==> Continuous integration flag set. Building Debug..."
        dotnet publish $project -c Debug @buildArgs
        if ($LASTEXITCODE -ne 0) { throw "Debug build failed" }
    }

    echo "==> Building $Configuration..."
    dotnet publish $project -c $Configuration @buildArgs
    if ($LASTEXITCODE -ne 0) { throw "$Configuration build failed" }

    echo "==> Building archive..."

    $version = Get-AssemblyVersion (Join-Path $repo "Directory.Build.props")
    $outDir = Join-Path $workspace "out\$Configuration\OverlayPluginAddon\net48"
    $stage = Join-Path $workspace "out\$Configuration\package-OverlayPluginAddon"
    $pkg = Join-Path $stage "OverlayPluginAddon"

    $dll = Join-Path $outDir "OverlayPluginAddon.dll"
    if (-not (Test-Path $dll)) { throw "Build output not found: $dll" }

    if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
    New-Item -ItemType Directory -Path $pkg | Out-Null

    # An OverlayPlugin addon ships only its own assembly plus its data files; every
    # third-party dependency is already loaded by OverlayPlugin itself.
    Copy-Item $dll $pkg
    if ($Configuration -eq "Debug") {
        $pdb = Join-Path $outDir "OverlayPluginAddon.pdb"
        if (Test-Path $pdb) { Copy-Item $pdb $pkg }
    }

    # ClearScript is the exception to the line above: OverlayPlugin has no JavaScript engine of its
    # own, so V8 and everything it needs has to ship with us.
    #
    # ACT loads a plugin from bytes it read itself, so the runtime never learns which folder the dll
    # came from and will not probe it - PrivateAssemblies.cs hands these over by name at resolve
    # time. Which means a file missing from this list does not degrade, it throws on the parser's
    # own thread, and that used to take the whole of ACT down. The set below was verified by loading
    # the dll from bytes with nothing else on the probing path (see the README's build section);
    # the last four are transitive and were not obvious.
    #
    # They go in lib/ rather than beside the dll. PrivateAssemblies loads them from a copy under
    # .shadow/, so the files the updater overwrites are never the locked ones - and the new folder
    # is what lets a release that still loads them from the top level update to this one at all,
    # since the archive no longer holds a single file that release has locked.
    #
    # Keep lib/ itself flat: ClearScript finds ClearScriptV8.win-x64.dll beside its own assembly.
    $lib = Join-Path $pkg "lib"
    New-Item -ItemType Directory -Path $lib | Out-Null
    $private = @(
        "ClearScript.Core.dll",
        "ClearScript.V8.dll",
        "ClearScriptV8.win-x64.dll",
        "ClearScript.V8.ICUData.dll",
        "Microsoft.Bcl.AsyncInterfaces.dll",
        "System.Threading.Tasks.Extensions.dll",
        "System.Runtime.CompilerServices.Unsafe.dll",
        "System.Memory.dll",
        "System.Buffers.dll",
        "System.Numerics.Vectors.dll",
        "System.ValueTuple.dll",
        # OverlayPlugin has this loaded already, but only because it happens to; ClearScript needs
        # it and nothing guarantees the order.
        "Newtonsoft.Json.dll"
    )
    foreach ($file in $private) {
        $path = Join-Path $outDir $file
        if (-not (Test-Path $path)) { throw "Dependency missing from the build output: $path" }
        Copy-Item $path $lib
    }

    # EventSource looks for data/actions.json and data/parser-ff.js beside the dll first, so the
    # whole data directory has to ship - FFLogs' parser included.
    $data = Join-Path $repo "data"
    if (-not (Test-Path $data)) { throw "data/ directory not found: $data" }
    Copy-Item -Recurse $data (Join-Path $pkg "data")

    $suffix = if ($Configuration -eq "Release") { "" } else { "-$Configuration" }
    $archive = Join-Path $workspace "out\OverlayPluginAddon-$version$suffix.zip"
    if (Test-Path $archive) { Remove-Item $archive -Force }
    # Compress-Archive uses backslash separators on Windows, but OverlayPlugin's
    # Installer.Extract splits entry keys on '/' only.  Use System.IO.Compression
    # directly so every entry uses forward slashes.
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $zipStream = [System.IO.File]::Create($archive)
    $zip = New-Object System.IO.Compression.ZipArchive($zipStream, [System.IO.Compression.ZipArchiveMode]::Create)

    $pkgParent = Split-Path -Parent $pkg   # = .../package
    Get-ChildItem -Recurse -File $pkg | ForEach-Object {
        # Produce "OverlayPluginAddon/OverlayPluginAddon.dll" style entries
        $rel = $_.FullName.Substring($pkgParent.Length + 1).Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $zip, $_.FullName, $rel,
            [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }

    $zip.Dispose()
    $zipStream.Dispose()

    echo "==> Done: $archive"
}
finally {
    Pop-Location
}
