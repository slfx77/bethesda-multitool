param(
    [Parameter(Mandatory)][ValidateSet('july','2011')][string]$Build,
    [ValidatePattern('^attempt-[0-9]{3}$')][string]$Attempt,
    [switch]$InspectOnly
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$root = Join-Path $repo 'artifacts/prototype-runtime/xenia'
$run = Join-Path $root "runs/$Build"
$preparation = Get-Content -LiteralPath (Join-Path $run 'preparation.json') -Raw | ConvertFrom-Json
if ($Attempt) {
    $run = Join-Path $run $Attempt
    New-Item -ItemType Directory -Path $run -ErrorAction Stop | Out-Null
    foreach ($kind in @('storage', 'content', 'cache')) {
        $preparation.$kind = Join-Path $run $kind
        New-Item -ItemType Directory -Path $preparation.$kind | Out-Null
    }
}
$executable = Join-Path $root 'source/build/bin/Windows/Release/xenia_canary.exe'
if (-not (Test-Path -LiteralPath $executable)) { throw 'The native emulator build is not present.' }
$guestHash = (Get-FileHash -LiteralPath $preparation.guestCopy -Algorithm SHA256).Hash.ToLowerInvariant()
if ($guestHash -ne $preparation.guestSha256) { throw 'Staged guest changed after preparation.' }
if ((Get-FileHash -LiteralPath $preparation.guestSource -Algorithm SHA256).Hash.ToLowerInvariant() -ne $guestHash) {
    throw 'Original guest changed after preparation.'
}
$arguments = @(
    $preparation.guestCopy,
    '--bmt_runtime=true', '--allow_game_relative_writes=false', '--allow_plugins=false',
    "--storage_root=$($preparation.storage)", "--content_root=$($preparation.content)",
    "--cache_root=$($preparation.cache)", "--log_file=$run/xenia.log",
    '--gpu=d3d12', '--hid=keyboard', '--keyboard_mode=1', '--mute=true'
)
$receipt = [ordered]@{
    preparedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    emulator = $executable
    emulatorSha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant()
    guest = $preparation.guestCopy
    guestSha256 = $guestHash
    arguments = $arguments
    gameRelativeWrites = $false
    status = 'prepared'
}
if ($InspectOnly) { $receipt | ConvertTo-Json -Depth 5; exit 0 }
$receiptPath = Join-Path $run 'launch.json'
if (Test-Path -LiteralPath $receiptPath) { throw 'This run already has a launch receipt; preserve it and use a new run directory.' }
if ($arguments | Where-Object { $_ -match '[\s"]' }) {
    throw 'Launch arguments require quoting; use a staging root without spaces.'
}
$process = Start-Process -FilePath $executable -ArgumentList $arguments -WorkingDirectory $run -WindowStyle Hidden -PassThru
$receipt.status = 'launched-not-validated'
$receipt.processId = $process.Id
$receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $receiptPath -Encoding utf8
$receipt | ConvertTo-Json -Depth 5
