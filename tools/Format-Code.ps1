param([switch]$Check)
$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    # Limit formatting to repository sources, excluding ignored research, build outputs and local files.
    $fstSourceFiles = @(git ls-files -- '*.cs')
    if ($LASTEXITCODE -ne 0 -or $fstSourceFiles.Count -eq 0) { throw 'Cannot enumerate tracked C# sources' }
    $fstFormatArguments = @('format', 'whitespace', '.', '--folder')
    if ($Check) { $fstFormatArguments += '--verify-no-changes' }
    $fstFormatArguments += '--include'
    $fstFormatArguments += $fstSourceFiles
    & dotnet @fstFormatArguments
    if ($LASTEXITCODE -ne 0) { throw 'C# formatting check failed. Run ./tools/Format-Code.ps1 to apply formatting.' }
} finally {
    Pop-Location
}
