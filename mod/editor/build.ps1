# Builds BossEditor.html with the game's attack list baked in, and puts it next to
# your boss files (BepInEx\SunLoader). Run it again after the mod writes a new
# attack_types.json, e.g. once you've played more levels.
$ErrorActionPreference = 'Stop'
$game = 'C:\Program Files (x86)\Steam\steamapps\common\Just Shapes & Beats\BepInEx\SunLoader'
$catalog = Get-Content "$game\attack_types.json" -Raw -Encoding UTF8 | ConvertFrom-Json
$seenPath = "$game\attack_types_seen.json"
$seen = if (Test-Path $seenPath) { Get-Content $seenPath -Raw -Encoding UTF8 } else { 'null' }

# Older catalogs list timeline/pos as settings; they're really time and x/y.
foreach ($t in $catalog.attackTypes) {
    $names = @($t.params.PSObject.Properties.Name)
    if ($null -eq $t.usesPosition) { $t | Add-Member usesPosition ($names -contains 'pos') }
    foreach ($k in 'timeline', 'pos') { $t.params.PSObject.Properties.Remove($k) }
}

$html = Get-Content "$PSScriptRoot\editor.html" -Raw -Encoding UTF8
$html = $html.Replace('/*CATALOG*/null', ($catalog | ConvertTo-Json -Depth 6 -Compress))
$html = $html.Replace('/*SEEN*/null', $seen)
[IO.File]::WriteAllText("$game\BossEditor.html", $html, [Text.UTF8Encoding]::new($false))
"Wrote $game\BossEditor.html"

