param(
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\Build\CrystalStudioAvalonia\win-x64'),
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnetPath = if ($dotnetCommand) { $dotnetCommand.Source } else { Join-Path $env:USERPROFILE '.dotnet\dotnet.exe' }
if (!(Test-Path -LiteralPath $dotnetPath)) { throw '.NET 10 SDK is required.' }

if (!$SkipTests) {
    & $dotnetPath test (Join-Path $PSScriptRoot 'CrystalStudio.Tests\CrystalStudio.Tests.csproj') -c Release -p:UseAppHost=false --nologo
    if ($LASTEXITCODE -ne 0) { throw 'CrystalStudio verification failed.' }
}
& $dotnetPath publish (Join-Path $PSScriptRoot 'CrystalStudio\CrystalStudio.csproj') -c Release -r $RuntimeIdentifier --self-contained true -p:PublishReadyToRun=true -o $OutputDirectory --nologo
if ($LASTEXITCODE -ne 0) { throw 'CrystalStudio publication failed.' }
Write-Host ([System.IO.Path]::GetFullPath((Join-Path $OutputDirectory 'CrystalStudio.exe')))
