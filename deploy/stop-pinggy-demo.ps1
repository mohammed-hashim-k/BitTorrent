param(
    [string] $WorkDir = "publish\pinggy-demo"
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$demoDir = Join-Path $repoRoot $WorkDir

function Stop-ProcessTree {
    param([int] $RootProcessId)

    Get-CimInstance Win32_Process -Filter "ParentProcessId = $RootProcessId" |
        ForEach-Object { Stop-ProcessTree -RootProcessId $_.ProcessId }

    $process = Get-Process -Id $RootProcessId -ErrorAction SilentlyContinue
    if ($process) {
        Stop-Process -Id $RootProcessId -Force
    }
}

if (-not (Test-Path $demoDir)) {
    Write-Host "No Pinggy demo directory found: $demoDir"
    return
}

Get-ChildItem $demoDir -Filter "*.pid" -ErrorAction SilentlyContinue | ForEach-Object {
    $pidText = Get-Content $_.FullName -Raw
    $processId = 0
    if ([int]::TryParse($pidText.Trim(), [ref] $processId)) {
        $process = Get-CimInstance Win32_Process -Filter "ProcessId = $processId" -ErrorAction SilentlyContinue
        if ($process) {
            $commandLine = $process.CommandLine ?? ""
            if ($commandLine -match "pinggy|BitTorrent\.dll") {
                Stop-ProcessTree -RootProcessId $processId
                Write-Host "Stopped $($_.BaseName) process $processId"
            } else {
                Write-Host "Skipped stale $($_.BaseName) PID $processId"
            }
        }
    }

    Remove-Item $_.FullName -Force
}

Write-Host "Pinggy demo stop requested."
