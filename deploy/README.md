# Free VM Deployment

This project needs a VM because the BitTorrent peer protocol uses raw TCP. Static hosts and most free web app hosts are not a good fit.

The simplest free target is an Ubuntu VM on Oracle Cloud Always Free. Open these inbound TCP ports in the cloud security list:

- `6969` for the HTTP tracker
- `5001` for the BitTorrent peer listener

From this repository on Windows, deploy a single file as a public seed:

```powershell
.\deploy\deploy-vm.ps1 -SshTarget ubuntu@YOUR_PUBLIC_IP -PublicIp YOUR_PUBLIC_IP -Runtime linux-arm64 -SourcePath .\TestData\payload.bin
```

Use `-Runtime linux-x64` if your VM is x64 instead of Oracle Ampere ARM.

The script will:

- run tests
- publish a self-contained Linux binary
- copy the binary and payload to `/opt/bittorrent`
- create a public `.torrent` file with `http://YOUR_PUBLIC_IP:6969/announce/`
- install and start `bittorrent-tracker.service`
- install and start `bittorrent-client.service`

Useful server commands:

```bash
sudo systemctl status bittorrent-tracker bittorrent-client --no-pager
sudo journalctl -u bittorrent-tracker -f
sudo journalctl -u bittorrent-client -f
```

To fetch the generated torrent back to your machine:

```powershell
scp ubuntu@YOUR_PUBLIC_IP:/opt/bittorrent/payload.bin.torrent .
```

Only seed files you own or have permission to share.
