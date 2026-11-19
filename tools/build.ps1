#
# DPT.Engine - build and verify.
# Copyright (C) 2026 DartPower Team. GPLv2.
#
# One command that does what "before you commit" means for this repository:
# build everything, run the unit tests, run the engine's headless verification
# and the master's self-test. Each step is a real check, not a smoke test.
#
#   .\tools\build.ps1              build and verify (Debug)
#   .\tools\build.ps1 -Release     build and verify (Release)
#   .\tools\build.ps1 -Pack        also stage a runnable distribution
#   .\tools\build.ps1 -SkipTests   build only
#
[CmdletBinding()]
param(
    [switch] $Release,
    [switch] $Pack,
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$configuration = if ($Release) { 'Release' } else { 'Debug' }
$framework = 'net40'

$failures = @()

function Write-Step([string] $text) {
    Write-Host ''
    Write-Host "== $text" -ForegroundColor Cyan
}

function Invoke-Step([string] $name, [scriptblock] $body) {
    try {
        & $body
        if ($LASTEXITCODE -ne 0 -and $null -ne $LASTEXITCODE) {
            throw "exit code $LASTEXITCODE"
        }
    }
    catch {
        Write-Host "FAILED: $name - $_" -ForegroundColor Red
        $script:failures += $name
    }
}

function Get-Executable([string] $project, [string] $name) {
    return Join-Path $root "src\$project\bin\$configuration\$framework\$name.exe"
}

Push-Location $root

try {
    Write-Step "Building the solution ($configuration)"

    Invoke-Step 'build' {
        dotnet build DPTEngine.slnx -c $configuration -v q --nologo
    }

    if (-not $SkipTests -and $failures.Count -eq 0) {
        Write-Step 'Unit tests'

        Invoke-Step 'unit tests' {
            $exe = Join-Path $root "tests\DPT.Engine.Tests\bin\$configuration\$framework\dptengine-tests.exe"

            if (-not (Test-Path $exe)) { throw "missing $exe" }

            & $exe

            if ($LASTEXITCODE -ne 0) { throw "unit tests reported failures" }
        }

        Write-Step 'Engine self-test'

        Invoke-Step 'self-test' {
            $exe = Get-Executable 'DPT.Engine.SelfTest' 'dptengine-selftest'

            if (-not (Test-Path $exe)) { throw "missing $exe" }

            & $exe

            if ($LASTEXITCODE -ne 0) { throw "self-test reported failures" }
        }

        Write-Step 'Headless verification (built-in template)'

        Invoke-Step 'headless' {
            $exe = Get-Executable 'DPT.Engine' 'dptengine'

            if (-not (Test-Path $exe)) { throw "missing $exe" }

            & $exe --headless

            if ($LASTEXITCODE -ne 0) { throw "headless verification reported failures" }
        }

        Write-Step 'Master self-test'

        Invoke-Step 'master' {
            $exe = Get-Executable 'DPT.Engine.Master' 'dptengine-master'

            if (-not (Test-Path $exe)) { throw "missing $exe" }

            & $exe --self-test

            if ($LASTEXITCODE -ne 0) { throw "master self-test reported failures" }
        }
    }

    if ($Pack -and $failures.Count -eq 0) {
        Write-Step "Staging the distribution"

        Invoke-Step 'pack' {
            $dist = Join-Path $root "dist\$framework"

            if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
            New-Item -ItemType Directory -Force -Path $dist | Out-Null
            New-Item -ItemType Directory -Force -Path (Join-Path $dist 'Projects') | Out-Null

            foreach ($pair in @(
                @('DPT.Engine', 'dptengine'),
                @('DPT.Engine.Editor', 'dptengine-editor'),
                @('DPT.Engine.Launcher', 'dptengine-launcher'),
                @('DPT.Engine.Master', 'dptengine-master'))) {
                $source = Get-Executable $pair[0] $pair[1]

                if (-not (Test-Path $source)) { throw "missing $source" }

                Copy-Item $source $dist

                # Everything beside the executable too.
                #
                # The runtime references DPT.Engine.Shared as an assembly, so copying
                # only the .exe produced a distribution that threw
                # FileNotFoundException on start-up - the classic "the DLL went
                # missing from the package" failure. Copying the whole output folder
                # is the deployment the build actually produces.
                $folder = Split-Path -Parent $source

                Get-ChildItem $folder -File |
                    Where-Object { $_.Extension -in @('.dll', '.config') } |
                    ForEach-Object { Copy-Item $_.FullName $dist -Force }
            }

            Copy-Item (Join-Path $root 'README.md') $dist
            Copy-Item (Join-Path $root 'LICENSE') $dist

            Write-Host "Distribution staged in $dist"
        }
    }
}
finally {
    Pop-Location
}

Write-Host ''

if ($failures.Count -gt 0) {
    Write-Host ('FAILED: ' + ($failures -join ', ')) -ForegroundColor Red
    exit 1
}

Write-Host 'Everything passed.' -ForegroundColor Green
exit 0
