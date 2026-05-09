param(
    [Parameter(Mandatory = $true)]
    [string] $SshTarget,

    [Parameter(Mandatory = $true)]
    [string] $PublicIp,

    [string] $Runtime = "linux-arm64",
    [string] $SourcePath = "TestData\payload.bin",
    [string] $RemoteRoot = "/opt/bittorrent",
    [int] $PeerPort = 5001,
    [int] $TrackerPort = 6969,
    [switch] $SkipTests
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$sourceFullPath = Resolve-Path (Join-Path $repoRoot $SourcePath)
$sourceItem = Get-Item $sourceFullPath

if ($sourceItem.PSIsContainer) {
    throw "deploy-vm.ps1 currently supports a single source file. Pass a file path with -SourcePath."
}

$publishDir = Join-Path $repoRoot "publish\$Runtime"
$binaryPath = Join-Path $publishDir "BitTorrent"
$installScript = Join-Path $PSScriptRoot "install-ubuntu.sh"
$sourceName = $sourceItem.Name
$torrentName = "$sourceName.torrent"
$announceUrl = "http://${PublicIp}:$TrackerPort/announce/"

if (-not $SkipTests) {
    dotnet test (Join-Path $repoRoot "BitTorrent.sln")
}

dotnet publish (Join-Path $repoRoot "BitTorrent.csproj") `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -o $publishDir

scp $binaryPath "${SshTarget}:/tmp/BitTorrent"
scp $sourceItem.FullName "${SshTarget}:/tmp/$sourceName"
scp $installScript "${SshTarget}:/tmp/install-bittorrent.sh"

$remoteCommands = @"
set -euo pipefail
sudo mkdir -p '$RemoteRoot/data'
sudo mv /tmp/BitTorrent '$RemoteRoot/BitTorrent'
sudo mv '/tmp/$sourceName' '$RemoteRoot/data/$sourceName'
sudo chmod +x '$RemoteRoot/BitTorrent'
sudo '$RemoteRoot/BitTorrent' create '$RemoteRoot/data/$sourceName' '$announceUrl' '$RemoteRoot/$torrentName'
sudo PUBLIC_IP='$PublicIp' PEER_PORT='$PeerPort' TRACKER_PORT='$TrackerPort' TORRENT_FILE='$RemoteRoot/$torrentName' DATA_DIR='$RemoteRoot/data' BINARY='$RemoteRoot/BitTorrent' APP_ROOT='$RemoteRoot' bash /tmp/install-bittorrent.sh
"@

$remoteCommands | ssh $SshTarget "bash -s"

Write-Host "Deployment requested. Check status with:"
Write-Host "ssh $SshTarget `"sudo systemctl status bittorrent-tracker bittorrent-client --no-pager`""
Write-Host "Download the generated torrent with:"
Write-Host "scp ${SshTarget}:$RemoteRoot/$torrentName ."
