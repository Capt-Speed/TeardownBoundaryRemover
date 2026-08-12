[CmdletBinding()]
param(
    [string] $OutputRoot,
    [string] $DotfuscatorDirectory = $env:TBR_DOTFUSCATOR_DIR
)

$ErrorActionPreference = "Stop"
$repositoryRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $PSScriptRoot "publish\protected-win-x64"
}
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if (!$OutputRoot.StartsWith($repositoryRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputRoot must stay inside the source repository."
}
if ([string]::IsNullOrWhiteSpace($DotfuscatorDirectory)) {
    throw "Dotfuscator Community was not specified. Install it yourself and pass -DotfuscatorDirectory, or set TBR_DOTFUSCATOR_DIR."
}
$DotfuscatorDirectory = [IO.Path]::GetFullPath($DotfuscatorDirectory)
$dotfuscator = Join-Path $DotfuscatorDirectory "dotfuscator.exe"
if (!(Test-Path -LiteralPath $dotfuscator -PathType Leaf)) {
    throw "dotfuscator.exe was not found in: $DotfuscatorDirectory"
}
$toolVersion = (Get-Item -LiteralPath $dotfuscator).VersionInfo.ProductVersion
if ([string]::IsNullOrWhiteSpace($toolVersion) -or !$toolVersion.StartsWith("7.7.0", [StringComparison]::OrdinalIgnoreCase)) {
    throw "This reproducible release expects Dotfuscator Community 7.7.0, but found: $toolVersion"
}

$projectPath = Join-Path $PSScriptRoot "src\TeardownBoundaryRemover\TeardownBoundaryRemover.csproj"
$projectBin = Join-Path $PSScriptRoot "src\TeardownBoundaryRemover\bin\Release\net8.0-windows\win-x64"
$projectAssembly = Join-Path $projectBin "TeardownBoundaryRemover.dll"
$inputDirectory = Join-Path $PSScriptRoot "protect-input"
$protectedDirectory = Join-Path $PSScriptRoot "protect-output"
$mapDirectory = Join-Path $PSScriptRoot "protect-map-private"
$mapPath = Join-Path $mapDirectory "renaming-map.xml"
$savedAssembly = Join-Path $PSScriptRoot "protect-original.dll"
$singleDirectory = Join-Path $OutputRoot "single-file"
$multiDirectory = Join-Path $OutputRoot "multi-file"

foreach ($directory in @($inputDirectory, $protectedDirectory, $mapDirectory, $OutputRoot)) {
    if (Test-Path -LiteralPath $directory) {
        Remove-Item -LiteralPath $directory -Recurse -Force
    }
    New-Item -ItemType Directory -Path $directory | Out-Null
}

dotnet publish $projectPath -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $inputDirectory
if ($LASTEXITCODE -ne 0) { throw "The staging publish failed." }

$inputAssembly = Join-Path $inputDirectory "TeardownBoundaryRemover.dll"
& $dotfuscator "/in:-$inputAssembly" "/out:$protectedDirectory" "/rename:on" "/smart:on" "/suppress:off" "/debug:off" "/mapout:$mapPath" "/clobbermap:on"
if ($LASTEXITCODE -ne 0) { throw "Dotfuscator Community failed." }

$protectedAssembly = Join-Path $protectedDirectory "TeardownBoundaryRemover.dll"
if (!(Test-Path -LiteralPath $protectedAssembly -PathType Leaf)) {
    throw "The protected assembly was not produced."
}

try {
    Copy-Item -LiteralPath $projectAssembly -Destination $savedAssembly -Force
    Copy-Item -LiteralPath $protectedAssembly -Destination $projectAssembly -Force

    New-Item -ItemType Directory -Path $singleDirectory -Force | Out-Null
    dotnet publish $projectPath -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=false --no-build --no-restore -o $singleDirectory
    if ($LASTEXITCODE -ne 0) { throw "The protected single-file publish failed." }

    New-Item -ItemType Directory -Path $multiDirectory -Force | Out-Null
    Get-ChildItem -LiteralPath $inputDirectory -Force | Copy-Item -Destination $multiDirectory -Recurse -Force
    Copy-Item -LiteralPath $protectedAssembly -Destination (Join-Path $multiDirectory "TeardownBoundaryRemover.dll") -Force
}
finally {
    if (Test-Path -LiteralPath $savedAssembly) {
        Copy-Item -LiteralPath $savedAssembly -Destination $projectAssembly -Force
        Remove-Item -LiteralPath $savedAssembly -Force
    }
}

foreach ($directory in @($singleDirectory, $multiDirectory)) {
    $exe = Join-Path $directory "TeardownBoundaryRemover.exe"
    if (!(Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Missing protected executable: $exe" }
    & $exe --self-test
    if ($LASTEXITCODE -ne 0) { throw "Protected self-test failed: $exe" }
}

$unexpectedSingleFiles = @(Get-ChildItem -LiteralPath $singleDirectory -File | Where-Object { $_.Name -ne "TeardownBoundaryRemover.exe" })
foreach ($sidecar in $unexpectedSingleFiles) {
    Remove-Item -LiteralPath $sidecar.FullName -Force
}
if (@(Get-ChildItem -LiteralPath $singleDirectory -File).Count -ne 1) {
    throw "The portable single-file output was not reduced to one executable."
}

$singleHash = (Get-FileHash -LiteralPath (Join-Path $singleDirectory "TeardownBoundaryRemover.exe") -Algorithm SHA256).Hash
$multiExeHash = (Get-FileHash -LiteralPath (Join-Path $multiDirectory "TeardownBoundaryRemover.exe") -Algorithm SHA256).Hash
$multiDllHash = (Get-FileHash -LiteralPath (Join-Path $multiDirectory "TeardownBoundaryRemover.dll") -Algorithm SHA256).Hash
Write-Host "Protected builds completed."
Write-Host "Single-file EXE SHA-256: $singleHash"
Write-Host "Multi-file EXE SHA-256:  $multiExeHash"
Write-Host "Protected DLL SHA-256:   $multiDllHash"
Write-Host "Keep private and do not distribute the renaming map: $mapPath"
