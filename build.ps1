param(
    [string]$NxDir = "",
    [string]$DeployDir = "$env:UGII_USER_DIR\startup",
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release"
)

$ProjectDir = $PSScriptRoot
$ConfigDir  = "$ProjectDir\FlatPatternHighlightConfig"

$Dlls = @(
    @{ Name = "FlatPatternHighlight.dll"; Proj = "FlatPatternHighlight.csproj" },
    @{ Name = "FlatPatternHighlightConfig.dll"; Proj = "FlatPatternHighlightConfig/FlatPatternHighlightConfig.csproj" }
)

Write-Host "=== FlatPatternHighlight Build (Main + Config) ===" -ForegroundColor Cyan
Write-Host ""

# 1. Localizar NX
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

# 2. Pre-requisitos
Write-Host "[1/6] Checking prerequisites..." -ForegroundColor Yellow
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

# 3. Copiar NXSigningResource.res
Write-Host "[2/6] Copying NXSigningResource.res..." -ForegroundColor Yellow
Copy-Item "$nxUfOpenDir\NXSigningResource.res" "$ProjectDir\" -Force
Write-Host "  Done" -ForegroundColor Green

# 4. Patching .csproj HintPaths
Write-Host "[3/6] Patching .csproj HintPaths to $NxDir..." -ForegroundColor Yellow
$nxEscaped = $NxDir.Replace('\', '\\')
foreach ($entry in $Dlls)
{
    $projPath = "$ProjectDir\$($entry.Proj)"
    if (Test-Path $projPath)
    {
        $content = Get-Content $projPath -Raw
        $content = $content -replace 'D:\\\\NX2512', $nxEscaped
        $content = $content -replace 'C:\\\\Program Files\\\\Siemens\\\\NX2512', $nxEscaped
        Set-Content $projPath $content -Encoding UTF8
    }
}
Write-Host "  Done" -ForegroundColor Green

# 5. Build
Write-Host "[4/6] Building plugins ($Configuration)..." -ForegroundColor Yellow
Set-Location $ProjectDir
foreach ($entry in $Dlls)
{
    $projPath = "$ProjectDir\$($entry.Proj)"
    $dllName  = $entry.Name
    Write-Host "  -> $dllName ..." -NoNewline -ForegroundColor DarkGray
    $buildResult = dotnet build "$projPath" -c $Configuration --nologo -v q 2>&1
    if ($LASTEXITCODE -ne 0)
    {
        Write-Host " FAILED" -ForegroundColor Red
        Write-Host $buildResult -ForegroundColor Red
        exit 1
    }
    Write-Host " OK" -ForegroundColor Green
}
Write-Host "  All builds successful" -ForegroundColor Green

# 6. Assinar DLLs
Write-Host "[5/6] Signing DLLs..." -ForegroundColor Yellow
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

# 7. Copiar para diretorio de deploy
Write-Host "[6/6] Copying to deploy directory..." -ForegroundColor Yellow
$deployOk = $true
if ([string]::IsNullOrWhiteSpace($DeployDir))
{
    Write-Warning "  DeployDir not set -- skipping copy."
    $deployOk = $false
}
else
{
    if (-not (Test-Path $DeployDir))
    {
        New-Item -ItemType Directory -Path $DeployDir -Force | Out-Null
    }
    foreach ($entry in $Dlls)
    {
        $projDir = Split-Path "$ProjectDir\$($entry.Proj)" -Parent
        $dllPath = "$projDir\bin\$Configuration\$($entry.Name)"
        if (Test-Path $dllPath)
        {
            Copy-Item $dllPath $DeployDir -Force
            Write-Host "  Copied $($entry.Name) -> $DeployDir" -ForegroundColor Green
        }
        else
        {
            Write-Warning "  $($entry.Name) not found -- skipping"
            $deployOk = $false
        }
    }
    foreach ($ext in @(".men", ".rtb"))
    {
        $src = "$ProjectDir\FlatPatternHighlight$ext"
        if (Test-Path $src)
        {
            Copy-Item $src $DeployDir -Force
            Write-Host "  Copied FlatPatternHighlight$ext -> $DeployDir" -ForegroundColor Green
        }
    }
}

# Resumo
Write-Host ""
Write-Host "=== Build Summary ===" -ForegroundColor Green
foreach ($entry in $Dlls)
{
    $projDir = Split-Path "$ProjectDir\$($entry.Proj)" -Parent
    $dllPath = "$projDir\bin\$Configuration\$($entry.Name)"
    $sizeStr = "?"
    if (Test-Path $dllPath) { $sizeStr = "$((Get-Item $dllPath).Length / 1KB -as [int]) KB" }
    Write-Host "  $($entry.Name)`t$sizeStr" -ForegroundColor $(if (Test-Path $dllPath) { 'Green' } else { 'Red' })
}

if ($anyFail)
{
    Write-Host ""
    Write-Warning "Some DLLs could not be signed. Ctrl+U will still work."
}

if ($deployOk)
{
    Write-Host ""
    Write-Host "Deployed to: $DeployDir" -ForegroundColor Cyan
    if (Test-Path $DeployDir)
    {
        Get-ChildItem $DeployDir -Filter "FlatPatternHighlight*" | ForEach-Object {
            $size = [math]::Round($_.Length / 1KB)
            Write-Host "  $($_.Name)`t${size} KB" -ForegroundColor DarkGray
        }
    }
}
else
{
    Write-Host ""
    Write-Warning "Deploy incomplete -- some files were not copied."
}

Write-Host ""
Write-Host "  For the Config button, use action:" -ForegroundColor Yellow
Write-Host "    FlatPatternHighlightConfig.dll@FlatPatternHighlightConfig.ConfigMain.Main" -ForegroundColor White
