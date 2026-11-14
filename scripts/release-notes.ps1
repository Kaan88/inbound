[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    # Where to write the notes; without it they go to the output.
    [string]$OutFile
)

# CI.2: a first draft of the GitHub release notes for a version, from its changelog.json entry. The release workflow
# puts it in the draft release; the summary paragraph, the extension line and Known issues are written by hand there
# before the release is published (docs\release-builds.md). Nothing here says what was or wasn't tested.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$changelog = [IO.File]::ReadAllText((Join-Path $root "src\DownloadManager.Contracts\Changelog\changelog.json")) | ConvertFrom-Json
$entry = @($changelog.versions | Where-Object { $_.version -eq $Version })
if ($entry.Count -ne 1) { throw "changelog.json has no entry for $Version." }

$headings = @{ "New" = "What's new"; "Fixes" = "Fixes"; "Upgrade notes" = "Upgrade notes" }
$lines = [Collections.Generic.List[string]]::new()
$lines.Add("# Inbound Download Manager $Version")
$lines.Add("")
$lines.Add("<!-- Draft from changelog.json. Before publishing: write a one-paragraph summary here, the browser extension line, and Known issues at the end. -->")
foreach ($section in @($entry[0].sections)) {
    $items = @($section.items)
    if ($items.Count -eq 0) { continue }
    $title = if ($headings.ContainsKey($section.title)) { $headings[$section.title] } else { $section.title }
    $lines.Add("")
    $lines.Add("## $title")
    $lines.Add("")
    foreach ($item in $items) { $lines.Add("- " + $item.text) }
}
$text = ($lines -join "`n") + "`n"
if ($OutFile) { [IO.File]::WriteAllText($OutFile, $text, [Text.UTF8Encoding]::new($false)) }
else { $text }
