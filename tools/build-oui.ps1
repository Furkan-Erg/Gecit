# Rebuilds src\Gecit\Assets\oui.txt.gz (MAC prefix -> manufacturer, for "Ağım") from Wireshark's copy
# of the IEEE registry. Lines: "<hex prefix>[/<bits>]<TAB><short name>". Run now and then: pwsh tools\build-oui.ps1
param([string]$Source = "https://www.wireshark.org/download/automated/data/manuf")
$ErrorActionPreference = "Stop"
$out = Join-Path $PSScriptRoot "..\src\Gecit\Assets\oui.txt.gz"

$text = (Invoke-WebRequest $Source -UseBasicParsing -TimeoutSec 120).Content
if ($text -is [byte[]]) { $text = [Text.Encoding]::UTF8.GetString($text) }

# Legal-form noise, so "Samsung Electronics Co.,Ltd" reads as "Samsung Electronics".
# CultureInvariant: under tr-TR, case-insensitive "inc" doesn't match "Inc" (I -> ı).
$suffix = [regex]::new('[\s,.]*(\b(co|corp|corporation|corporate|inc|incorporated|ltd|limited|llc|gmbh|ag|sa|sas|s\.a|srl|bv|b\.v|oy|ab|as|a/s|kg|plc|pte|pty|kk|k\.k|company|international|holdings?|group)\b[\s,.()]*)+$',
    'IgnoreCase, CultureInvariant')
$lines = [Collections.Generic.List[string]]::new()
foreach ($line in $text -split "`n") {
    if (-not $line -or $line[0] -eq '#') { continue }
    $cols = $line.TrimEnd("`r") -split "`t"
    if ($cols.Count -lt 2) { continue }
    $prefix = $cols[0].Trim()
    $name = if ($cols.Count -ge 3 -and $cols[2].Trim()) { $cols[2].Trim() } else { $cols[1].Trim() }
    $bits = 24
    if ($prefix -match '^(.+)/(\d+)$') { $prefix = $Matches[1]; $bits = [int]$Matches[2] }
    $hex = ($prefix -replace '[:\-.]', '').ToUpperInvariant()
    $hex = $hex.Substring(0, [Math]::Min($hex.Length, [int][Math]::Ceiling($bits / 4)))
    for ($i = 0; $i -lt 3; $i++) { $name = $suffix.Replace($name, '').Trim() }
    $name = $name -replace '\s+', ' '
    if (-not $name) { continue }
    $lines.Add($(if ($bits -eq 24) { "$hex`t$name" } else { "$hex/$bits`t$name" }))
}

$ms = [IO.MemoryStream]::new()
$gz = [IO.Compression.GZipStream]::new($ms, [IO.Compression.CompressionLevel]::SmallestSize)
$bytes = [Text.Encoding]::UTF8.GetBytes(($lines -join "`n") + "`n")
$gz.Write($bytes, 0, $bytes.Length)
$gz.Dispose()
[IO.File]::WriteAllBytes($out, $ms.ToArray())
"{0} kayıt -> {1} ({2:N0} KB)" -f $lines.Count, (Resolve-Path $out), ($ms.ToArray().Length / 1KB)
