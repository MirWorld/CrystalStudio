param(
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$OutputDirectory,
    [switch]$SkipTests,
    [switch]$Aot
)
$ErrorActionPreference = 'Stop'
$logDirectory = Join-Path $PSScriptRoot 'Build\logs'
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
$logPath = Join-Path $logDirectory ('publish-{0}.log' -f (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
Start-Transcript -LiteralPath $logPath -Force | Out-Null
try {
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
        & $dotnetPath test (Join-Path $PSScriptRoot 'CrystalStudio.Tests\CrystalStudio.Tests.csproj') -c Release -p:UseAppHost=false --nologo | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'CrystalStudio verification failed.' }
    }
    if ($Aot) {
        & $dotnetPath publish (Join-Path $PSScriptRoot 'CrystalStudio\CrystalStudio.csproj') -c Release -r $RuntimeIdentifier --self-contained true -p:PublishProfile=NativeAot-win-x64 -o $OutputDirectory --nologo | Out-Host
    } else {
        & $dotnetPath publish (Join-Path $PSScriptRoot 'CrystalStudio\CrystalStudio.csproj') -c Release -r $RuntimeIdentifier --self-contained true -p:PublishReadyToRun=true -o $OutputDirectory --nologo | Out-Host
    }
    if ($LASTEXITCODE -ne 0) { throw 'CrystalStudio publication failed.' }
    if (!(Test-Path -LiteralPath (Join-Path $OutputDirectory 'CrystalStudio.exe'))) { throw 'CrystalStudio.exe is missing from the publish output.' }
    Write-Host ([System.IO.Path]::GetFullPath((Join-Path $OutputDirectory 'CrystalStudio.exe')))
} catch {
    Write-Host ('[ERROR] {0}' -f $_.Exception.Message)
    throw
} finally {
    Stop-Transcript | Out-Null
    Write-Host ('Publish log: {0}' -f $logPath)
}
