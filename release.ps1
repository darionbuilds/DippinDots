<#
  Builds a Release package and publishes it to the custom Dalamud repo.

  1. Bump <Version> in DippinDots.csproj (Dalamud only offers an update when AssemblyVersion goes up).
  2. Commit your code changes.
  3. Run:  pwsh ./release.ps1            (build, write repo.json, commit, tag, push, GitHub release)
           pwsh ./release.ps1 -DryRun    (build and write repo.json only)

  Needs the .NET 10 SDK (user-local install) and an authenticated `gh`.
#>
param([switch]$DryRun)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$owner = 'darionbuilds/DippinDots'
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'

if (-not $DryRun -and (git status --porcelain -- . ':!repo.json')) {
    throw 'Uncommitted changes. Commit them first so the release matches the tagged source.'
}

& $dotnet build DippinDots.csproj -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }

$zip = 'bin\Release\DippinDots\latest.zip'
$manifest = Get-Content 'bin\Release\DippinDots\DippinDots.json' -Raw | ConvertFrom-Json
$version = $manifest.AssemblyVersion
$tag = "v$version"

if (-not $DryRun -and (git tag --list $tag)) {
    throw "Tag $tag already exists. Bump <Version> in DippinDots.csproj first."
}

# One versioned URL per release, so a cached repo.json never points at a newer zip than it describes.
$url = "https://github.com/$owner/releases/download/$tag/latest.zip"
$entry = [ordered]@{}
foreach ($p in $manifest.PSObject.Properties) { $entry[$p.Name] = $p.Value }
$entry.DownloadLinkInstall = $url
$entry.DownloadLinkUpdate = $url
$entry.DownloadLinkTesting = $url
$entry.IsHide = $false
$entry.IsTestingExclusive = $false
$entry.LastUpdate = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()

ConvertTo-Json -InputObject @([pscustomobject]$entry) -Depth 5 | Set-Content repo.json -Encoding utf8NoBOM
Write-Host "repo.json written for $tag"

if ($DryRun) { Write-Host 'Dry run: nothing committed or published.'; return }

git add repo.json
git commit -m "Release $tag"
git tag $tag
git push origin HEAD $tag
gh release create $tag $zip --repo $owner --title "Dippin' Dots $version" --notes "Dippin' Dots $version"
Write-Host "Published $tag. Dalamud picks it up on its next repo refresh."
