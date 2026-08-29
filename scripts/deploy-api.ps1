<#
.SYNOPSIS
    Build and deploy the Media Button API + portal to Azure App Service.

.DESCRIPTION
    Does the whole sequence in the order that matters, and verifies the
    result against the live site afterwards.

    This exists because of a 2026-08-28 incident: the API was published
    from a checkout whose gitignored web/dist predated the Reporting tab,
    which silently reverted the live portal. The pre-flight check that was
    run compared web/dist against the committed publish/api/wwwroot - the
    repo's idea of the bundle, not the deployed one. Step 5 below compares
    against the live site instead, which is the only comparison that can
    actually catch this.

.PARAMETER SkipSpaBuild
    Publish without rebuilding the portal bundle. The csproj guard still
    refuses a stale or missing web/dist unless you also pass -Force.

.PARAMETER Force
    Bypass the csproj SPA bundle guard (-p:SkipSpaBundleCheck=true). Only
    for an API-only change where you intend to leave the deployed bundle
    exactly as it is.

.EXAMPLE
    ./scripts/deploy-api.ps1
    ./scripts/deploy-api.ps1 -SkipSpaBuild -Force
#>
[CmdletBinding()]
param(
    [string]$AppName        = 'mediabutton',
    [string]$ResourceGroup  = 'BCHSystems',
    [string]$SiteUrl        = 'https://mediabutton.azurewebsites.net',
    [switch]$SkipSpaBuild,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$repo    = Split-Path -Parent $PSScriptRoot
$webDir  = Join-Path $repo 'web'
$csproj  = Join-Path $repo 'src/MediaButton.Backend.Api/MediaButton.Backend.Api.csproj'
$outDir  = Join-Path ([System.IO.Path]::GetTempPath()) "mediabutton-publish-$(Get-Random)"
$zipPath = Join-Path ([System.IO.Path]::GetTempPath()) "mediabutton-$(Get-Random).zip"

function Step($n, $msg) { Write-Host "`n[$n] $msg" -ForegroundColor Cyan }

# 1. Portal bundle -------------------------------------------------------
if ($SkipSpaBuild) {
    Step 1 'Skipping portal build (-SkipSpaBuild).'
} else {
    Step 1 'Building the portal bundle'
    if (-not (Test-Path (Join-Path $webDir '.env.local'))) {
        throw "web/.env.local is missing. The bundle embeds MSAL auth config at build time; without it sign-in fails with AADSTS900144. Copy web/.env.example and fill it in."
    }
    Push-Location $webDir
    try {
        if (-not (Test-Path 'node_modules')) { npm ci; if ($LASTEXITCODE -ne 0) { throw 'npm ci failed.' } }
        npm run build
        if ($LASTEXITCODE -ne 0) { throw 'npm run build failed.' }
    } finally { Pop-Location }
}

# 2. Publish -------------------------------------------------------------
Step 2 'Publishing the API'
$publishArgs = @($csproj, '-c', 'Release', '-o', $outDir, '--nologo')
if ($Force) { $publishArgs += '-p:SkipSpaBundleCheck=true' }
dotnet publish @publishArgs
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

$localAsset = (Get-ChildItem (Join-Path $outDir 'wwwroot/assets') -Filter 'index-*.js' |
               Select-Object -First 1).Name
if (-not $localAsset) { throw 'No portal bundle in the publish output - refusing to deploy an empty wwwroot.' }
Write-Host "    bundle to deploy: $localAsset"

# 3. Package -------------------------------------------------------------
Step 3 'Packaging'
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $outDir '*') -DestinationPath $zipPath
Write-Host ("    {0:N2} MB" -f ((Get-Item $zipPath).Length / 1MB))

# 4. Deploy --------------------------------------------------------------
Step 4 "Deploying to $AppName"
az webapp deploy --name $AppName --resource-group $ResourceGroup --src-path $zipPath --type zip | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'az webapp deploy failed.' }

# 5. Verify against the LIVE site ---------------------------------------
# App Service recycles asynchronously after a deploy, and the root URL can
# answer 200 from a still-warm instance while /api/info is returning 503.
# Every probe here retries, or the verification reports a failure that is
# really just the app coming back up.
function Invoke-WithRetry {
    param([scriptblock]$Probe, [string]$What, [int]$Attempts = 30, [int]$DelaySeconds = 5)
    for ($i = 1; $i -le $Attempts; $i++) {
        try {
            $result = & $Probe
            if ($result) { return $result }
        } catch { }
        Start-Sleep -Seconds $DelaySeconds
    }
    throw "$What did not come back after $($Attempts * $DelaySeconds)s. Check the App Service."
}

Step 5 'Verifying the live site'
$live = Invoke-WithRetry -What 'The site' -Probe {
    $r = Invoke-WebRequest -Uri $SiteUrl -UseBasicParsing -TimeoutSec 20
    if ($r.StatusCode -eq 200) { $r } else { $null }
}

if ($live.Content -notmatch 'assets/(index-[^"]+\.js)') { throw 'Could not find the bundle reference in the live index.html.' }
$liveAsset = $Matches[1]

if ($liveAsset -ne $localAsset) {
    throw "MISMATCH: the live site serves '$liveAsset' but we deployed '$localAsset'. The deploy did not take effect."
}

$info = Invoke-WithRetry -What '/api/info' -Probe {
    Invoke-RestMethod -Uri "$SiteUrl/api/info" -TimeoutSec 20
}
Write-Host "`nDeployed OK" -ForegroundColor Green
Write-Host "    version : $($info.version)"
Write-Host "    bundle  : $liveAsset (live matches deployed)"

Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
Remove-Item $outDir -Recurse -Force -ErrorAction SilentlyContinue
