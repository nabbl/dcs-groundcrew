param(
    [string]$Output = "$PSScriptRoot\..\artifacts\windows-x64"
)

$ErrorActionPreference = "Stop"
$Root = Resolve-Path "$PSScriptRoot\.."
$Ui = Join-Path $Root "src\dashboard-ui"
$Api = Join-Path $Root "src\DcsDashboard.Api"
$WwwRoot = Join-Path $Api "wwwroot"

Push-Location $Ui
try {
    npm ci
    npm run build
}
finally { Pop-Location }

if (Test-Path $WwwRoot) { Remove-Item $WwwRoot -Recurse -Force }
New-Item $WwwRoot -ItemType Directory | Out-Null
Copy-Item (Join-Path $Ui "dist\*") $WwwRoot -Recurse

dotnet publish (Join-Path $Api "DcsDashboard.Api.csproj") `
    --configuration Release `
    --runtime win-x64 `
    --self-contained false `
    --output $Output

Write-Host "Published Groundcrew to $Output"
