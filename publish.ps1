[CmdletBinding()]
param(
  [ValidateSet("patch", "minor", "major")]
  [string]$Bump,

  # Republish the version already in Directory.Build.props WITHOUT bumping.
  # With --skip-duplicate this is a safe no-op if that version is already on NuGet.
  # Use it when the bump landed in an earlier commit (the common case) so the
  # published version matches what git records.
  [switch]$NoBump,

  # Optional. If omitted, NUGET_API_KEY is read from SaaS/.env.local (two dirs up).
  [string]$ApiKey
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-VersionFromPropsFile {
  param([Parameter(Mandatory = $true)][string]$PropsPath)

  $text = Get-Content -Path $PropsPath -Raw
  $match = [regex]::Match($text, "<Version>(?<version>[^<]+)</Version>")
  if (-not $match.Success) {
    throw "Could not find <Version> in $PropsPath"
  }

  return $match.Groups["version"].Value.Trim()
}

function Get-BumpedVersion {
  param(
    [Parameter(Mandatory = $true)][string]$CurrentVersion,
    [Parameter(Mandatory = $true)][ValidateSet("patch", "minor", "major")][string]$Bump
  )

  if ($CurrentVersion -notmatch "^(\d+)\.(\d+)\.(\d+)$") {
    throw "Unsupported version format '$CurrentVersion'. Expected 'major.minor.patch'."
  }

  $major = [int]$Matches[1]
  $minor = [int]$Matches[2]
  $patch = [int]$Matches[3]

  switch ($Bump) {
    "patch" {
      $patch++
    }
    "minor" {
      $minor++
      $patch = 0
    }
    "major" {
      $major++
      $minor = 0
      $patch = 0
    }
  }

  return "$major.$minor.$patch"
}

function Set-VersionInPropsFile {
  param(
    [Parameter(Mandatory = $true)][string]$PropsPath,
    [Parameter(Mandatory = $true)][string]$Version
  )

  $assemblyAndFileVersion = "$Version.0"

  $text = Get-Content -Path $PropsPath -Raw
  $text = $text -replace "<Version>[^<]+</Version>", "<Version>$Version</Version>"

  if ($text -match "<AssemblyVersion>[^<]+</AssemblyVersion>") {
    $text = $text -replace "<AssemblyVersion>[^<]+</AssemblyVersion>", "<AssemblyVersion>$assemblyAndFileVersion</AssemblyVersion>"
  }

  if ($text -match "<FileVersion>[^<]+</FileVersion>") {
    $text = $text -replace "<FileVersion>[^<]+</FileVersion>", "<FileVersion>$assemblyAndFileVersion</FileVersion>"
  }

  Set-Content -Path $PropsPath -Value $text -Encoding utf8
}

function Get-PackageIdFromPropsFile {
  param([Parameter(Mandatory = $true)][string]$PropsPath)

  # Check Directory.Build.props first
  $text = Get-Content -Path $PropsPath -Raw
  $match = [regex]::Match($text, "<PackageId>(?<id>[^<]+)</PackageId>")
  if ($match.Success) {
    return $match.Groups["id"].Value.Trim()
  }

  # Check .csproj files in src directory
  $repoRoot = Split-Path -Parent $PropsPath
  $srcPath = Join-Path $repoRoot "src"
  if (Test-Path $srcPath) {
    $csprojFiles = @(Get-ChildItem -Path $srcPath -Recurse -Filter "*.csproj" -File -ErrorAction SilentlyContinue)
    foreach ($csproj in $csprojFiles) {
      $csprojText = Get-Content -Path $csproj.FullName -Raw
      $csprojMatch = [regex]::Match($csprojText, "<PackageId>(?<id>[^<]+)</PackageId>")
      if ($csprojMatch.Success) {
        return $csprojMatch.Groups["id"].Value.Trim()
      }
    }
  }

  # If no PackageId is specified, derive from directory name
  $dirName = Split-Path -Leaf $repoRoot
  return $dirName
}

# Main script
$repoRoot = $PSScriptRoot
$propsPath = Join-Path $repoRoot "Directory.Build.props"
if (-not (Test-Path $propsPath)) {
  throw "Missing file: $propsPath"
}

if (-not $Bump -and -not $NoBump) {
  throw "Specify -Bump <patch|minor|major> to bump+publish, or -NoBump to publish the current version."
}

$packageId = Get-PackageIdFromPropsFile -PropsPath $propsPath
$currentVersion = Get-VersionFromPropsFile -PropsPath $propsPath

# Resolve the API key: explicit -ApiKey wins, else read NUGET_API_KEY from SaaS/.env.local.
if (-not $ApiKey) {
  $saasRoot = Split-Path -Parent (Split-Path -Parent $repoRoot)
  $envFile = Join-Path $saasRoot ".env.local"
  if (Test-Path $envFile) {
    $keyLine = Select-String -Path $envFile -Pattern '^NUGET_API_KEY=(.+)$' | Select-Object -First 1
    if ($keyLine) { $ApiKey = $keyLine.Matches.Groups[1].Value.Trim() }
  }
  if (-not $ApiKey) {
    throw "No -ApiKey given and NUGET_API_KEY not found in $envFile"
  }
}

if ($NoBump) {
  $targetVersion = $currentVersion
} else {
  $targetVersion = Get-BumpedVersion -CurrentVersion $currentVersion -Bump $Bump
}

Write-Host "Building and publishing $packageId package..." -ForegroundColor Cyan
if ($NoBump) {
  Write-Host "Version: $targetVersion (republish, no bump)"
} else {
  Write-Host "Version: $currentVersion -> $targetVersion"
}
Write-Host ""

# Update version (only when bumping; -NoBump ships what's already in props)
if (-not $NoBump) {
  Set-VersionInPropsFile -PropsPath $propsPath -Version $targetVersion
}

try {
  # Find solution or project file
  $slnFiles = @(Get-ChildItem -Path $repoRoot -Filter "*.sln" -File -ErrorAction SilentlyContinue)
  if ($slnFiles.Count -eq 1) {
    $packTarget = $slnFiles[0].FullName
  } else {
    $srcPath = Join-Path $repoRoot "src"
    $csprojFiles = @(
      Get-ChildItem -Path $srcPath -Recurse -Filter "*.csproj" -File -ErrorAction SilentlyContinue |
      Where-Object { $_.FullName -notmatch "\\(bin|obj)\\" }
    )
    if ($csprojFiles.Count -eq 1) {
      $packTarget = $csprojFiles[0].FullName
    } else {
      throw "Could not find a single .sln or .csproj file"
    }
  }

  $artifactsDir = Join-Path $repoRoot "artifacts"
  New-Item -ItemType Directory -Force -Path $artifactsDir | Out-Null

  # Step 0: Clean — wipe bin/obj so the Build step rebuilds from source.
  # Without this, dotnet's incremental cache can skip CoreCompile when output
  # timestamps already exceed the (edited) source timestamps, silently packing
  # a stale DLL into the new-version nupkg. Bit us during the Phase 3 cutover
  # (Bff.AspNetCore@1.2.1 shipped without the X-Realm fix; 1.2.2 was burned to
  # work around it). A few seconds of clean buys deterministic builds.
  Write-Host "Step 0: Cleaning previous outputs..." -ForegroundColor Yellow
  dotnet clean $packTarget -c Release --verbosity quiet | Out-Null
  if ($LASTEXITCODE -ne 0) { throw "dotnet clean failed" }
  # Belt + suspenders: also wipe bin/obj that `dotnet clean` may leave behind
  # when it falls back to a project-level rather than file-level clean.
  Get-ChildItem -Path $repoRoot -Recurse -Force -Directory -ErrorAction SilentlyContinue `
    | Where-Object { $_.Name -in @('bin','obj') } `
    | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
  Write-Host ""

  # Step 1: Restore
  Write-Host "Step 1: Restoring dependencies..." -ForegroundColor Yellow
  dotnet restore $packTarget
  if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed" }

  # Step 2: Build
  Write-Host ""
  Write-Host "Step 2: Building..." -ForegroundColor Yellow
  dotnet build $packTarget -c Release --no-restore
  if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }

  # Step 3: Pack
  Write-Host ""
  Write-Host "Step 3: Packing..." -ForegroundColor Yellow
  dotnet pack $packTarget -c Release -o $artifactsDir --no-build /p:ContinuousIntegrationBuild=true
  if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed" }

  # Step 4: Push
  Write-Host ""
  Write-Host "Step 4: Pushing version $targetVersion to NuGet.org..." -ForegroundColor Yellow
  $nupkgPath = Join-Path $artifactsDir "$packageId.$targetVersion.nupkg"

  if (-not (Test-Path $nupkgPath)) {
    throw "Package not found: $nupkgPath"
  }

  dotnet nuget push $nupkgPath --api-key $ApiKey --source "https://api.nuget.org/v3/index.json" --skip-duplicate

  if ($LASTEXITCODE -eq 0) {
    Write-Host ""
    Write-Host "Successfully published $packageId $targetVersion to NuGet.org!" -ForegroundColor Green
  } else {
    $exitCode = $LASTEXITCODE
    throw "Failed to push package (exit code: $exitCode)"
  }
}
catch {
  if (-not $NoBump) {
    # Rollback version on failure (only if we changed it)
    Set-VersionInPropsFile -PropsPath $propsPath -Version $currentVersion
    Write-Warning "Rolled back version change in Directory.Build.props due to failure."
  }
  throw
}
