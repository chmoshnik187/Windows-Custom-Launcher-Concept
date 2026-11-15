# Публикация GitHub Release для текущей версии DotShell.
#
# Зачем: система обновлений смотрит только на GitHub Releases (`/releases/latest`),
# а не на коммиты в main. Без этого скрипта код может быть в ветке, но пользователи
# не увидят обновления. Скрипт собирает self-contained exe, проверяет его версию,
# упаковывает island/full zip и загружает всё в релиз `vX.Y.Z`.
#
# Запуск:
#   $env:GITHUB_TOKEN = 'github_pat_...'
#   pwsh -File scripts/release.ps1
#
# Версия берётся из Directory.Build.props, описание — из первого раздела CHANGELOG.md
# с этим номером. Перед запуском дерево должно быть синхронизировано с main:
# скрипт проверяет, что версия в main совпадает с локальной.
#
# Параметры:
#   -Token       токен GitHub (по умолчанию GITHUB_TOKEN)
#   -Repository  owner/repo (по умолчанию chmoshnik187/Windows-Custom-Launcher-Concept)
#   -Version     переопределить версию (по умолчанию из Directory.Build.props)
#   -SkipChecks  не запускать smoke.ps1 перед публикацией
#   -DryRun      показать план, ничего не публиковать

[CmdletBinding()]
param(
    [string]$Token = $env:GITHUB_TOKEN,
    [string]$Repository = 'chmoshnik187/Windows-Custom-Launcher-Concept',
    [string]$Version,
    [switch]$SkipChecks,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = Split-Path -Parent $PSScriptRoot
$propsPath = Join-Path $root 'Directory.Build.props'
$changelogPath = Join-Path $root 'CHANGELOG.md'
$publishDir = Join-Path $root 'publish'
$assetsDir = Join-Path $publishDir 'assets'

function Write-Step([string]$text) { Write-Host "`n== $text" -ForegroundColor Cyan }
function Write-Ok([string]$text)   { Write-Host "  ok    $text" -ForegroundColor Green }
function Write-Note([string]$text) { Write-Host "  ...   $text" -ForegroundColor DarkGray }

# --- версия и описание -------------------------------------------------------

if (-not $Version) {
    [xml]$props = Get-Content $propsPath -Raw -Encoding UTF8
    $Version = @($props.Project.PropertyGroup.Version) | Where-Object { $_ } | Select-Object -First 1
}
if (-not $Version) { throw "Не нашёл Version в $propsPath" }

$tag = "v$Version"
$changelog = Get-Content $changelogPath -Raw -Encoding UTF8
$pattern = "(?ms)^##\s+$([regex]::Escape($Version))\b.*?(?=^##\s|\z)"
$section = [regex]::Match($changelog, $pattern)
if (-not $section.Success) {
    throw "В CHANGELOG.md нет раздела «## $Version». Добавь его перед публикацией."
}

$body = ($section.Value -replace '(?m)^---\s*$', '').Trim()
$title = (($section.Value -split "`n")[0] -replace '^##\s*', '').Trim()

Write-Step "релиз $tag"
Write-Ok "версия: $Version"
Write-Ok "заголовок: $title"
Write-Ok "репозиторий: $Repository"

# --- проверка, что main уже содержит эту версию ------------------------------

if (-not $DryRun) {
    $rawProps = "https://raw.githubusercontent.com/$Repository/main/Directory.Build.props"
    try {
        $remote = Invoke-WebRequest -Uri $rawProps -Headers @{ 'User-Agent' = 'DotShell-Release' } `
            -UseBasicParsing -TimeoutSec 60
        $remoteText = $remote.Content
        if ($remoteText -notmatch "<Version>\s*$([regex]::Escape($Version))\s*</Version>") {
            throw " В main версия не $Version. Сначала синхронизируй dotshell/ с main."
        }
        Write-Ok "main уже содержит $Version"
    }
    catch {
        if ($_.Exception.Message -like '*В main версия*') { throw }
        throw "Не смог проверить Directory.Build.props в main: $($_.Exception.Message)"
    }
}

# --- проверки ----------------------------------------------------------------

if (-not $SkipChecks) {
    Write-Step 'smoke'
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'smoke.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'smoke.ps1 завершился с ошибкой' }
    Write-Ok 'smoke PASS'
}

# --- сборка ------------------------------------------------------------------

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

Write-Step 'publish self-contained single-file'
$project = Join-Path $root 'src/DotShell.Island.Wpf/DotShell.Island.Wpf.csproj'
& dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish завершился с ошибкой' }

$exe = Join-Path $publishDir 'DotShell.exe'
if (-not (Test-Path $exe)) { throw "Нет exe после publish: $exe" }
Get-ChildItem $publishDir -Filter '*.pdb' -File | Remove-Item -Force
Write-Ok ("DotShell.exe: {0:N1} МБ" -f ((Get-Item $exe).Length / 1MB))

Write-Step 'проверка версии скачанного exe'
$probe = (& $exe --version-probe | Out-String).Trim()
if ($probe -ne $Version) { throw "exe сообщает «$probe», ожидали «$Version»" }
Write-Ok "exe отвечает $Version"

# --- архивы ------------------------------------------------------------------

function New-ReleaseZip {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [object[]]$Entries
    )

    if (Test-Path $Path) { Remove-Item $Path -Force }
    $zip = [System.IO.Compression.ZipFile]::Open($Path, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($item in $Entries) {
            if (-not (Test-Path $item.FullName)) { throw "Нет файла для архива: $($item.FullName)" }
            $entry = $zip.CreateEntry($item.Name, [System.IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = (Get-Item $item.FullName).LastWriteTime
            $input = [System.IO.File]::OpenRead($item.FullName)
            try {
                $output = $entry.Open()
                try { $input.CopyTo($output) } finally { $output.Dispose() }
            }
            finally { $input.Dispose() }
        }
    }
    finally { $zip.Dispose() }
    Write-Ok ("{0}: {1:N1} МБ" -f (Split-Path -Leaf $Path), ((Get-Item $Path).Length / 1MB))
}

New-Item -ItemType Directory -Path $assetsDir -Force | Out-Null
$installIsland = Join-Path $root 'release/install.bat'
$installFull = Join-Path $root 'release/install-full.bat'
$uninstall = Join-Path $root 'release/uninstall.bat'
$readme = Join-Path $root 'release/KAK-USTANOVIT.txt'

Write-Step 'архивы'
$islandZip = Join-Path $assetsDir "DotShell-$Version-island-win-x64.zip"
$fullZip = Join-Path $assetsDir "DotShell-$Version-full-win-x64.zip"

New-ReleaseZip -Path $islandZip -Entries @(
    [pscustomobject]@{ Name = 'DotShell.exe'; FullName = $exe },
    [pscustomobject]@{ Name = 'install.bat'; FullName = $installIsland },
    [pscustomobject]@{ Name = 'uninstall.bat'; FullName = $uninstall },
    [pscustomobject]@{ Name = 'KAK-USTANOVIT.txt'; FullName = $readme }
)

New-ReleaseZip -Path $fullZip -Entries @(
    [pscustomobject]@{ Name = 'DotShell.exe'; FullName = $exe },
    [pscustomobject]@{ Name = 'install-full.bat'; FullName = $installFull },
    [pscustomobject]@{ Name = 'uninstall.bat'; FullName = $uninstall },
    [pscustomobject]@{ Name = 'KAK-USTANOVIT.txt'; FullName = $readme }
)

$assetFiles = @(
    [pscustomobject]@{ Path = $exe;       ContentType = 'application/octet-stream' },
    [pscustomobject]@{ Path = $islandZip; ContentType = 'application/zip' },
    [pscustomobject]@{ Path = $fullZip;   ContentType = 'application/zip' }
)

# --- GitHub ------------------------------------------------------------------

if ($DryRun) {
    Write-Step 'DryRun: публикация пропущена'
    Write-Host "Будет создан релиз $tag и загружены:"
    $assetFiles | ForEach-Object { Write-Host "  - $(Split-Path -Leaf $_.Path)" }
    return
}

if (-not $Token) { throw 'Нет токена: задай GITHUB_TOKEN или параметр -Token' }

$headers = @{
    Authorization          = "Bearer $Token"
    Accept                 = 'application/vnd.github+json'
    'User-Agent'           = 'DotShell-Release'
    'X-GitHub-Api-Version' = '2022-11-28'
}

function Invoke-GitHub {
    param(
        [Parameter(Mandatory)] [string]$Method,
        [Parameter(Mandatory)] [string]$Path,
        [object]$Payload
    )

    $uri = if ($Path.StartsWith('http')) { $Path } else { "https://api.github.com$Path" }
    $params = @{
        Uri        = $uri
        Method     = $Method
        Headers    = $headers
        TimeoutSec = 900
    }
    if ($null -ne $Payload) {
        $params.Body = ($Payload | ConvertTo-Json -Depth 10 -Compress)
        $params.ContentType = 'application/json'
    }
    Invoke-RestMethod @params
}

Write-Step 'GitHub Release'
$release = $null
try {
    $release = Invoke-GitHub -Method Get -Path "/repos/$Repository/releases/tags/$tag"
    Write-Note "релиз уже существует: $($release.html_url)"
    $release = Invoke-GitHub -Method Patch -Path "/repos/$Repository/releases/$($release.id)" -Payload @{
        name = $title
        body = $body
    }
    Write-Ok 'описание обновлено'
}
catch {
    $status = $null
    try { $status = [int]$_.Exception.Response.StatusCode } catch { }
    if ($status -ne 404) { throw }
}

if (-not $release) {
    $release = Invoke-GitHub -Method Post -Path "/repos/$Repository/releases" -Payload @{
        tag_name         = $tag
        target_commitish = 'main'
        name             = $title
        body             = $body
        draft            = $false
        prerelease       = $false
    }
    Write-Ok "создан релиз $tag"
}

$uploadUrl = ($release.upload_url -replace '\{.*$', '')
$existing = @{}
foreach ($asset in @($release.assets)) {
    if ($asset) { $existing[$asset.name] = $asset }
}

foreach ($item in $assetFiles) {
    $name = Split-Path -Leaf $item.Path
    $size = (Get-Item $item.Path).Length
    $old = $existing[$name]

    if ($old -and [int64]$old.size -eq $size) {
        Write-Note "$name уже загружен ($size байт)"
        continue
    }

    if ($old) {
        Write-Note "$name отличается по размеру — перезаписываю"
        Invoke-GitHub -Method Delete -Path "/repos/$Repository/releases/assets/$($old.id)" | Out-Null
    }

    $uri = "$uploadUrl?name=$([uri]::EscapeDataString($name))"
    Invoke-RestMethod -Uri $uri -Method Post -Headers $headers -ContentType $item.ContentType `
        -InFile $item.Path -TimeoutSec 900 | Out-Null
    Write-Ok "$name загружен ($size байт)"
}

Write-Step 'готово'
Write-Host $release.html_url -ForegroundColor Green
Write-Host "Проверка обновления: установленная 1.10.0/1.11.0 должна увидеть $Version."
