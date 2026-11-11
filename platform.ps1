<#
.SYNOPSIS
Start the samples, generate or preview documentation, or run the Docker test suite.

.DESCRIPTION
Single entry point for the solution's isolated Docker Compose stacks.

.EXAMPLE
.\platform.ps1 start
Start and rebuild the dashboard, database and samples.

.EXAMPLE
.\platform.ps1 start -Clean
Wipe the development stack's volumes first, then start fresh.

.EXAMPLE
.\platform.ps1 test
Run the baseline suite (unit + integration + E2E).

.EXAMPLE
.\platform.ps1 test e2e -SlowMo 0
Run E2E flows only at full speed.

.EXAMPLE
.\platform.ps1 setup
Link .claude/skills to .agents/skills so Claude Code discovers the shared skills.

.EXAMPLE
.\platform.ps1 purge
Remove every container, image, volume, and build cache on this machine, then reset Docker Desktop to factory defaults.
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('start', 'stop', 'status', 'test', 'docs', 'setup', 'purge', 'help')]
    [string] $Command = 'help',

    # test suite or docs action.
    [Parameter(Position = 1)]
    [ValidateSet('all', 'unit', 'e2e', 'start', 'generate')]
    [string] $Suite = 'all',

    # start: wipe this project's volumes first.
    [switch] $Clean,

    # test: leave the test stack up afterwards to inspect it in Aspire.
    [switch] $Keep,
    # test: Playwright SlowMo in ms (compose default 500).
    [int] $SlowMo = -1,
    # test: do not record the per-test videos.
    [switch] $NoVideo,

    # start/test: skip image builds and use what is already built.
    [switch] $NoBuild,
    # docs start: loopback port for the preview.
    [ValidateRange(1, 65535)]
    [int] $Port = 4321
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$DevProject = 'sounding'
$SamplesProject = 'sounding-samples'
$CaptureProject = 'sounding-docs'
$SiteProject = 'sounding-docs-site'
$ManagedProjects = @($DevProject, $SamplesProject, 'sounding-examples', $CaptureProject, $SiteProject)
$TestProject = 'sounding-test'
$EnvFile = '.env'
$DevFiles = @('docker-compose.yml')
$TestFiles = @('docker-compose.yml', 'docker-compose.test.yml')
$TestRunner = 'platform-test-runner'
$HasGpu = $false

# ── Helpers ──────────────────────────────────────────────────────────

