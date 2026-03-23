$peers = @{}
$listener = [System.Net.HttpListener]::new()
$listener.Prefixes.Add("http://127.0.0.1:6969/announce/")
$listener.Start()
while ($true) {
    $ctx = $listener.GetContext()
    $requestPort = [int]$ctx.Request.QueryString["port"]
    $eventName = $ctx.Request.QueryString["event"]

    if ($eventName -eq "stopped") {
        $peers.Remove($requestPort) | Out-Null
    } else {
        $peers[$requestPort] = $requestPort
    }

    $peerBytes = [System.Collections.Generic.List[byte]]::new()
    foreach ($peerPort in $peers.Keys) {
        if ($peerPort -eq $requestPort) { continue }
        foreach ($b in [byte[]](127,0,0,1,[math]::Floor($peerPort / 256),($peerPort % 256))) {
            [void]$peerBytes.Add([byte]$b)
        }
    }

    $response = [System.Collections.Generic.List[byte]]::new()
    foreach ($b in [System.Text.Encoding]::ASCII.GetBytes("d8:intervali1e5:peers")) { [void]$response.Add($b) }
    foreach ($b in [System.Text.Encoding]::ASCII.GetBytes($peerBytes.Count.ToString())) { [void]$response.Add($b) }
    [void]$response.Add([byte][char]":")
    foreach ($b in $peerBytes) { [void]$response.Add($b) }
    [void]$response.Add([byte][char]"e")

    $bytes = $response.ToArray()
    $ctx.Response.StatusCode = 200
    $ctx.Response.ContentLength64 = $bytes.Length
    $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
    $ctx.Response.Close()
}
