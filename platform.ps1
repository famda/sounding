<#
.SYNOPSIS
Start the samples, generate or preview documentation, run the Docker test suite, or evaluate the MCP tools.

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
.\platform.ps1 evals run slow-db
Check that a local model finds the cause of a slow database through the MCP tools.

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
    [ValidateSet('start', 'stop', 'status', 'test', 'docs', 'evals', 'setup', 'purge', 'help')]
    [string] $Command = 'help',

    # test suite, docs action or evals action.
    [Parameter(Position = 1)]
    [ValidateSet('all', 'unit', 'e2e', 'start', 'generate', 'list', 'run', 'report', 'record')]
    [string] $Suite = 'all',

    # evals run/record: one case, or the tuning or holdout set (default: all).
    [Parameter(Position = 2)]
    [string] $Case = '',

    # start: wipe this project's volumes first.
    [switch] $Clean,

    # test: leave the test stack up afterwards to inspect it in Aspire.
    [switch] $Keep,
    # evals run: attempts per case.
    [ValidateRange(1, 50)]
    [int] $Repeat = 1,
    # evals run/report: an earlier run to compare with.
    [string] $Baseline = '',
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
$EvalsProject = 'sounding-evals'
$EvalsEnvironmentProject = 'sounding-evals-env'
$ManagedProjects = @($DevProject, $SamplesProject, 'sounding-examples', $CaptureProject, $SiteProject, $EvalsProject, $EvalsEnvironmentProject)
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
    $compose = @('compose', '--env-file', $EnvFile, '-p', $DevProject, '--profile', 'tools')
    foreach ($file in $DevFiles) { $compose += @('-f', $file) }
    if ($script:HasGpu) { $compose += @('-f', 'docker-compose.gpu.yml') }
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
    # The simulated users ask the assistant only when its profile runs.
    $env:SAMPLES_ASSISTANT = if ($script:HasGpu) { '1' } else { '0' }
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
    Write-Host '  REST API          http://localhost:8080/api/docs'
    Write-Host '  MCP endpoint      http://localhost:8080/mcp'
    Write-Host '  MCP Inspector     http://localhost:6274 (Sounding already listed)'
    Write-Host '  Chat              http://localhost:3000 (local model, Sounding tools on)'
}

# The models volume is shared by the chat's model and the evals' model: created here, so neither project owns it.
function Assert-ModelsVolume {
    Invoke-Docker -Query @('volume', 'create', 'sounding-models') | Out-Null
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

    Write-Step 'Starting'
    Assert-ModelsVolume
    Invoke-Docker ($compose + $up + @('db', 'dashboard', 'mcp-inspector', 'chat', 'chat-model', 'chat-model-pull'))
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

# Docker's own progress output is noise here: it is kept and shown only when the step fails.
function Invoke-DockerQuietly([string[]] $Arguments) {
    $output = & docker @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        $output | ForEach-Object { Write-Host $_ }
        throw "docker $($Arguments -join ' ') exited with code $LASTEXITCODE"
    }
}

# What the code under test is: the commit, and a hash of every uncommitted change to the dashboard, the samples and
# the evals (tracked or not), so that two runs of different working trees never look alike. Read-only Git commands.
function Get-ImplementationFingerprint {
    $head = (& git rev-parse --short=12 HEAD 2>$null)
    if (-not $head) { return 'no-git' }
    $paths = @('src', 'samples', 'evals', ':(exclude)evals/out', ':(exclude)evals/snapshots')
    $dirty = @(& git status --porcelain=v1 -uall -- @paths 2>$null)
    if ($dirty.Count -eq 0) { return $head }
    $hash = [System.Security.Cryptography.IncrementalHash]::CreateHash([System.Security.Cryptography.HashAlgorithmName]::SHA256)
    $hash.AppendData([System.Text.Encoding]::UTF8.GetBytes(((& git diff HEAD -- @paths 2>$null) -join "`n")))
    foreach ($line in $dirty | Where-Object { $_.StartsWith('??') }) {
        $file = $line.Substring(3).Trim('"')
        $hash.AppendData([System.Text.Encoding]::UTF8.GetBytes($file))
        if (Test-Path -LiteralPath $file -PathType Leaf) { $hash.AppendData([System.IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $file))) }
    }
    return "$head+dirty-$([Convert]::ToHexString($hash.GetHashAndReset()).Substring(0, 12).ToLowerInvariant())"
}

