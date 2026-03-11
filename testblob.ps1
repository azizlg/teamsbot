$wc = New-Object System.Net.WebClient
$wc.Headers.Add("Content-Type", "text/plain")
$body = $wc.UploadString("http://localhost:8446/session/verify001", "POST", "")
Write-Host ("BlobLen=" + $body.Length)
Write-Host ("BlobPrefix=" + $body.Substring(0, [Math]::Min(80, $body.Length)))
