$ErrorActionPreference='Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    dotnet publish src/Fst.App/Fst.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist-next --nologo
    if($LASTEXITCODE -ne 0){throw 'Application build failed'}
    dotnet publish src/Fst.Setup/Fst.Setup.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o release --nologo
    if($LASTEXITCODE -ne 0){throw 'Installer build failed'}
    Get-Item -LiteralPath release/FST-Setup.exe
} finally {Pop-Location}