# The runner without the environment, for planning and reporting: it only reads and writes files.
function Invoke-EvalRunner([string[]] $Arguments) {
    $out = Join-Path $PSScriptRoot 'evals/out'
    $snapshots = Join-Path $PSScriptRoot 'evals/snapshots'
    New-Item -ItemType Directory -Force $out | Out-Null
    & docker run --rm -v "${out}:/out" -v "${snapshots}:/snapshots" -e "IMPLEMENTATION=$(Get-ImplementationFingerprint)" `
        "$EvalsProject-runner" @Arguments | Out-Host
    return $LASTEXITCODE
}

function Get-SnapshotName($Case) { if ($Case.PSObject.Properties['snapshot']) { $Case.snapshot } else { $Case.id } }

# evals list | run [case | tuning | holdout] | report | record [case]. A run replays each case's recorded snapshot with
# the database clock held at the moment it was taken, in every form the case has (plain question, server prompt).
function Invoke-Evals {
    $scenarios = Get-Content 'evals/scenarios.json' -Raw | ConvertFrom-Json
    $evals = @('compose', '--progress', 'quiet', '--env-file', $EnvFile, '-p', $EvalsProject, '-f', 'evals/docker-compose.yml')
    $live = @('compose', '--progress', 'quiet', '--env-file', $EnvFile, '-p', $EvalsEnvironmentProject, '-f', 'evals/environment/docker-compose.yml')
    $faulty = $live + @('--profile', 'faults')

    if ($Suite -eq 'list') {
        $scenarios | ForEach-Object {
            $forms = @(if (-not $_.PSObject.Properties['plain'] -or $_.plain) { 'question' }) + @(if ($_.PSObject.Properties['prompt']) { $_.prompt.name })
            [pscustomobject]@{ Case = $_.id; Set = $_.set; Asked = $forms -join ', '; Tests = $_.title }
        } | Format-Table -AutoSize
        return
    }
    if ($Suite -notin 'run', 'report', 'record') { throw 'Usage: .\platform.ps1 evals list | run [case] | report | record [case]' }

    Invoke-DockerQuietly ($evals + @('build', '--quiet', 'runner'))
    if ($Suite -eq 'report') {
        $arguments = @('report')
        if ($Baseline) { $arguments += @('--baseline', $Baseline) }
        $script:exitCode = Invoke-EvalRunner $arguments
        return
    }

    $selected = @(if (-not $Case) { $scenarios }
        elseif ($Case -in 'tuning', 'holdout') { $scenarios | Where-Object set -eq $Case }
        else { $scenarios | Where-Object id -eq $Case })
    if ($selected.Count -eq 0) { throw "No case '$Case'. See: .\platform.ps1 evals list" }
    Assert-EnvFile

    if ($Suite -eq 'record') {
        # A case that replays another's snapshot is recorded through that one.
        $selected = @($selected | Where-Object { -not $_.PSObject.Properties['snapshot'] })
        Invoke-DockerQuietly ($faulty + @('build', '--quiet'))
        try {
            foreach ($scenario in $selected) {
                Invoke-DockerQuietly ($faulty + @('down', '--remove-orphans'))
                # Up starts everything but the faulty containers, which the runner starts when the case says so.
                Invoke-DockerQuietly ($live + @('up', '-d'))
                Invoke-DockerQuietly ($faulty + @('create', 'worker', 'reports-batch', 'exporter', 'indexer'))
                & docker @($evals + @('run', '--rm', '--no-deps', 'runner', 'record', $scenario.id))
                if ($LASTEXITCODE -ne 0) { $script:exitCode = 1 }
            }
        }
        finally { Invoke-DockerQuietly ($faulty + @('down', '--remove-orphans')) }
        return
    }

    $missing = @($selected | Where-Object { -not (Test-Path "evals/snapshots/$(Get-SnapshotName $_).sql.gz") })
    if ($missing.Count) { throw "Not recorded yet: $($missing.id -join ', '). Record with: .\platform.ps1 evals record <case>" }

    Assert-ModelsVolume
    if (Test-NvidiaGpu) { $evals += @('-f', 'evals/docker-compose.gpu.yml') }
    else { Write-Warning 'No NVIDIA GPU found: the model runs on the processor, and each answer can take many minutes.' }
    $implementation = Get-ImplementationFingerprint
    $run = Get-Date -Format 'yyyyMMdd-HHmmss'
    Invoke-DockerQuietly ($live + @('build', '--quiet', 'dashboard'))
    Invoke-DockerQuietly ($evals + @('up', '-d', 'model'))
    if ((Invoke-EvalRunner (@('plan', $run, "$Repeat") + @($selected.id))) -ne 0) { throw 'Could not plan the run.' }
    Invoke-DockerQuietly ($faulty + @('down', '--remove-orphans'))
    Invoke-DockerQuietly ($live + @('up', '-d', 'db', 'dashboard'))

    try {
        foreach ($scenario in $selected) {
            $forms = @()
            if (-not $scenario.PSObject.Properties['plain'] -or $scenario.plain) { $forms += , @() }
            if ($scenario.PSObject.Properties['prompt']) { $forms += , @('--prompt') }
            foreach ($form in $forms) {
                foreach ($attempt in 1..$Repeat) {
                    & docker @($evals + @('run', '--rm', '-e', "RUN_ID=$run", '-e', "ATTEMPT=$attempt", '-e', "IMPLEMENTATION=$implementation",
                            'runner', 'run', $scenario.id) + $form)
                    if ($LASTEXITCODE -ne 0) { $script:exitCode = 1 }
                }
            }
        }
    }
    finally { Invoke-DockerQuietly ($faulty + @('down', '--remove-orphans')) }

    $arguments = @('report', '--run', $run)
    if ($Baseline) { $arguments += @('--baseline', $Baseline) }
    if ((Invoke-EvalRunner $arguments) -ne 0) { $script:exitCode = 1 }
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

  evals list            The evaluation cases (see evals/README.md)
  evals run [case]      Ask a local model each case through the MCP tools (all, one case, tuning or holdout)
    -Repeat <n>         attempts per case
    -Baseline <run>     compare with an earlier run
  evals report          The newest run's results
  evals record [case]   Re-record a case's snapshot (after changing the agent, schema or samples)

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
'@
}

# ── Main ─────────────────────────────────────────────────────────────

$savedEnv = @{}
foreach ($name in @('PLAYWRIGHT_SLOW_MO', 'PLAYWRIGHT_RECORD_VIDEO', 'SOUNDING_DOCS_PORT', 'SAMPLES_ASSISTANT')) {
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
        'evals'  { Invoke-Evals }
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
