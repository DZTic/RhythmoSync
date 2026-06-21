# Publie RhythmoSync Studio en exécutable portable (auto-contenu, win-x64).
#
# IMPORTANT — NE PAS passer en « fichier unique » (-p:PublishSingleFile=true) :
# dans un exe single-file, le MediaElement WPF n'initialise pas son moteur média
# et la vidéo refuse de jouer. La publication se fait en DOSSIER, avec les DLL
# natives média/WPF (PresentationNative_cor3.dll, wpfgfx_cor3.dll…) externes.
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$proj = Join-Path $root 'src/RhythmoSync.App/RhythmoSync.App.csproj'
$out  = Join-Path $root 'publish/RhythmoSyncStudio-win-x64'
$zip  = Join-Path $root 'publish/RhythmoSyncStudio-win-x64.zip'

if (Test-Path $out) { Remove-Item -Recurse -Force $out }

dotnet publish $proj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:DebugType=none -p:DebugSymbols=false `
    -o $out
if ($LASTEXITCODE -ne 0) { throw "Échec de dotnet publish (code $LASTEXITCODE)." }

if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path $out -DestinationPath $zip -Force

Write-Host ""
Write-Host "Publié  : $out"
Write-Host "Archive : $zip"
