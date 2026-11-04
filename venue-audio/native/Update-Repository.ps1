param(
    [Parameter(Mandatory=$true)][string]$ManifestPath,
    [Parameter(Mandatory=$true)][string]$RepositoryPath,
    [Parameter(Mandatory=$true)][string]$Tag
)
$ErrorActionPreference = 'Stop'
$entry = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
if ($entry.InternalName -ne 'VenueAudio' -or $entry.DalamudApiLevel -ne 15) { throw 'Unexpected Venue Audio manifest.' }
$download = "https://github.com/NoctusDev/buddys-buddy/releases/download/$Tag/VenueAudio.zip"
foreach ($name in @('DownloadLinkInstall','DownloadLinkUpdate','DownloadLinkTesting')) {
    $entry | Add-Member -NotePropertyName $name -NotePropertyValue $download -Force
}
$entry | Add-Member -NotePropertyName IsHide -NotePropertyValue $false -Force
$entry | Add-Member -NotePropertyName IsTestingExclusive -NotePropertyValue $false -Force
$entries = @(Get-Content -LiteralPath $RepositoryPath -Raw | ConvertFrom-Json | Where-Object InternalName -ne 'VenueAudio') + @($entry)
ConvertTo-Json -InputObject $entries -Depth 20 | Set-Content -LiteralPath $RepositoryPath -Encoding utf8
