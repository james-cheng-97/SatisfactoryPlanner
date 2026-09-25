# Builds both release flavours into release\:
#   release\standard\SatisfactoryPlanner.exe       small: game data from the wiki on first start; the blueprint writer's
#                                                   library installed with npm on the first export (Node.js + network)
#   release\full\SatisfactoryPlanner-full.exe      offline: game data, icons, names, the writer's library and node.exe built in
# The full build needs Data\ (game data: run the app once and use "Update from wiki", then copy
# %LOCALAPPDATA%\SatisfactoryPlanner\data here, or keep an existing Data\) and bp\node_modules (npm ci in bp\).
param([string]$NodeDir = "$env:ProgramFiles\nodejs")
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not (Test-Path 'bp\node_modules\@etothepii\satisfactory-file-parser')) {
    Push-Location bp; npm ci --no-audit --no-fund; Pop-Location
}
if (-not (Test-Path 'Data\DocsRecipes.json')) { throw 'Data\ is missing: the full build bundles the game data (see the note at the top).' }
if (-not (Test-Path "$NodeDir\node.exe")) { throw "node.exe not found in $NodeDir (pass -NodeDir)." }

Remove-Item release -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish -c Release -o release\standard
if ($LASTEXITCODE) { throw 'standard build failed' }
dotnet publish -c Release -p:Flavor=Full -p:NodeDir="$NodeDir" -o release\full
if ($LASTEXITCODE) { throw 'full build failed' }
Rename-Item release\full\SatisfactoryPlanner.exe SatisfactoryPlanner-full.exe

Get-ChildItem release -Recurse -File | ForEach-Object { '{0,-45} {1,8:N1} MB' -f $_.FullName.Substring($PSScriptRoot.Length + 1), ($_.Length / 1MB) }
