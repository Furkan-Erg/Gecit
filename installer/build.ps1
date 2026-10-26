# Builds dist\Gecit-Setup-<version>.exe: self-contained publish + Inno Setup.
#   pwsh installer\build.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$csproj = Join-Path $root 'src\Gecit\Gecit.csproj'
$version = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$publish = Join-Path $root 'publish\app'

$dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 bulunamadı: winget install JRSoftware.InnoSetup' }

Write-Host "Geçit $version yayınlanıyor..."
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
# Self-contained: users don't need to install .NET. No debug symbols in the package.
& $dotnet publish $csproj -c Release -r win-x64 --self-contained true -o $publish `
    -p:DebugType=none -p:DebugSymbols=false -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish başarısız' }
# Gecit.Dns.exe is only a developer test tool; the service runs inside Gecit.exe.
Remove-Item (Join-Path $publish 'Gecit.Dns.exe'), (Join-Path $publish 'Gecit.Dns.runtimeconfig.json') -ErrorAction SilentlyContinue

# WebView2 Evergreen bootstrapper (Microsoft redistributable), installed only if missing.
$bootstrapper = Join-Path $PSScriptRoot 'MicrosoftEdgeWebview2Setup.exe'
if (-not (Test-Path $bootstrapper)) {
    Invoke-WebRequest 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $bootstrapper
}

Write-Host 'Kurulum paketi derleniyor...'
& $iscc /Q "/DAppVersion=$version" (Join-Path $PSScriptRoot 'gecit.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup derlemesi başarısız' }

$out = Join-Path $root "dist\Gecit-Setup-$version.exe"
$size = [math]::Round((Get-Item $out).Length / 1MB, 1)
Write-Host "Hazır: $out ($size MB)"
Get-FileHash $out -Algorithm SHA256 | ForEach-Object { Write-Host "SHA256: $($_.Hash)" }
