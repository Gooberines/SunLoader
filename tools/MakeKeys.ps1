# Makes the keys for custom bosses in multiplayer.
#   keys\multiplayer-keys.txt             the keys to give out. Private: git ignores this folder.
#   mod\SunLoader\MultiplayerKeyHashes.cs their hashes, which the mod checks a typed key against.
# Making new keys stops the old ones working, so it won't overwrite them without -Force.
param([int]$Count = 200, [switch]$Force)

$root = Split-Path $PSScriptRoot -Parent
$txt = Join-Path $root 'keys\multiplayer-keys.txt'
$cs = Join-Path $root 'mod\SunLoader\MultiplayerKeyHashes.cs'
if ((Test-Path $txt) -and -not $Force) { throw "$txt already exists. Use -Force to replace every key." }

# No 0/O or 1/I/L, so keys are easy to read out. 20 of these 31 characters can't be guessed.
$alphabet ='ABCDEFGHJKMNPQRSTUVWXYZ23456789'
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
$sha = [Security.Cryptography.SHA256]::Create()

function New-Key {
    $groups = 1..4 | ForEach-Object {
        $chars = 1..5 | ForEach-Object {
            # Throws away values that would favour some letters.
            do { $b = [byte[]]::new(1); $rng.GetBytes($b) } while ($b[0] -ge (256 - (256 % $alphabet.Length)))
            $alphabet[$b[0] % $alphabet.Length]
        }
        -join $chars
    }
    'SUN-' + ($groups -join '-')
}

$keys = @{}
while ($keys.Count -lt $Count) { $keys[(New-Key)] = 1 }
$lines = @($keys.Keys | Sort-Object)

$hashes = $lines | ForEach-Object {
    $normalized = $_ -replace '-', ''
    $bytes = [Text.Encoding]::UTF8.GetBytes("sunloader-multiplayer-v1:$normalized")
    '            "' + (($sha.ComputeHash($bytes) | ForEach-Object { $_.ToString('X2') }) -join '') + '",'
} | Sort-Object

New-Item -ItemType Directory -Force (Split-Path $txt) | Out-Null
[IO.File]::WriteAllLines($txt, [string[]]$lines)
$code = @(
    'using System.Collections.Generic;', '',
    'namespace SunLoader', '{',
    '    // Made by tools\MakeKeys.ps1. Hashes only: the keys themselves are not in this repository.',
    '    static class MultiplayerKeyHashes', '    {',
    '        public static readonly HashSet<string> All = new()', '        {'
) + $hashes + @('        };', '    }', '}')
[IO.File]::WriteAllLines($cs, [string[]]$code)
"Made $($lines.Count) keys: $txt"