function Write-Step([string] $Message) {
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

# -Query marks a read-only call whose output is captured and not echoed.
function Invoke-Docker {
    param([string[]] $Arguments, [switch] $AllowFailure, [switch] $Query)

    if (-not $Query) {
        Write-Host "> docker $($Arguments -join ' ')" -ForegroundColor DarkGray
    }

    & docker @Arguments
    if ($LASTEXITCODE -ne 0 -and -not $AllowFailure) {
        throw "docker exited with code $LASTEXITCODE"
    }
}

function Test-ProjectExists([string] $Project) {
    $ids = Invoke-Docker -Query @('ps', '-aq', '--filter', "label=com.docker.compose.project=$Project")
    return [bool]$ids
}

function Get-DevCompose {
    $compose = @('compose', '--env-file', $EnvFile, '-p', $DevProject)
    foreach ($file in $DevFiles) { $compose += @('-f', $file) }
    return $compose
}

function Test-NvidiaGpu {
    if (-not (Get-Command nvidia-smi -ErrorAction SilentlyContinue)) { return $false }
    & nvidia-smi -L *> $null
    return $LASTEXITCODE -eq 0
}

function Get-SamplesCompose {
    $compose = @('compose', '--env-file', $EnvFile, '-p', $SamplesProject, '-f', 'samples/docker-compose.yml')
    if ($script:HasGpu) { $compose += @('--profile', 'gpu') }
    return $compose
}

function Get-CaptureCompose {
    return @('compose', '-p', $CaptureProject, '-f', 'docs/product/screenshots/compose.yaml')
}

function Get-SiteCompose {
    return @('compose', '-p', $SiteProject, '-f', 'docs/product/site/compose.yaml')
}

function Get-TestCompose {
    # Committed non-secret defaults; CI process environment can override them.
    $compose = @('compose', '--env-file', 'example.env', '-p', $TestProject)
    foreach ($file in $TestFiles) { $compose += @('-f', $file) }
    $compose += @('--profile', 'migrate')
    return $compose
}

function Stop-TestStack {
    Invoke-Docker ((Get-TestCompose) + @('down', '--volumes', '--remove-orphans'))
}

function Assert-EnvFile {
    if (-not (Test-Path $EnvFile)) {
        throw "$EnvFile not found in $PWD. Copy example.env to $EnvFile and set SOUNDING_DB_PASSWORD."
    }
}

function Show-DevUrls {
    Write-Host ''
    Write-Host '  Dashboard         http://localhost:8080'
}

function Stop-Project([string] $Project) {
    $ids = @(Invoke-Docker -Query @('ps', '-q', '--filter', "label=com.docker.compose.project=$Project"))
    if ($ids.Count -gt 0) {
        Write-Step "Stopping $Project"
        Invoke-Docker (@('stop') + $ids)
    }
}

# ── Commands ─────────────────────────────────────────────────────────

function Invoke-Start {
    Assert-EnvFile
    $script:HasGpu = Test-NvidiaGpu
    $compose = Get-DevCompose
    $samples = Get-SamplesCompose

    # The samples directory was previously named examples; don't leave its published ports occupied.
    Stop-Project 'sounding-examples'

    if ($Clean) {
        Write-Step 'Removing the samples and their volumes'
        Invoke-Docker ($samples + @('down', '--volumes', '--remove-orphans'))
        Write-Step 'Removing the dashboard stack and its volumes'
        Invoke-Docker ($compose + @('down', '--volumes', '--remove-orphans'))
    }

    $up = @('up', '-d', '--remove-orphans')
    if ($NoBuild) { $up += '--no-build' } else { $up += '--build' }

    Write-Step 'Starting the dashboard and database'
    Invoke-Docker ($compose + $up + @('db', 'dashboard'))
    Write-Step $(if ($script:HasGpu) { 'Starting the samples, with ollama on the NVIDIA GPU' } else { 'Starting the samples (no NVIDIA GPU: ollama skipped)' })
    Invoke-Docker ($samples + $up)

    Write-Step 'Samples are up'
    Show-DevUrls
}

function Invoke-Stop {
    foreach ($project in $ManagedProjects) { Stop-Project $project }
}

function Invoke-Status {
    foreach ($project in $ManagedProjects) {
        Write-Step "Project: $project"
        if (-not (Test-ProjectExists $project)) { Write-Host '  not running'; continue }
        Invoke-Docker -Query @('ps', '-a', '--filter', "label=com.docker.compose.project=$project",
            '--format', 'table {{.Label "com.docker.compose.service"}}\t{{.Status}}\t{{.Ports}}')
    }
}

function Invoke-Docs {
    switch ($Suite) {
        'start' {
            $index = Join-Path $PSScriptRoot 'docs/product/site/dist/index.html'
            if (-not (Test-Path -LiteralPath $index -PathType Leaf)) {
                throw 'Documentation site not found. Run: .\platform.ps1 docs generate'
            }
            Write-Step 'Starting the documentation preview'
            $compose = Get-SiteCompose
            $build = if ($NoBuild) { '--no-build' } else { '--build' }
            Invoke-Docker ($compose + @('up', '-d', '--no-deps', $build, '--force-recreate', 'preview'))
            Write-Host "  Documentation     http://localhost:$Port/sounding/"
        }
        'generate' {
            $compose = Get-CaptureCompose
            $failure = $null
            try {
                Write-Step 'Capturing documentation screenshots'
                Invoke-Docker ($compose + @('up', '--build', '--abort-on-container-exit', '--exit-code-from', 'capture'))
            }
            catch { $failure = $_ }
            finally {
                try {
                    Invoke-Docker ($compose + @('down', '--remove-orphans'))
                }
                catch {
                    if ($failure) { Write-Warning "Documentation cleanup failed: $($_.Exception.Message)" }
                    else { $failure = $_ }
                }
            }
            if ($failure) { throw $failure }

            Write-Step 'Generating the documentation site'
            Invoke-Docker ((Get-SiteCompose) + @('run', '--build', '--rm', '--no-deps', 'build'))
            Write-Host "Documentation is ready to publish: $(Join-Path $PSScriptRoot 'docs/product/site/dist')"
        }
        default { throw 'Choose a documentation action: .\platform.ps1 docs start or .\platform.ps1 docs generate' }
    }
}

function Invoke-Test {
    $compose = Get-TestCompose

    $services = @(Invoke-Docker -Query ($compose + @('--profile', '*', 'config', '--services')))
    if ($TestRunner -notin $services) {
        throw "No $TestRunner service is defined. Add the solution test runner before running tests; no stacks were changed."
    }

    Write-Step 'Resetting the test stack'
    Stop-TestStack

    if ($SlowMo -ge 0) { $env:PLAYWRIGHT_SLOW_MO = "$SlowMo" }
    if ($NoVideo) { $env:PLAYWRIGHT_RECORD_VIDEO = 'false' }

    $failure = $null
    try {
        Write-Step 'Starting the test stack'
        $up = @('up', '-d')
        if (-not $NoBuild) { $up += '--build' }
        Invoke-Docker ($compose + $up)

        Write-Step "Running the suite: $Suite"
        $run = @('run', '--rm')
        if (-not $NoBuild) { $run += '--build' }
        $run += @($TestRunner, './run-tests.sh', $Suite)
        Invoke-Docker ($compose + $run) -AllowFailure
        # Script scope rather than a return value: everything docker prints here is function output too.
        $script:exitCode = $LASTEXITCODE
    }
    catch {
        $failure = $_
    }
    finally {
        try {
            if ($Keep) {
                Write-Step 'Test stack retained for inspection (services may be partially started)'
                $dashboard = Invoke-Docker -Query -AllowFailure ($compose + @('port', 'platform-otel-dashboard', '18888')) 2>$null
                if ($dashboard) { Write-Host "  Aspire            http://$dashboard" }
                Write-Host '  Tear it down with: .\platform.ps1 stop'
            }
            else {
                Write-Step 'Tearing the test stack down'
                Stop-TestStack
            }
        }
        catch {
            # Cleanup must not replace the startup exception or the test runner's failure code.
            if ($failure -or $script:exitCode -ne 0) {
                Write-Warning "Test stack finalization failed: $($_.Exception.Message)"
            }
            else {
                $failure = $_
            }
        }
    }
    if ($failure) { throw $failure }

    Write-Host ''
    if ($script:exitCode -eq 0) {
        Write-Host "PASSED ($Suite)" -ForegroundColor Green
    }
    else {
        Write-Host "FAILED ($Suite, exit $($script:exitCode)) — evidence in test-results/" -ForegroundColor Red
    }
}

function Invoke-Setup {
    $link = Join-Path $PSScriptRoot '.claude/skills'
    $target = Join-Path $PSScriptRoot '.agents/skills'
    $existing = Get-Item $link -Force -ErrorAction SilentlyContinue
    if ($existing -and $existing.LinkType) { Write-Host ".claude/skills already links to $($existing.Target)"; return }
    if ($existing) { throw '.claude/skills exists as a real directory; move its skills into .agents/skills, remove it, and rerun setup.' }

    # Junctions need no elevation or Developer Mode on Windows.
    $type = if ($env:OS -eq 'Windows_NT') { 'Junction' } else { 'SymbolicLink' }
    Write-Step "Linking .claude/skills to .agents/skills ($type)"
    New-Item -ItemType Directory -Force (Split-Path $link) | Out-Null
    New-Item -ItemType $type -Path $link -Target $target | Out-Null
    Write-Host '  Restart Claude Code sessions to discover the skills.'
}

function Invoke-External([string] $Display, [scriptblock] $Action) {
    Write-Host "> $Display" -ForegroundColor DarkGray
    & $Action
}

function Invoke-Purge {
    Write-Step 'Purge: every container, image, volume and build cache on this machine, from this project or not, then Docker Desktop back to factory defaults'

    # A broken engine is a reason to purge, so nothing here may stop the reset below.
    $containers = @(Invoke-Docker -Query -AllowFailure @('ps', '-aq') 2>$null)
    if ($containers) { Invoke-Docker (@('rm', '--force', '--volumes') + $containers) -AllowFailure }
    Invoke-Docker @('system', 'prune', '--all', '--volumes', '--force') -AllowFailure
    $volumes = @(Invoke-Docker -Query -AllowFailure @('volume', 'ls', '-q') 2>$null)
    if ($volumes) { Invoke-Docker (@('volume', 'rm', '--force') + $volumes) -AllowFailure }
    Invoke-Docker @('builder', 'prune', '--all', '--force') -AllowFailure

    Write-Step 'Resetting Docker Desktop to factory defaults'
    Invoke-Docker @('desktop', 'stop', '--force') -AllowFailure
    # Whatever is still running would keep the files below locked.
    Invoke-External 'Stop-Process "Docker Desktop", com.docker.*' {
        Get-Process -Name 'Docker Desktop', 'com.docker.backend', 'com.docker.build', 'com.docker.sailor' -ErrorAction SilentlyContinue |
            Stop-Process -Force -ErrorAction SilentlyContinue
    }
    Invoke-External 'wsl --shutdown' { wsl --shutdown }
    $distros = @((wsl --list --quiet) -replace "`0", '' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    foreach ($distro in @('docker-desktop', 'docker-desktop-data') | Where-Object { $_ -in $distros }) {
        Invoke-External "wsl --unregister $distro" { wsl --unregister $distro | Out-Null }
    }
    # Settings and the data disk. The CLI configuration in ~\.docker (logins, contexts) stays.
    foreach ($folder in @("$env:APPDATA\Docker", "$env:APPDATA\Docker Desktop", "$env:LOCALAPPDATA\Docker")) {
        if (Test-Path $folder) { Invoke-External "Remove-Item -Recurse -Force $folder" { Remove-Item -Recurse -Force $folder } }
    }
    Invoke-Docker @('desktop', 'start', '--detach') -AllowFailure

    Write-Step 'Purged'
    Write-Host '  Docker Desktop is starting as a fresh install: it asks to accept its terms again, and every image must be pulled or built again.'
}

function Show-Usage {
    Write-Host @'
Usage: .\platform.ps1 <command> [options]

Commands
  start                 Start the dashboard, database and samples (rebuilds images);
                        ollama too when an NVIDIA GPU is present
    -Clean              wipe all the stack's volumes first
    -NoBuild            use the images already built

  test [suite]          Run the Docker test suite
  test                  unit + integration + E2E (the CI baseline)
  test unit             unit + integration only
  test e2e              E2E assertion flows only
    -SlowMo <ms>        Playwright SlowMo (default 500; 0 for fast iteration)
    -NoVideo            skip the per-test video recordings
    -Keep               leave the test stack running and print its loopback Aspire URL
    -NoBuild            use the images already built

  docs start            Start the generated docs preview in the background
    -Port <port>        loopback port (default 4321)
    -NoBuild            use the preview image already built
  docs generate         Capture fresh screenshots and build the static docs site

  stop                  Stop all Sounding samples, dashboard and docs containers
  status                Show the containers of the Sounding stacks
  setup                 Link .claude/skills to .agents/skills (once per clone, for Claude Code)
  purge                 Remove EVERY container, image, volume and build cache on this machine (any project),
                        then reset Docker Desktop to factory defaults. No confirmation.
  help                  This text

Tests are not configured yet; their template options are reserved for a future test stack.
stop never stops containers belonging to other solutions and preserves volumes.
'@
}

# ── Main ─────────────────────────────────────────────────────────────

$savedEnv = @{}
foreach ($name in @('PLAYWRIGHT_SLOW_MO', 'PLAYWRIGHT_RECORD_VIDEO', 'SOUNDING_DOCS_PORT')) {
    $savedEnv[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

Push-Location $PSScriptRoot
$exitCode = 0
try {
    if ($Command -eq 'test' -and $Suite -notin @('all', 'unit', 'e2e')) {
        throw 'Test suite must be all, unit or e2e.'
    }
    $env:SOUNDING_DOCS_PORT = "$Port"
    switch ($Command) {
        'start'  { Invoke-Start }
        'stop'   { Invoke-Stop }
        'status' { Invoke-Status }
        'test'   { Invoke-Test }
        'docs'   { Invoke-Docs }
        'setup'  { Invoke-Setup }
        'purge'  { Invoke-Purge }
        default  { Show-Usage }
    }
}
catch {
    Write-Host ''
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    $exitCode = 1
}
finally {
    # The overrides above are process-wide; do not leak them into the caller's session.
    foreach ($entry in $savedEnv.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
    }
    Pop-Location
}

exit $exitCode
