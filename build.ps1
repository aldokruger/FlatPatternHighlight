param(
    [string]$NxDir = "",
    [string]$DeployDir = "$env:UGII_USER_DIR\startup",
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release"
)

$ProjectDir = $PSScriptRoot
$OutputDir = "$ProjectDir\bin\$Configuration"
$DllName = "FlatPatternHighlight.dll"
$CsprojPath = "$ProjectDir\FlatPatternHighlight.csproj"

Write-Host "=== FlatPatternHighlight Build ===" -ForegroundColor Cyan
Write-Host ""

if ([string]::IsNullOrEmpty($NxDir))
{
    $candidates = @(
        "C:\Program Files\Siemens\NX2512",
        "D:\NX2512",
        "C:\Siemens\NX2512",
        "$env:UGII_BASE_DIR"
    )
    $NxDir = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

if ([string]::IsNullOrEmpty($NxDir) -or -not (Test-Path $NxDir))
{
    Write-Error "NX 2512 not found. Specify -NxDir parameter with the correct path."
    Write-Error "Example: .\build.ps1 -NxDir D:\NX2512"
    exit 1
}

$nxManagedDir = "$NxDir\NXBIN\managed"
$nxUfOpenDir  = "$NxDir\UGOPEN"
$signTool     = "$NxDir\NXBIN\SignDotNet.exe"

Write-Host "NX install: $NxDir" -ForegroundColor DarkGray

Write-Host "[1/4] Checking prerequisites..." -ForegroundColor Yellow

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet)
{
    Write-Error "dotnet SDK not found."
    exit 1
}

$required = @(
    "$nxManagedDir\NXOpen.dll"
    "$nxManagedDir\NXOpenUI.dll"
    "$nxUfOpenDir\NXSigningResource.res"
)

$missing = $required | Where-Object { -not (Test-Path $_) }
if ($missing)
{
    Write-Error "Missing required NX files:"
    $missing | ForEach-Object { Write-Error "  $_" }
    exit 1
}
Write-Host "  All prerequisites OK" -ForegroundColor Green

Write-Host "[2/4] Copying NXSigningResource.res..." -ForegroundColor Yellow
Copy-Item "$nxUfOpenDir\NXSigningResource.res" "$ProjectDir\" -Force
Write-Host "  Done" -ForegroundColor Green

Write-Host "[3/4] Patching .csproj with NX path..." -ForegroundColor Yellow
$csprojContent = Get-Content $CsprojPath -Raw
$csprojContent = $csprojContent -replace 'C:\\Program Files\\Siemens\\NX2512', $NxDir.Replace('\', '\\')
Set-Content $CsprojPath $csprojContent
Write-Host "  HintPath updated to $NxDir" -ForegroundColor Green

Write-Host "[3/4] Building plugin ($Configuration)..." -ForegroundColor Yellow
Set-Location $ProjectDir

$buildResult = dotnet build -c $Configuration --nologo -v q 2>&1
if ($LASTEXITCODE -ne 0)
{
    Write-Error "Build failed:"
    Write-Host $buildResult -ForegroundColor Red
    exit 1
}
Write-Host "  Build OK" -ForegroundColor Green

$dllPath = "$OutputDir\$DllName"
if (-not (Test-Path $dllPath))
{
    Write-Error "Expected DLL not found at: $dllPath"
    exit 1
}

$menFile = "$ProjectDir\FlatPatternHighlight.men"
$rtbFile = "$ProjectDir\FlatPatternHighlight.rtb"

Write-Host "  Signing DLL..." -ForegroundColor Yellow
& $signTool $dllPath 2>&1 | Out-Null
if ($LASTEXITCODE -eq 0)
{
    Write-Host "  Signed OK" -ForegroundColor Green
}
else
{
    Write-Warning "Signing failed (no DotNet Author License?). DLL will work via Ctrl+U."
}

$signedNote = ""
if ($LASTEXITCODE -ne 0) { $signedNote = " (sem assinatura - use Ctrl+U)" }

Write-Host ""
Write-Host "=== Build Successful ===" -ForegroundColor Green
Write-Host "  DLL:      $dllPath$signedNote"
Write-Host "  Size:     $((Get-Item $dllPath).Length / 1KB -as [int]) KB"
Write-Host ""
Write-Host "Install options:" -ForegroundColor Cyan
Write-Host "  1. Ctrl+U: File > Execute > NX Open > select the DLL directly"
Write-Host "  2. Install: Copy to UGII_USER_DIR\startup\ for ribbon + menu auto-load"
Write-Host "     Copy all three files to a startup folder:"
Write-Host "     Copy-Item '$dllPath' 'C:\startup\'" -ForegroundColor DarkGray
Write-Host "     Copy-Item '$menFile' 'C:\startup\'" -ForegroundColor DarkGray
Write-Host "     Copy-Item '$rtbFile' 'C:\startup\'" -ForegroundColor DarkGray
