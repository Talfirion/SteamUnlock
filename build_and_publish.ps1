# Steam Unlock Build Script
# This script publishes the app as a single-file executable

$projectDir = "./src/SteamUnlock"
$publishDir = "./publish"

if (Test-Path $publishDir) {
    Remove-Item -Path $publishDir -Recurse -Force
}

Write-Host "[*] Publishing Steam Unlock..." -ForegroundColor Cyan

dotnet publish "$projectDir/SteamUnlock.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:PublishReadyToRun=true `
    -o $publishDir

if ($LASTEXITCODE -eq 0) {
    Write-Host "[+] Publish successful! Output folder: $publishDir" -ForegroundColor Green
    Write-Host "[*] Copying engine files..." -ForegroundColor Cyan
    
    # Copy engine files and runtime config to publish folder for testing
    if (Test-Path "./bin") {
        Copy-Item -Path "./bin" -Destination "$publishDir/bin" -Recurse -Force
    }
    if (Test-Path "./list.txt") {
        Copy-Item -Path "./list.txt" -Destination "$publishDir/list.txt" -Force
    }
    if (Test-Path "./engine_args.txt") {
        Copy-Item -Path "./engine_args.txt" -Destination "$publishDir/engine_args.txt" -Force
    }
    if (Test-Path "./engine_args_coexist.txt") {
        Copy-Item -Path "./engine_args_coexist.txt" -Destination "$publishDir/engine_args_coexist.txt" -Force
    }
    
    Write-Host "[+] Ready to create installer using installer.iss" -ForegroundColor Green
} else {
    Write-Host "[!] Publish failed." -ForegroundColor Red
}
