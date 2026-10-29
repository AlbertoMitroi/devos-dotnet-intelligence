#!/usr/bin/env pwsh
# The single local verify command for PowerShell hosts (2.4). Same steps as verify.sh; needs only Docker.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$tag = if ($env:DI_VERIFY_TAG) { $env:DI_VERIFY_TAG } else { 'dotnet-intelligence:verify' }
$work = Join-Path $root '.verify'
$user = if ($IsLinux -or $IsMacOS) { "$(id -u):$(id -g)" } else { $null }

function Step([string] $name) { Write-Host "`n== $name" }

# Arguments are passed as one array, so docker flags such as -r or -t are never bound by PowerShell.
function Invoke-Docker([string[]] $Arguments) {
    & docker @Arguments
    if ($LASTEXITCODE -ne 0) { throw "docker $($Arguments -join ' ') failed with exit code $LASTEXITCODE" }
}

function UserArgs { if ($user) { @('--user', $user) } else { @() } }

if (Test-Path $work) { Remove-Item -Recurse -Force $work }
New-Item -ItemType Directory -Force -Path (Join-Path $work 'repos'), (Join-Path $work 'out') | Out-Null

Step 'test stage'
Invoke-Docker @('build', '--target', 'test', '-t', "$tag-test", $root)

Step 'runtime and action images'
Invoke-Docker @('build', '--target', 'runtime', '-t', $tag, $root)
Invoke-Docker @('build', '--target', 'action', '-t', "$tag-action", $root)

Step 'self-test'
$version = & docker run --rm --network none $tag version --json
if ($LASTEXITCODE -ne 0) { throw 'version --json failed' }
$version | Set-Content -NoNewline -Path (Join-Path $work 'version.json')
Write-Host $version
foreach ($expected in '"roslynCompatible":true', '"version":"10.0.403"', '"name":"Microsoft.NETCore.App","version":"10.0.14"') {
    if (-not $version.Contains($expected)) { throw "version --json lacks $expected" }
}
$uid = & docker run --rm --entrypoint id $tag -u
if ($uid.Trim() -eq '0') { throw 'runtime image runs as root' }

Step 'fixture repositories'
foreach ($fixture in Get-ChildItem -Directory (Join-Path $root 'tests/fixtures')) {
    $arguments = @('run', '--rm') + (UserArgs) + @('-e', 'HOME=/tmp', '--entrypoint', 'bash',
        '-v', "$(Join-Path $root 'tests/fixtures'):/fixtures:ro", '-v', "$(Join-Path $work 'repos'):/repos",
        $tag, '/opt/dotnet-intelligence/scripts/fixture-repo.sh', "/fixtures/$($fixture.Name)", "/repos/$($fixture.Name)")
    Invoke-Docker $arguments
}

Step 'fixture analyses'
foreach ($repo in Get-ChildItem -Directory (Join-Path $work 'repos')) {
    $name = $repo.Name
    foreach ($run in 1, 2) {
        $out = Join-Path $work "out/$name-$run"
        New-Item -ItemType Directory -Force -Path $out | Out-Null
        $arguments = @('run', '--rm') + (UserArgs) + @('-v', "$($repo.FullName):/src:ro", '-v', "${out}:/out",
            $tag, 'analyze', '--src', '/src', '--out', '/out')
        & docker @arguments | Set-Content -Path (Join-Path $work "out/$name-$run.events.ndjson")
        if ($LASTEXITCODE -ne 0) { throw "analyze $name run ${run}: exit code $LASTEXITCODE, expected 0 (complete)" }
        if (-not (Test-Path (Join-Path $out 'manifest.json'))) { throw "analyze $name run ${run}: no manifest" }
    }
    # Byte comparison inside the image, so the check is the same on every host.
    Invoke-Docker @('run', '--rm', '--entrypoint', 'diff', '-v', "$(Join-Path $work 'out'):/runs:ro", $tag, '-r', '-x', 'run.json', "/runs/$name-1", "/runs/$name-2")
    Write-Host "${name}: two runs identical"
    Invoke-Docker @('run', '--rm', '-v', "$(Join-Path $work 'out'):/runs:ro", $tag, 'verify', "/runs/$name-1")
}

Step 'GitHub Action mode'
$workspace = Join-Path $work 'workspace'
New-Item -ItemType Directory -Force -Path $workspace | Out-Null
Copy-Item -Recurse (Join-Path $work 'repos/f00-minimal') (Join-Path $workspace 'repo')
New-Item -ItemType File -Force -Path (Join-Path $workspace 'github-output') | Out-Null
& docker run --rm --network none `
    -e GITHUB_WORKSPACE=/github/workspace -e GITHUB_OUTPUT=/github/workspace/github-output `
    -e INPUT_PATH=repo -e INPUT_OUTPUT=intelligence-out -e 'INPUT_FAIL-ON=failed' `
    -v "${workspace}:/github/workspace" -w /github/workspace `
    "$tag-action" gha | Set-Content -Path (Join-Path $work 'action.log')
if ($LASTEXITCODE -ne 0) { throw "gha exit code $LASTEXITCODE" }
$outputs = Get-Content (Join-Path $workspace 'github-output')
foreach ($expected in 'status=complete', 'manifest-path=intelligence-out/manifest.json') {
    if ($outputs -notcontains $expected) { throw "GitHub output lacks $expected" }
}
if (-not (Test-Path (Join-Path $workspace 'intelligence-out/manifest.json'))) { throw 'action wrote no manifest' }
$outputs | Write-Host

Step 'verify passed'
