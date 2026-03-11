$json = Get-Content C:\bot-dotnet\appsettings.json -Raw
$json = $json.Replace("9DF94ED45B981B0553B172618F2E8B6927B4E3FE", "084D474C2D61B43F963EE84870A327014C118FA6")
Set-Content -Path C:\bot-dotnet\appsettings.json -Value $json -NoNewline
Write-Host "Updated appsettings.json cert thumbprint"
