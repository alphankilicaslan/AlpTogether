Write-Host "=================================================" -ForegroundColor Magenta
Write-Host " AlpTogether - Otomatik Derleme Sistemi          " -ForegroundColor Magenta
Write-Host "=================================================" -ForegroundColor Magenta
Write-Host ""

# 1. Build Single-File AlpTogether Client
Write-Host "[1/2] AlpTogether Client (C# .NET 9 WPF) Derleniyor..." -ForegroundColor Yellow
dotnet publish AlpTogether.csproj -c Release -r win-x64 -p:PublishSingleFile=true --self-contained false
if ($LASTEXITCODE -ne 0) {
    Write-Host "HATA: AlpTogether derlemesi basarisiz!" -ForegroundColor Red
    exit 1
}

try {
    Copy-Item -Path "bin\Release\net9.0-windows\win-x64\publish\AlpTogether.exe" -Destination ".\AlpTogether.exe" -Force -ErrorAction Stop
    Write-Host " -> AlpTogether.exe ana dizine kopyalandi." -ForegroundColor Green
} catch {
    Write-Host " -> BILGI: AlpTogether.exe su an calisiyor (kilitli). Guncel surum publish klasorunde hazir bekliyor." -ForegroundColor Yellow
}

# 2. Build Single-File AlpTogether.Setup (GitHub Web Installer)
Write-Host "`n[2/2] AlpTogether GitHub Web Setup (AlpTogether.Setup.exe) Derleniyor..." -ForegroundColor Yellow
dotnet publish AlpTogetherWebSetup\AlpTogetherWebSetup.csproj -c Release -r win-x64 -p:PublishSingleFile=true --self-contained false
if ($LASTEXITCODE -ne 0) {
    Write-Host "HATA: AlpTogether.Setup derlemesi basarisiz!" -ForegroundColor Red
    exit 1
}

try {
    Copy-Item -Path "AlpTogetherWebSetup\bin\Release\net9.0-windows\win-x64\publish\AlpTogether.Setup.exe" -Destination ".\AlpTogether.Setup.exe" -Force -ErrorAction Stop
    Write-Host " -> AlpTogether.Setup.exe ana dizine kopyalandi." -ForegroundColor Green
} catch {
    Write-Host " -> BILGI: AlpTogether.Setup.exe su an calisiyor (kilitli). Guncel surum publish klasorunde hazir bekliyor." -ForegroundColor Yellow
}

Write-Host "`n[OK] TUM ISLEMLER BASARIYLA TAMAMLANDI!" -ForegroundColor Magenta
Write-Host "-> Cikti Dosyalari: AlpTogether.exe ve AlpTogether.Setup.exe" -ForegroundColor Cyan
