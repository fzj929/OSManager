[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "linux-x64",
    [string]$Version = "1.0.0",
    [string]$OutputDirectory = "artifacts"
)

$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$webRoot = Join-Path $repoRoot "web"
$project = Join-Path $repoRoot "src/OSManager.Api/OSManager.Api.csproj"
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
$packageName = "osmanager-$Version-$Runtime"
$stagingRoot = Join-Path $artifactsRoot $packageName
$appRoot = Join-Path $stagingRoot "app"
$archivePath = Join-Path $artifactsRoot "$packageName.tar.gz"

function Assert-SafeArtifactPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    $prefix = $artifactsRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to operate outside the artifacts directory: $full"
    }
}

Write-Host "[1/5] Installing frontend dependencies" -ForegroundColor Cyan
if (Test-Path (Join-Path $webRoot "package-lock.json")) {
    & npm.cmd ci --prefix $webRoot --cache (Join-Path $webRoot ".npm-cache")
} else {
    & npm.cmd install --prefix $webRoot --cache (Join-Path $webRoot ".npm-cache")
}
if ($LASTEXITCODE -ne 0) { throw "npm install failed" }

Write-Host "[2/5] Building Vue frontend" -ForegroundColor Cyan
& npm.cmd run build --prefix $webRoot
if ($LASTEXITCODE -ne 0) { throw "Frontend build failed" }

New-Item -ItemType Directory -Force -Path $artifactsRoot | Out-Null
Assert-SafeArtifactPath $stagingRoot
if (Test-Path -LiteralPath $stagingRoot) { Remove-Item -LiteralPath $stagingRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path $appRoot | Out-Null

Write-Host "[3/5] Publishing ASP.NET Core (framework-dependent, .NET 8)" -ForegroundColor Cyan
& dotnet restore $project -r $Runtime --configfile (Join-Path $repoRoot "NuGet.Config")
if ($LASTEXITCODE -ne 0) { throw "NuGet restore failed" }
& dotnet publish $project -c $Configuration -r $Runtime --self-contained false --no-restore -o $appRoot /p:Version=$Version
if ($LASTEXITCODE -ne 0) { throw "Backend publish failed" }

Write-Host "[4/5] Assembling Linux installation package" -ForegroundColor Cyan
Copy-Item -LiteralPath (Join-Path $repoRoot "deploy/linux/install.sh") -Destination $stagingRoot
Copy-Item -LiteralPath (Join-Path $repoRoot "deploy/linux/start.sh") -Destination $stagingRoot
Copy-Item -LiteralPath (Join-Path $repoRoot "deploy/linux/stop.sh") -Destination $stagingRoot
Copy-Item -LiteralPath (Join-Path $repoRoot "deploy/linux/restart.sh") -Destination $stagingRoot
Copy-Item -LiteralPath (Join-Path $repoRoot "deploy/linux/status.sh") -Destination $stagingRoot
Copy-Item -LiteralPath (Join-Path $repoRoot "deploy/linux/configure-sudo.sh") -Destination $stagingRoot
Copy-Item -LiteralPath (Join-Path $repoRoot "deploy/linux/grant-path.sh") -Destination $stagingRoot
Copy-Item -LiteralPath (Join-Path $repoRoot "deploy/linux/osmanager.service.template") -Destination $stagingRoot
Copy-Item -LiteralPath (Join-Path $repoRoot "deploy/linux/appsettings.Production.example.json") -Destination $stagingRoot
[IO.File]::WriteAllText((Join-Path $stagingRoot "VERSION"), $Version, (New-Object Text.UTF8Encoding($false)))

Assert-SafeArtifactPath $archivePath
if (Test-Path -LiteralPath $archivePath) { Remove-Item -LiteralPath $archivePath -Force }
Write-Host "[5/5] Creating tar.gz archive" -ForegroundColor Cyan
& tar -czf $archivePath -C $artifactsRoot $packageName
if ($LASTEXITCODE -ne 0) { throw "tar.gz creation failed" }

$size = [Math]::Round((Get-Item -LiteralPath $archivePath).Length / 1MB, 2)
Write-Host ""
Write-Host "Package ready: $archivePath ($size MB)" -ForegroundColor Green
Write-Host "Linux: tar -xzf $(Split-Path $archivePath -Leaf) && cd $packageName && sudo bash install.sh"
