param(
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$OutputDirectory,
    [switch]$SkipTests,
    [switch]$Aot
)
$ErrorActionPreference = 'Stop'
if ($Aot -and ($env:OS -ne 'Windows_NT' -or $RuntimeIdentifier -ne 'win-x64')) {
    throw 'The NativeAOT profile requires Windows and the win-x64 runtime identifier.'
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $PSScriptRoot $(if ($Aot) { '..\Build\CrystalStudioAvalonia\aot\win-x64' } else { '..\Build\CrystalStudioAvalonia\win-x64' })
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnetPath = if ($dotnetCommand) { $dotnetCommand.Source } else { Join-Path $env:USERPROFILE '.dotnet\dotnet.exe' }
if (!(Test-Path -LiteralPath $dotnetPath)) { throw '.NET 10 SDK is required.' }

if (!$SkipTests) {
    & $dotnetPath test (Join-Path $PSScriptRoot 'CrystalStudio.Tests\CrystalStudio.Tests.csproj') -c Release -p:UseAppHost=false --nologo
    if ($LASTEXITCODE -ne 0) { throw 'CrystalStudio verification failed.' }
}
if ($Aot) {
    & $dotnetPath publish (Join-Path $PSScriptRoot 'CrystalStudio\CrystalStudio.csproj') -c Release -r $RuntimeIdentifier --self-contained true -p:PublishProfile=NativeAot-win-x64 -o $OutputDirectory --nologo
} else {
    & $dotnetPath publish (Join-Path $PSScriptRoot 'CrystalStudio\CrystalStudio.csproj') -c Release -r $RuntimeIdentifier --self-contained true -p:PublishReadyToRun=true -o $OutputDirectory --nologo
}
if ($LASTEXITCODE -ne 0) { throw 'CrystalStudio publication failed.' }
if (!(Test-Path -LiteralPath (Join-Path $OutputDirectory 'CrystalStudio.exe'))) { throw 'CrystalStudio.exe is missing from the publish output.' }
Write-Host ([System.IO.Path]::GetFullPath((Join-Path $OutputDirectory 'CrystalStudio.exe')))
