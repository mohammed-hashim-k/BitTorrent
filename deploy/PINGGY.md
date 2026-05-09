# Temporary Pinggy Demo

Pinggy can expose this project without a VM or credit card, but the free tunnel address changes and expires after about 60 minutes. This is useful for a short demo, not a 24/7 seed.

## Start Everything

From the repository root:

```powershell
.\deploy\start-pinggy-demo.ps1
```

The script will:

- build the project
- start a Pinggy TCP tunnel for the BitTorrent peer listener
- start the local HTTP tracker
- start a Pinggy HTTP tunnel for the tracker
- create a fresh `.torrent` file with the Pinggy tracker URL
- start a local seeder

The generated torrent is written to:

```text
publish\pinggy-demo\payload-pinggy.torrent
```

Share that `.torrent` while the demo is running.

## Stop Everything

```powershell
.\deploy\stop-pinggy-demo.ps1
```

## Manual Shape

The script is doing the same basic flow manually described here.

Start the peer TCP tunnel:

```powershell
npx pinggy tcp@a.pinggy.io -R0:127.0.0.1:5001 --v --no-autoreconnect
```

Start the tracker after resolving the Pinggy TCP hostname to an IPv4 address:

```powershell
dotnet .\bin\Debug\net9.0\BitTorrent.dll tracker "http://127.0.0.1:6969/announce/" PEER_TUNNEL_IPV4
```

Start the tracker HTTP tunnel:

```powershell
npx pinggy -l 6969 --v --no-autoreconnect
```

Create the tunnel-specific torrent:

```powershell
dotnet .\bin\Debug\net9.0\BitTorrent.dll create .\TestData\payload.bin "https://YOUR_HTTP_TUNNEL/announce/" .\payload-pinggy.torrent
```

Start the seeder with Pinggy's public TCP port as the final argument:

```powershell
dotnet .\bin\Debug\net9.0\BitTorrent.dll client 5001 .\payload-pinggy.torrent C:\Temp\BitTorrentSeedPinggy PEER_TUNNEL_PORT
```

When either Pinggy tunnel expires, repeat the process and share the new `.torrent` file.
