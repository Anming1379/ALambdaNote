$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot

$iscc = 'D:\Program Files (x86)\Inno Setup 6\ISCC.exe'
if (-not (Test-Path -LiteralPath $iscc)) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) {
        $iscc = $command.Source
    }
    else {
        throw 'ISCC.exe not found. Install Inno Setup 6 or edit $iscc in this script.'
    }
}

Get-Process ALambdaNote -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300

$version = '1.1.2'
$project = Join-Path $root 'ALambdaNote.csproj'
[xml]$xml = Get-Content -LiteralPath $project
$versions = @($xml.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })
if ($versions.Count -gt 0) {
    $version = [string]$versions[0]
}

$publish = Join-Path $root 'publish\app'
if (Test-Path -LiteralPath $publish) {
    Remove-Item -LiteralPath $publish -Recurse -Force
}

Write-Host "Publishing ALambda Note $version ..."
dotnet publish $project -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true `
    -p:PublishReadyToRun=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $publish

if ($LASTEXITCODE -ne 0) {
    throw 'dotnet publish failed.'
}

$iss = Join-Path $root 'installer\ALambdaNote.iss'
Write-Host "Compiling installer ..."
& $iscc "/DMyAppVersion=$version" $iss

if ($LASTEXITCODE -ne 0) {
    throw 'ISCC failed.'
}

$output = Join-Path $root "installer\Output\ALambdaNote-Setup-$version.exe"
if (-not (Test-Path -LiteralPath $output)) {
    throw "Installer not found: $output"
}

Write-Host "Done: $output"
