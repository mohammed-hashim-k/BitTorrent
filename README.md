# BitTorrent

A small BitTorrent client written in C# for .NET 9.

This project is intentionally compact, but it still implements the core pieces of the BitTorrent workflow:

- reading and writing `.torrent` files
- computing the torrent `info_hash`
- announcing to an HTTP tracker
- accepting inbound peer connections
- connecting to remote peers
- speaking the BitTorrent wire protocol
- requesting and sending blocks
- verifying pieces with SHA-1
- resuming from data already present on disk

The codebase is small enough to study end-to-end, which makes it useful both as a working sample and as a learning project.

## Contents

- [What This Project Does](#what-this-project-does)
- [What It Does Not Do](#what-it-does-not-do)
- [Project Layout](#project-layout)
- [How The Client Works](#how-the-client-works)
- [Detailed Runtime Sequence](#detailed-runtime-sequence)
- [Torrent File Format In This Project](#torrent-file-format-in-this-project)
- [Peer Protocol In This Project](#peer-protocol-in-this-project)
- [Piece, Block, And File Mapping](#piece-block-and-file-mapping)
- [Build And Run](#build-and-run)
- [Manual End-To-End Test](#manual-end-to-end-test)
- [Static Test Fixture](#static-test-fixture)
- [Automated Tests](#automated-tests)
- [Logging](#logging)
- [Important Classes](#important-classes)
- [Design Notes And Simplifications](#design-notes-and-simplifications)
- [Known Limitations](#known-limitations)

## What This Project Does

At a high level, the application:

1. loads a `.torrent` file
2. parses the tracker URL and `info` metadata
3. calculates the `info_hash`
4. inspects local files to see which pieces already exist
5. announces itself to the tracker
6. receives a list of peers
7. connects to those peers and performs the BitTorrent handshake
8. exchanges bitfields and `have` messages
9. chooses pieces and blocks to download
10. writes blocks to disk
11. verifies each completed piece using SHA-1
12. uploads verified blocks to other peers

The same executable can behave as:

- a seeder, if the target data already exists and verifies
- a leecher, if the target data is missing or incomplete
- both, once it starts verifying pieces and serving them to others

## What It Does Not Do

This is a deliberately focused implementation, not a full production-grade BitTorrent client. It does not currently implement:

- UDP trackers
- DHT
- peer exchange (PEX)
- magnet links
- encryption / protocol obfuscation
- advanced choking algorithms
- multi-tracker tiers
- IPv6 peer parsing from tracker compact responses
- rare edge-case handling found in mature clients

That does not make it broken. It just means the project concentrates on the main educational and functional flow.

## Project Layout

The main runtime lives in the repository root:

- [Program.cs](c:/Users/LENOVO/source/repos/BitTorrent/Program.cs)
  Starts the console app, validates arguments, creates the client, and keeps the process alive.
- [Client.cs](c:/Users/LENOVO/source/repos/BitTorrent/Client.cs)
  The top-level coordinator. It owns the torrent, peers, upload/download queues, and background loops.
- [Torrent.cs](c:/Users/LENOVO/source/repos/BitTorrent/Torrent.cs)
  Handles torrent metadata, piece hashes, verification, and mapping the logical torrent byte stream onto real files on disk.
- [Tracker.cs](c:/Users/LENOVO/source/repos/BitTorrent/Tracker.cs)
  Announces to HTTP trackers and parses compact peer lists.
- [Peer.cs](c:/Users/LENOVO/source/repos/BitTorrent/Peer.cs)
  Implements the BitTorrent wire protocol for a single peer connection.
- [BEncoding.cs](c:/Users/LENOVO/source/repos/BitTorrent/BEncoding.cs)
  Encodes and decodes bencoded dictionaries, lists, integers, and byte strings.
- [Throttle.cs](c:/Users/LENOVO/source/repos/BitTorrent/Throttle.cs)
  Enforces simple upload and download byte-per-second throttling.
- [Log.cs](c:/Users/LENOVO/source/repos/BitTorrent/Log.cs)
  Centralized logging helpers with optional verbose protocol logging.

Helpful supporting files:

- [tracker.ps1](c:/Users/LENOVO/source/repos/BitTorrent/tracker.ps1)
  Minimal local HTTP tracker used for manual testing.
- [TestData/payload.bin](c:/Users/LENOVO/source/repos/BitTorrent/TestData/payload.bin)
  Static test payload.
- [TestData/payload.bin.torrent](c:/Users/LENOVO/source/repos/BitTorrent/TestData/payload.bin.torrent)
  Static torrent file for the payload.
- [BitTorrent.Tests/BitTorrentEndToEndTests.cs](c:/Users/LENOVO/source/repos/BitTorrent/BitTorrent.Tests/BitTorrentEndToEndTests.cs)
  Integration-style tests that generate a torrent, run a tracker, and verify a real transfer between two clients.

## How The Client Works

The application is built around a small number of responsibilities:

- `Torrent` knows what data should exist and whether it is valid.
- `Tracker` knows where peers might be found.
- `Peer` knows how to talk to one remote client.
- `Client` decides what to request, what to upload, and when to connect or disconnect.

That split is important:

- `Torrent` is about data and integrity.
- `Peer` is about network message framing and connection state.
- `Client` is about orchestration and policy.
- `Tracker` is about peer discovery.

## Detailed Runtime Sequence

This section walks through the normal lifecycle of one client process.

### 1. Program startup

The application is launched like this:

```powershell
dotnet run -- 5001 .\TestData\payload.bin.torrent C:\Temp\BitTorrentSeed
```

The three required arguments are:

- listening port
- torrent file path
- download directory

`Program.Main` validates the arguments, creates a `Client`, hooks `Ctrl+C`, starts the client, and then waits.

### 2. Client construction

`Client` immediately:

- generates a 20-character local peer id
- loads the torrent using `Torrent.LoadFromFile`
- subscribes to torrent events such as piece verification and tracker-discovered peer updates

At this point, no network work has happened yet. The client has just built its local state.

### 3. Torrent loading

`Torrent.LoadFromFile`:

1. reads the `.torrent` file bytes
2. decodes them with `BEncoding.DecodeFile`
3. rebuilds torrent metadata from the decoded object graph
4. extracts:
   - tracker URL
   - piece length
   - concatenated piece hashes
   - file layout
   - optional metadata like comment, encoding, and creator

The `info_hash` is then calculated by bencoding the `info` dictionary and hashing it with SHA-1.

That `info_hash` is critical:

- the tracker uses it to identify the swarm
- peers use it in the handshake to prove they are talking about the same torrent

### 4. Local disk scan and resume state

Once the torrent object exists, it checks whether the download directory already contains matching data.

For each piece:

1. it reads the piece bytes from disk
2. computes the piece SHA-1
3. compares it against the expected hash from the torrent metadata

If the piece matches:

- the piece is marked verified
- all blocks in that piece are marked acquired

This is why a process can start as a seeder without downloading anything first.

### 5. Client start

When `Client.Start()` runs, it:

- opens a `TcpListener` on the requested port
- starts four independent background loops

Those loops are:

- tracker updates
- peer processing
- upload processing
- download processing

The loops are intentionally separated so that one area of work does not completely block the others.

### 6. Tracker announce

The tracker loop announces the client to each configured tracker.

The announce URL contains:

- `info_hash`
- `peer_id`
- `port`
- `uploaded`
- `downloaded`
- `left`
- `event`
- `compact=1`

`Tracker` uses `HttpClient` to issue the request.

If the tracker responds successfully, the client:

- reads the `interval`
- reads the compact peer list
- converts each 6-byte entry into an `IPEndPoint`
- raises `PeerListUpdated`

The compact peer list format used here is:

- 4 bytes for IPv4 address
- 2 bytes for port

### 7. Peer discovery and connection

When the client receives tracker peers, it creates `Peer` instances for them.

The client can also accept inbound connections through its `TcpListener`.

So there are two connection paths:

- outbound peer created from tracker result
- inbound peer created from accepted socket

Both are wrapped in the same `Peer` class and go through the same protocol handling after connection.

### 8. Handshake

After a peer connection is established:

1. the local side sends a BitTorrent handshake
2. the remote side is expected to do the same
3. the received handshake is validated

Validation checks include:

- exact handshake length
- protocol string equals `BitTorrent protocol`
- `info_hash` matches the currently loaded torrent

If the `info_hash` does not match, the connection is dropped immediately.

If it does match:

- the remote peer id is recorded
- the handshake is marked as received
- the local bitfield is sent

### 9. Bitfield and have exchange

Once handshake is complete, peers exchange availability information.

This happens through:

- the initial `bitfield` message
- later `have` messages when new pieces are verified

Each `Peer` maintains an `IsPieceDownloaded` array representing what that remote peer has.

That array is what the client uses later to decide:

- whether a peer is useful
- which pieces can be requested from which peer

### 10. Peer policy loop

`Client.ProcessPeers()` is the high-level policy pass.

It:

- disconnects timed-out peers
- sends `interested` or `not interested`
- sends keep-alives
- unchokes interested leechers when upload slots allow
- tracks which connected peers are acting as seeders for us

This is not a full production choking algorithm, but it is enough for a clear and working client flow.

### 11. Download scheduling

`Client.ProcessDownloads()` is where the client decides what to request next.

The flow is:

1. flush any incoming blocks from the receive queue to disk
2. if the torrent is complete, stop requesting
3. rank pieces
4. rank seeders
5. request eligible blocks

The piece ranking combines:

- how much of the piece is already in progress
- how rare the piece appears across connected peers
- a small random factor to avoid deterministic ties

The block request rules include:

- do not request if download throttling is active
- do not request a block already acquired
- do not request from a peer that already has a block in flight
- do not let multiple peers own the same block request at once

This keeps the logic simple and avoids duplicate work.

### 12. Receiving blocks

When a peer sends a `piece` message:

- `Peer` decodes it
- `Peer` raises `BlockReceived`
- `Client` enqueues the block

The client then:

- clears request ownership for that block on the sending peer
- sends `cancel` to any other peer that still believed it owned the same block
- processes downloads again

### 13. Writing blocks and verifying pieces

When queued blocks are flushed:

1. `Torrent.WriteBlock()` maps the block into the logical torrent byte stream
2. the block is written to the correct file and offset
3. the block is marked acquired
4. the containing piece is re-verified

Verification is piece-based, not block-based.

That means:

- a block write alone does not make data trustworthy
- a piece only becomes valid when the full piece hash matches

If the piece verifies:

- `IsPieceVerified[piece] = true`
- all blocks in that piece are marked acquired
- the `PieceVerified` event is raised

If all blocks were present but the piece hash still fails:

- the block acquisition flags for that piece are reset
- the piece becomes eligible for re-download

### 14. Advertising newly verified pieces

When a piece verifies, the client notifies all connected peers with:

- `have <pieceIndex>`

That allows other peers to start requesting that piece from this client.

### 15. Uploading blocks

When a remote peer sends `request`:

- `Peer` raises `BlockRequested`
- `Client` queues the request
- `ProcessUploads()` drains the queue

Before uploading, the client checks:

- the request was not cancelled
- upload throttling allows it
- the piece is actually verified locally

If all checks pass:

- the block is read from disk
- a `piece` message is sent back
- upload counters are updated

### 16. Shutdown

When the user stops the client:

- the listener is stopped
- peers are disconnected
- a `stopped` announce is sent to trackers

This is handled in `Client.Stop()`.

## Torrent File Format In This Project

Torrent metadata is encoded with bencoding.

The project supports:

- dictionaries
- lists
- integers
- byte strings

For `.torrent` files, the most important top-level keys are:

- `announce`
- `comment`
- `created by`
- `creation date`
- `encoding`
- `info`

Inside `info`, the important keys are:

- `piece length`
- `pieces`
- `name`
- `length` for single-file torrents
- `files` for multi-file torrents
- `private` when present

The `pieces` field is a byte array containing all SHA-1 piece hashes concatenated together. Every 20 bytes is one piece hash.

## Peer Protocol In This Project

The peer implementation supports these message types:

- handshake
- keep-alive
- choke
- unchoke
- interested
- not interested
- have
- bitfield
- request
- piece
- cancel
- port

The `port` message is currently only logged and not used for DHT behavior.

Message framing rules:

- handshake is a fixed-size 68-byte message
- all later messages are 4-byte big-endian length-prefixed

The code uses `BinaryPrimitives` for big-endian integer handling.

## Piece, Block, And File Mapping

The torrent payload is treated as one continuous logical byte stream.

That matters because:

- a torrent may contain one file
- or many files
- but peer requests are made in terms of piece and block offsets, not file names

`Torrent.Read()` and `Torrent.Write()` handle this translation.

Given a logical byte range, the code:

1. finds which files overlap the range
2. computes each file-local offset
3. reads or writes only the overlapping slice

This is one of the most important parts of the project because it bridges protocol-level data transfer with real files on disk.

Terminology:

- piece: a larger integrity unit hashed by SHA-1
- block: a smaller transfer unit inside a piece

In this project:

- default block size is `16384` bytes
- the sample torrent uses piece size `32768` bytes

## Build And Run

### Prerequisites

- .NET 9 SDK
- PowerShell for the included local tracker script

### Build

```powershell
dotnet build BitTorrent.sln
```

### Run

You can run the built DLL directly:

```powershell
dotnet .\bin\Debug\net9.0\BitTorrent.dll 5001 .\TestData\payload.bin.torrent C:\Temp\BitTorrentSeed
```

Or through `dotnet run`:

```powershell
dotnet run --project .\BitTorrent.csproj -- 5001 .\TestData\payload.bin.torrent C:\Temp\BitTorrentSeed
```

Arguments:

- first: peer listening port
- second: `.torrent` file path
- third: download directory

## Manual End-To-End Test

The repository includes everything needed for a local seeder/leecher test.

Open three PowerShell windows.

### Window 1: start the tracker

Run the included script:

```powershell
cd C:\Users\LENOVO\source\repos\BitTorrent
.\tracker.ps1
```

This starts a very small HTTP tracker at:

```text
http://127.0.0.1:6969/announce/
```

### Window 2: start the seeder

```powershell
cd C:\Users\LENOVO\source\repos\BitTorrent
mkdir C:\Temp\BitTorrentSeed -Force
copy .\TestData\payload.bin C:\Temp\BitTorrentSeed\payload.bin -Force
dotnet .\bin\Debug\net9.0\BitTorrent.dll 5001 .\TestData\payload.bin.torrent C:\Temp\BitTorrentSeed
```

This directory already contains the full payload, so the client should verify existing pieces and act as a seeder.

### Window 3: start the leecher

```powershell
cd C:\Users\LENOVO\source\repos\BitTorrent
mkdir C:\Temp\BitTorrentLeech -Force
dotnet .\bin\Debug\net9.0\BitTorrent.dll 5002 .\TestData\payload.bin.torrent C:\Temp\BitTorrentLeech
```

This directory starts empty, so the client should download the file from the seeder.

### Verify the result

```powershell
Get-FileHash .\TestData\payload.bin -Algorithm SHA256
Get-FileHash C:\Temp\BitTorrentLeech\payload.bin -Algorithm SHA256
```

The hashes should match.

## Static Test Fixture

The repository includes a checked-in sample payload and matching torrent:

- [TestData/payload.bin](c:/Users/LENOVO/source/repos/BitTorrent/TestData/payload.bin)
- [TestData/payload.bin.torrent](c:/Users/LENOVO/source/repos/BitTorrent/TestData/payload.bin.torrent)

Fixture details:

- file name: `payload.bin`
- file size: `131195` bytes
- piece count: `5`
- tracker URL: `http://127.0.0.1:6969/announce/`

This fixture is useful because it gives you a stable artifact for:

- debugging
- local transfers
- protocol tracing
- hover-documentation review

## Automated Tests

There is also a test project in:

- [BitTorrent.Tests/BitTorrent.Tests.csproj](c:/Users/LENOVO/source/repos/BitTorrent/BitTorrent.Tests/BitTorrent.Tests.csproj)

The tests cover:

- creating and loading a working torrent file
- transferring torrent data between a seeder and leecher using a local mock tracker

Run them with:

```powershell
dotnet test BitTorrent.sln
```

## Logging

Normal logs use:

- `INFO`
- `ERROR`

Verbose protocol logs use:

- `DEBUG`

Verbose logging is disabled by default.

To enable it:

```powershell
$env:BITTORRENT_VERBOSE='1'
```

Then run the client in that same PowerShell session.

Verbose logs are especially useful when you want to inspect:

- handshake flow
- bitfield exchange
- requests and pieces
- choke and unchoke behavior

## Important Classes

### Program

`Program` is intentionally minimal.

It:

- validates CLI arguments
- creates the `Client`
- wires shutdown behavior
- starts the process

### Client

`Client` is the session coordinator.

It owns:

- peer collections
- upload queue
- download queue
- background loops
- overall swarm policy

If you want to understand the application behavior at the highest level, start here.

### Torrent

`Torrent` is the data and integrity layer.

It owns:

- metadata
- piece hashes
- file layout
- verification state
- disk reads and writes

If you want to understand how the app knows whether data is valid, start here.

### Tracker

`Tracker` is responsible for:

- building announce URLs
- issuing HTTP requests
- reading tracker responses
- emitting discovered peers

### Peer

`Peer` is the protocol engine for one connection.

It handles:

- handshake
- message framing
- state transitions
- request and piece messages
- upload and download counters

### BEncoding

`BEncoding` converts between raw bencoded bytes and .NET objects.

It is used for:

- reading `.torrent` files
- building `.torrent` files
- decoding tracker responses

### Throttle

`Throttle` is a simple rolling-window byte limiter used for:

- uploads
- downloads

### Log

`Log` centralizes:

- info logs
- error logs
- verbose protocol logs

## Design Notes And Simplifications

The project intentionally favors readability over maximum feature coverage.

Some examples:

- background loops use simple threads and fixed sleep intervals
- tracker announces are sequential
- the ranking heuristic is compact and easy to follow
- one block at a time is requested from each peer
- duplicate block ownership is avoided with straightforward flags

These are reasonable tradeoffs for a learning-focused client.

## Known Limitations

A few important limitations to keep in mind:

- `Downloaded` is calculated from verified pieces, so it reflects confirmed progress rather than raw received bytes.
- The tracker implementation expects compact IPv4 peer lists.
- The client does not implement the broader BitTorrent ecosystem features like DHT or magnet links.
- The choking and peer selection strategy is intentionally simple.
- The app is best suited to controlled or educational scenarios rather than public-internet heavy use.

## Suggested Reading Order

If you want to understand the codebase efficiently, this order works well:

1. [Program.cs](c:/Users/LENOVO/source/repos/BitTorrent/Program.cs)
2. [Client.cs](c:/Users/LENOVO/source/repos/BitTorrent/Client.cs)
3. [Torrent.cs](c:/Users/LENOVO/source/repos/BitTorrent/Torrent.cs)
4. [Peer.cs](c:/Users/LENOVO/source/repos/BitTorrent/Peer.cs)
5. [Tracker.cs](c:/Users/LENOVO/source/repos/BitTorrent/Tracker.cs)
6. [BEncoding.cs](c:/Users/LENOVO/source/repos/BitTorrent/BEncoding.cs)
7. [Throttle.cs](c:/Users/LENOVO/source/repos/BitTorrent/Throttle.cs)
8. [Log.cs](c:/Users/LENOVO/source/repos/BitTorrent/Log.cs)

That sequence moves from high-level flow to data integrity, then into network protocol details.

## Summary

This project is a compact BitTorrent client that demonstrates the full core loop:

- load torrent metadata
- find peers
- connect and handshake
- exchange availability
- request blocks
- write data
- verify pieces
- upload verified data

It is small enough to understand completely, but complete enough to perform real local transfers.
