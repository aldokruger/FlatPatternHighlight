param(
    [string]$NxDir = "",
    [string]$DeployDir = "$env:UGII_USER_DIR\startup",
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release"
)

$ProjectDir = $PSScriptRoot
$ConfigDir  = "$ProjectDir\FlatPatternHighlightConfig"
$OutputDir  = "$ProjectDir\bin\$Configuration"
$ConfigOut  = "$ConfigDir\bin\$Configuration"

$Dlls = @(
    @{ Name = "FlatPatternHighlight.dll"; Proj = "FlatPatternHighlight.csproj" },
    @{ Name = "FlatPatternHighlightConfig.dll"; Proj = "FlatPatternHighlightConfig/FlatPatternHighlightConfig.csproj" }
)

Write-Host "=== FlatPatternHighlight Build (Main + Config) ===" -ForegroundColor Cyan
Write-Host ""

# ─────────────────────────────────────────────────────
# 1. Localizar NX
# ─────────────────────────────────────────────────────
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

# ─────────────────────────────────────────────────────
# 2. Pré-requisitos
# ─────────────────────────────────────────────────────
Write-Host "[1/5] Checking prerequisites..." -ForegroundColor Yellow

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

# ─────────────────────────────────────────────────────
# 3. Copiar NXSigningResource.res
# ─────────────────────────────────────────────────────
Write-Host "[2/5] Copying NXSigningResource.res..." -ForegroundColor Yellow
Copy-Item "$nxUfOpenDir\NXSigningResource.res" "$ProjectDir\" -Force
Write-Host "  Done" -ForegroundColor Green

# ─────────────────────────────────────────────────────
# 4. Patching .csproj HintPaths
# ─────────────────────────────────────────────────────
Write-Host "[3/5] Patching .csproj HintPaths to $NxDir..." -ForegroundColor Yellow
$nxEscaped = $NxDir.Replace('\', '\\')
foreach ($entry in $Dlls)
{
    $projPath = "$ProjectDir\$($entry.Proj)"
    if (Test-Path $projPath)
    {
        $content = Get-Content $projPath -Raw
        # Substitui qualquer caminho NX existente pelo atual
        $content = $content -replace 'D:\\\\NX2512', $nxEscaped
        $content = $content -replace 'C:\\\\Program Files\\\\Siemens\\\\NX2512', $nxEscaped
        Set-Content $projPath $content -Encoding UTF8
    }
}
Write-Host "  Done" -ForegroundColor Green

# ─────────────────────────────────────────────────────
# 5. Build
# ─────────────────────────────────────────────────────
Write-Host "[4/5] Building plugins ($Configuration)..." -ForegroundColor Yellow
Set-Location $ProjectDir

foreach ($entry in $Dlls)
{
    $projPath = "$ProjectDir\$($entry.Proj)"
    $dllName  = $entry.Name
    $label = $dllName -replace '\.dll$', ''

    Write-Host "  -> $dllName ..." -NoNewline -ForegroundColor DarkGray

    $buildResult = dotnet build "$projPath" -c $Configuration --nologo -v q 2>&1
    if ($LASTEXITCODE -ne 0)
    {
        Write-Host " FAILED" -ForegroundColor Red
        Write-Host $buildResult -ForegroundColor Red
        exit 1
    }

    # Localizar o DLL output (cada projeto tem seu próprio bin/)
    $projDir = Split-Path $projPath -Parent
    $dllOut = "$projDir\bin\$Configuration\$dllName"

    Write-Host " OK" -ForegroundColor Green
}

Write-Host "  All builds successful" -ForegroundColor Green

# ─────────────────────────────────────────────────────
# 6. Assinar DLLs
# ─────────────────────────────────────────────────────
Write-Host "[5/5] Signing DLLs..." -ForegroundColor Yellow
$anyFail = $false
foreach ($entry in $Dlls)
{
    $projDir = Split-Path "$ProjectDir\$($entry.Proj)" -Parent
    $dllPath = "$projDir\bin\$Configuration\$($entry.Name)"

    if (-not (Test-Path $dllPath))
    {
        Write-Warning "  $($entry.Name) not found at $dllPath"
        $anyFail = $true
        continue
    }

    & $signTool $dllPath 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0)
    {
        Write-Host "  $($entry.Name) signed OK" -ForegroundColor Green
    }
    else
    {
        Write-Warning "  $($entry.Name) signing failed (no DotNet Author License?)"
        $anyFail = $true
    }
}

# ─────────────────────────────────────────────────────
# Resumo
# ─────────────────────────────────────────────────────
Write-Host ""
Write-Host "=== Build Summary ===" -ForegroundColor Green

foreach ($entry in $Dlls)
{
    $projDir = Split-Path "$ProjectDir\$($entry.Proj)" -Parent
    $dllPath = "$projDir\bin\$Configuration\$($entry.Name)"
    $sizeStr = "?"
    if (Test-Path $dllPath) { $sizeStr = "$((Get-Item $dllPath).Length / 1KB -as [int]) KB" }
    Write-Host "  $($entry.Name)`t$sizeStr" -ForegroundColor $(
        if (Test-Path $dllPath) { 'Green' } else { 'Red' }
    )
}

if ($anyFail)
{
    Write-Host ""
    Write-Warning "Some DLLs could not be signed. Ctrl+U will still work."
}

Write-Host ""
Write-Host "Install options:" -ForegroundColor Cyan
Write-Host "  1. Ctrl+U: File > Execute > NX Open > select the DLL directly"
Write-Host "  2. Install: Copy all files below to a startup folder:"
Write-Host ""

foreach ($entry in $Dlls)
{
    $projDir = Split-Path "$ProjectDir\$($entry.Proj)" -Parent
    $dllPath = "$projDir\bin\$Configuration\$($entry.Name)"
    Write-Host "     Copy-Item '$dllPath' '$DeployDir'" -ForegroundColor DarkGray
}
Write-Host "     Copy-Item '$ProjectDir\FlatPatternHighlight.men' '$DeployDir'" -ForegroundColor DarkGray
Write-Host "     Copy-Item '$ProjectDir\FlatPatternHighlight.rtb' '$DeployDir'" -ForegroundColor DarkGray

Write-Host ""
Write-Host "  For the Config button, use action:" -ForegroundColor Yellow
Write-Host "    FlatPatternHighlightConfig.dll@FlatPatternHighlightConfig.ConfigMain.Main" -ForegroundColor White
