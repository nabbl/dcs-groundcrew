param(
    [Parameter(Mandatory = $true)]
    [string]$InstallDirectory,

    [Parameter(Mandatory = $true)]
    [string]$TailscaleIp,

    [int]$Port = 5080
)

$ErrorActionPreference = "Stop"
$ServiceName = "DcsGroundcrew"
$Executable = Join-Path $InstallDirectory "Groundcrew.exe"

if (-not (Test-Path $Executable)) {
    throw "Groundcrew.exe was not found in $InstallDirectory. Run publish-windows.ps1 first."
}

$BinaryPath = "`"$Executable`" --service --urls `"http://${TailscaleIp}:$Port`""
$Existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($Existing) {
    Stop-Service $ServiceName -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
}

New-Service -Name $ServiceName `
    -BinaryPathName $BinaryPath `
    -DisplayName "DCS Groundcrew" `
    -Description "Browser control plane for the local DCS dedicated server and companion services." `
    -StartupType Automatic

$Rule = Get-NetFirewallRule -DisplayName "DCS Groundcrew (Tailscale)" -ErrorAction SilentlyContinue
if (-not $Rule) {
    New-NetFirewallRule -DisplayName "DCS Groundcrew (Tailscale)" `
        -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port `
        -RemoteAddress "100.64.0.0/10" | Out-Null
}

Start-Service $ServiceName
Write-Host "Groundcrew is running at http://${TailscaleIp}:$Port"
