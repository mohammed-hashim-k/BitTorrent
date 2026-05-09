param(
    [string] $SourcePath = "TestData\payload.bin",
    [string] $SeedDir = "C:\Temp\BitTorrentSeedPinggy",
    [string] $WorkDir = "publish\pinggy-demo",
    [int] $TrackerPort = 6969,
    [int] $LocalPeerPort = 5001
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$demoDir = Join-Path $repoRoot $WorkDir
$appDll = Join-Path $repoRoot "bin\Debug\net9.0\BitTorrent.dll"
$sourceFullPath = Resolve-Path (Join-Path $repoRoot $SourcePath)
$sourceName = Split-Path $sourceFullPath -Leaf

function Start-LoggedProcess {
    param(
        [string] $Name,
        [string] $FilePath,
        [string[]] $Arguments,
        [string] $WorkingDirectory
    )

    $stdout = Join-Path $demoDir "$Name.out.log"
    $stderr = Join-Path $demoDir "$Name.err.log"
    Remove-Item $stdout, $stderr -ErrorAction SilentlyContinue

    $process = Start-Process `
        -FilePath $FilePath `
        -ArgumentList $Arguments `
        -WorkingDirectory $WorkingDirectory `
        -WindowStyle Hidden `
        -RedirectStandardOutput $stdout `
        -RedirectStandardError $stderr `
        -PassThru

    $process.Id | Set-Content (Join-Path $demoDir "$Name.pid")
    return $process
}

function Wait-ForMatch {
    param(
        [string] $Path,
        [string] $Pattern,
        [int] $TimeoutSeconds = 30
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $Path) {
            $content = Get-Content $Path -Raw -ErrorAction SilentlyContinue
            if (-not [string]::IsNullOrEmpty($content)) {
                $match = [regex]::Match($content, $Pattern)
                if ($match.Success) {
                    return $match
                }
            }
        }

        Start-Sleep -Milliseconds 500
    }

    throw "Timed out waiting for pattern '$Pattern' in $Path"
}

New-Item -ItemType Directory -Force -Path $demoDir | Out-Null

& (Join-Path $PSScriptRoot "stop-pinggy-demo.ps1") -WorkDir $WorkDir

dotnet build (Join-Path $repoRoot "BitTorrent.sln")

$peerProcess = Start-LoggedProcess `
    -Name "peer-tunnel" `
    -FilePath "npx.cmd" `
    -Arguments @("pinggy", "tcp@a.pinggy.io", "-R0:127.0.0.1:$LocalPeerPort", "--v", "--no-autoreconnect") `
    -WorkingDirectory $repoRoot

$peerMatch = Wait-ForMatch `
    -Path (Join-Path $demoDir "peer-tunnel.out.log") `
    -Pattern 'tcp://([^:\s"]+):(\d+)'

$peerHost = $peerMatch.Groups[1].Value
$peerPort = [int] $peerMatch.Groups[2].Value
$peerIp = ([System.Net.Dns]::GetHostAddresses($peerHost) |
    Where-Object AddressFamily -eq InterNetwork |
    Select-Object -First 1).IPAddressToString

$trackerProcess = Start-LoggedProcess `
    -Name "tracker" `
    -FilePath "dotnet" `
    -Arguments @($appDll, "tracker", "http://127.0.0.1:$TrackerPort/announce/", $peerIp) `
    -WorkingDirectory $repoRoot

Start-Sleep -Seconds 2

$httpProcess = Start-LoggedProcess `
    -Name "http-tunnel" `
    -FilePath "npx.cmd" `
    -Arguments @("pinggy", "-l", "$TrackerPort", "--v", "--no-autoreconnect") `
    -WorkingDirectory $repoRoot

$httpMatch = Wait-ForMatch `
    -Path (Join-Path $demoDir "http-tunnel.out.log") `
    -Pattern 'https://[^\s"]+'

$announceUrl = $httpMatch.Value.TrimEnd("/") + "/announce/"
$torrentPath = Join-Path $demoDir "$([IO.Path]::GetFileNameWithoutExtension($sourceName))-pinggy.torrent"

New-Item -ItemType Directory -Force -Path $SeedDir | Out-Null
Copy-Item $sourceFullPath (Join-Path $SeedDir $sourceName) -Force

dotnet $appDll create $sourceFullPath $announceUrl $torrentPath

$seederProcess = Start-LoggedProcess `
    -Name "seeder" `
    -FilePath "dotnet" `
    -Arguments @($appDll, "client", "$LocalPeerPort", $torrentPath, $SeedDir, "$peerPort") `
    -WorkingDirectory $repoRoot

Start-Sleep -Seconds 5

$infoPath = Join-Path $demoDir "demo-info.txt"
@(
    "Tracker announce URL: $announceUrl",
    "Peer TCP URL: tcp://${peerHost}:$peerPort",
    "Peer compact IPv4 advertised by tracker: $peerIp",
    "Torrent file: $torrentPath",
    "Seeder PID: $($seederProcess.Id)",
    "Stop command: .\deploy\stop-pinggy-demo.ps1"
) | Set-Content $infoPath

Write-Host "Pinggy demo is running."
Write-Host "Tracker announce URL: $announceUrl"
Write-Host "Peer TCP URL: tcp://${peerHost}:$peerPort"
Write-Host "Peer compact IPv4 advertised by tracker: $peerIp"
Write-Host "Torrent file: $torrentPath"
Write-Host "Seeder PID: $($seederProcess.Id)"
Write-Host "Info file: $infoPath"
Write-Host "Stop it with: .\deploy\stop-pinggy-demo.ps1"
