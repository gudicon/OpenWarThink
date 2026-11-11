param(
    [Parameter(Mandatory = $true)][string]$Destination,
    # Windows: MW2Mercs.exe; Mac: a universal MW2Mercs.app (needs Mac Build Support (Mono) for this editor).
    [ValidateSet('Windows', 'Mac')][string]$Target = 'Windows',
    # Unity 6000.3.14f1 editor: -Unity, else $env:UNITY_EDITOR, else the default Unity Hub location.
    [string]$Unity = $(if ($env:UNITY_EDITOR) { $env:UNITY_EDITOR } else { Join-Path $env:ProgramFiles 'Unity/Hub/Editor/6000.3.14f1/Editor/Unity.exe' })
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dest = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $dest) { throw "Destination must be new: $dest" }
if (-not (Test-Path -LiteralPath $Unity)) { throw "Unity editor is missing: $Unity" }
if ($dest.Contains('"') -or $Unity.Contains('"')) { throw 'Paths cannot contain double quotes' }
$project = Join-Path $dest 'project'
$player = Join-Path $dest 'player'
$log = Join-Path $dest 'unity-build.log'
New-Item -ItemType Directory -Path $project -Force | Out-Null
# Copy only tracked working-tree inputs. No Library, generated MW2Data, saves or installed game data.
$paths = @(& git -C $repo ls-files -- Assets Packages ProjectSettings)
if ($LASTEXITCODE -ne 0) { throw 'git ls-files failed' }
if (-not ($paths -contains 'Assets/MW2/Editor/Mw2PlayerBuild.cs')) { throw 'Track the build entry point before building' }
$sources = foreach ($relative in $paths) {
    $source = Join-Path $repo $relative
    $target = Join-Path $project $relative
    New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $target
    [ordered]@{ path = $relative; sha256 = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash }
}
$manifest = [ordered]@{
    head = (& git -C $repo rev-parse HEAD)
    changes = @(& git -C $repo status --short -- Assets Packages ProjectSettings)
    unity = $Unity
    target = $Target
    sources = @($sources)
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $dest 'inputs.json') -Encoding UTF8
$arguments = '-batchmode -nographics -quit -projectPath "{0}" -executeMethod MW2.EditorTools.Mw2PlayerBuild.{3} --mw2-build-output "{1}" -logFile "{2}"' -f $project, $player, $log, $Target
$process = Start-Process -FilePath $Unity -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait
if ($process.ExitCode -ne 0) { throw "Unity build exited $($process.ExitCode); see $log" }
$entry = if ($Target -eq 'Mac') { 'MW2Mercs.app/Contents/MacOS/MW2Mercs' } else { 'MW2Mercs.exe' }
if (-not (Test-Path -LiteralPath (Join-Path $player $entry))) { throw "Unity did not produce a Player; see $log" }
$files = Get-ChildItem -LiteralPath $player -Recurse -File | Sort-Object FullName | ForEach-Object {
    [ordered]@{ path = $_.FullName.Substring($player.Length + 1).Replace('\', '/'); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
}
ConvertTo-Json -InputObject @($files) -Depth 3 | Set-Content -LiteralPath (Join-Path $dest 'player-files.json') -Encoding UTF8
Write-Output "Player built: $player"
