# Капитальная ревизия «мусора»: мёртвый код, неиспользуемые настройки, брошенные
# проекты, отладочные скрипты, TODO, пустые catch и предупреждения сборки.
#
# Зачем: владелец попросил каждые 15 добавлений делать капитальный чек того, что
# накопилось. Скрипт — механическая часть: он НЕ удаляет ничего сам, а показывает
# список кандидатов с доказательством (где определено, сколько раз упомянуто).
# Решения об удалении/починке принимает человек.
#
# Запуск:
#   pwsh -File scripts/cruft-audit.ps1                 # без сборки (быстро)
#   pwsh -File scripts/cruft-audit.ps1 -Build          # + предупреждения компилятора
#   pwsh -File scripts/cruft-audit.ps1 -OutFile ..\отчёт.md
#
# Код возврата всегда 0: это отчёт, а не проверка. Мусор — не повод валить CI.
[CmdletBinding()]
param(
    [switch]$Build,
    [string]$OutFile = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root 'src'
$report = [System.Collections.Generic.List[string]]::new()

function Add-Line([string]$text = '') { $script:report.Add($text) }
function Read-Text([string]$path) {
    $t = Get-Content $path -Raw -Encoding UTF8
    if ($null -eq $t) { return '' }
    return $t
}

# Анализируем ТОЛЬКО проекты из solution: брошенный WinUI-проект иначе подсовывает
# свои одноимённые файлы (IslandViewModel.cs) и портит статистику.
$sln = Read-Text (Join-Path $root 'DotShell.sln')
$slnProjects = @([regex]::Matches($sln, 'src\\[^"]+\.csproj') |
    ForEach-Object { $_.Value.Replace('\', '/') })
$activeDirs = @($slnProjects | ForEach-Object {
    (Join-Path $root ([System.IO.Path]::GetDirectoryName($_))).TrimEnd('\', '/')
})

$allProjectFiles = @(Get-ChildItem (Join-Path $src '*') -Recurse -File -Filter '*.csproj' |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })

function Get-Files([string]$extension) {
    $result = @()
    foreach ($dir in $script:activeDirs) {
        $result += @(Get-ChildItem -Path $dir -Recurse -File -Filter "*.$extension" |
            Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })
    }
    return $result
}

function Get-Relative([string]$full) {
    return [System.IO.Path]::GetRelativePath($root, $full).Replace('\', '/')
}

$csFiles = @(Get-Files 'cs')
$xamlFiles = @(Get-Files 'xaml')
$csText = @{}
foreach ($f in $csFiles) { $csText[$f.FullName] = Read-Text $f.FullName }
$xamlText = @{}
foreach ($f in $xamlFiles) { $xamlText[$f.FullName] = Read-Text $f.FullName }

function Get-Mentions([string]$name) {
    $pattern = '\b' + [regex]::Escape($name) + '\b'
    $count = 0
    foreach ($t in $script:csText.Values) { if ($t) { $count += [regex]::Matches($t, $pattern).Count } }
    foreach ($t in $script:xamlText.Values) { if ($t) { $count += [regex]::Matches($t, $pattern).Count } }
    return $count
}

function Find-File([string]$relative) {
    $full = Join-Path $root ($relative.Replace('/', '\'))
    foreach ($f in $script:csFiles) { if ($f.FullName -eq $full) { return $f } }
    return $null
}

Add-Line '# Капитальная ревизия DotShell'
Add-Line ''
Add-Line ("Дата: " + (Get-Date -Format 'yyyy-MM-dd HH:mm') +
          " · активных исходников: " + $csFiles.Count + " .cs, " + $xamlFiles.Count + " .xaml")
Add-Line ''

# ---------- 1. Брошенные проекты и мусор в корне ----------
Add-Line '## 1. Брошенные проекты и мусор в корне'
Add-Line ''
$orphans = @($allProjectFiles | Where-Object {
    $rel = Get-Relative $_.FullName
    -not ($slnProjects -contains $rel)
})
if ($orphans.Count -eq 0) {
    Add-Line '- брошенных проектов нет'
} else {
    foreach ($p in $orphans) {
        $files = @(Get-ChildItem $p.Directory.FullName -Recurse -File |
            Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })
        Add-Line ("- ПРОЕКТ ВНЕ SOLUTION: " + (Get-Relative $p.FullName) +
                  " (" + $files.Count + " файлов) — либо вернуть в solution, либо удалить")
    }
}
$devFiles = @(Get-ChildItem $root -File | Where-Object { $_.Name -match '^_.*\.(py|ps1|json|txt|md|png)$' })
Add-Line ("- временных/отладочных файлов в корне репозитория: " + $devFiles.Count)
foreach ($f in $devFiles) { Add-Line ("    · " + $f.Name) }
Add-Line ''

# ---------- 2. Мёртвый приватный код ----------
Add-Line '## 2. Мёртвый приватный код (упомянут ровно один раз — в объявлении)'
Add-Line ''
$joinedCs = ($csText.Values | Where-Object { $_ }) -join "`n"
$privateMethods = [regex]::Matches($joinedCs,
    'private\s+(?:static\s+)?(?:async\s+)?(?:readonly\s+)?[\w<>,\[\]\?\. ]+\s+([A-Za-z_]\w*)\s*\(')
$privateFields = [regex]::Matches($joinedCs,
    'private\s+(?:static\s+)?(?:readonly\s+)?[A-Za-z_][\w<>,\[\]\?\. ]*\s+(_\w+)\s*(?:;|=)')
$deadMethods = @()
foreach ($m in $privateMethods) {
    $name = $m.Groups[1].Value
    if ((Get-Mentions $name) -le 1) { $deadMethods += $name }
}
$deadFields = @()
foreach ($m in $privateFields) {
    $name = $m.Groups[1].Value
    if ((Get-Mentions $name) -le 1) { $deadFields += $name }
}
$deadMethods = @($deadMethods | Sort-Object -Unique)
$deadFields = @($deadFields | Sort-Object -Unique)
Add-Line ("- приватных методов-сирот: " + $deadMethods.Count)
foreach ($n in $deadMethods) {
    $where = $csFiles | Where-Object { $csText[$_.FullName] -match ('private[^\n]*\s' + [regex]::Escape($n) + '\s*\(') } |
        ForEach-Object { Get-Relative $_.FullName }
    Add-Line ("    · " + $n + "  (" + ($where -join ', ') + ")")
}
Add-Line ("- приватных полей-сирот: " + $deadFields.Count)
foreach ($n in $deadFields) {
    $where = $csFiles | Where-Object { $csText[$_.FullName] -match ('private[^\n]*\s' + [regex]::Escape($n) + '\s*[;=]') } |
        ForEach-Object { Get-Relative $_.FullName }
    Add-Line ("    · " + $n + "  (" + ($where -join ', ') + ")")
}
Add-Line ''

# ---------- 3. Настройки ----------
Add-Line '## 3. Настройки: мёртвые поля и битые привязки'
Add-Line ''
$appFile = Find-File 'src/DotShell.Core/Settings/AppSettings.cs'
$appText = if ($appFile) { $csText[$appFile.FullName] } else { '' }
$propMatches = [regex]::Matches($appText, 'public\s+[\w<>,\[\]\?\. ]+\s+(\w+)\s*\{\s*get')
$settingProps = @($propMatches | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
$deadSettings = @()
foreach ($p in $settingProps) {
    $count = 0
    foreach ($f in $csText.Keys) {
        if ($f -eq $appFile.FullName) { continue }
        $count += [regex]::Matches($csText[$f], '\b' + [regex]::Escape($p) + '\b').Count
    }
    foreach ($t in $xamlText.Values) {
        $count += [regex]::Matches($t, '\b' + [regex]::Escape($p) + '\b').Count
    }
    if ($count -eq 0) { $deadSettings += $p }
}
Add-Line ("- полей AppSettings без единого упоминания: " + $deadSettings.Count)
foreach ($p in $deadSettings) { Add-Line ("    · " + $p) }

# битые привязки: XAML биндится на поле настройки, а свойства во ViewModel нет
$vmFile = Find-File 'src/DotShell.Island.Wpf/IslandViewModel.cs'
$vmText = if ($vmFile) { $csText[$vmFile.FullName] } else { '' }
$vmProps = @([regex]::Matches($vmText, 'public\s+[\w<>,\[\]\?\. ]+\s+(\w+)\s*(?:\{|=>)') |
    ForEach-Object { $_.Groups[1].Value })
$allXaml = ($xamlText.Values | Where-Object { $_ }) -join "`n"
$bindings = @([regex]::Matches($allXaml, '\{Binding\s+([A-Za-z_]\w*)') |
    ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
$broken = @($bindings | Where-Object {
    ($settingProps -contains $_) -and (-not ($vmProps -contains $_))
})
Add-Line ("- привязок в XAML к полю настройки без свойства во ViewModel (галочка молчит): " + $broken.Count)
foreach ($b in $broken) { Add-Line ("    · " + $b) }

$liveSettings = Join-Path $env:LOCALAPPDATA 'DotShell\settings.json'
if (Test-Path $liveSettings) {
    try {
        $json = Read-Text $liveSettings | ConvertFrom-Json
        $jsonKeys = @($json.PSObject.Properties.Name)
        $stale = @($jsonKeys | Where-Object { $settingProps -notcontains $_ })
        Add-Line ("- ключей в settings.json, которых нет в AppSettings: " + $stale.Count +
                  $(if ($stale.Count -gt 0) { " (" + ($stale -join ', ') + ")" } else { '' }))
    } catch {
        Add-Line '- settings.json прочитать не удалось'
    }
}
Add-Line ''

# ---------- 4. Отладочные флаги ----------
Add-Line '## 4. Флаги командной строки'
Add-Line ''
$progFile = Find-File 'src/DotShell.Island.Wpf/Program.cs'
$progText = if ($progFile) { $csText[$progFile.FullName] } else { '' }
$flags = @([regex]::Matches($progText, '"--([A-Za-z0-9\-]+)') |
    ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
$docs = ''
$docFiles = @(Get-ChildItem $root -File -Filter '*.md') +
            @(Get-ChildItem (Join-Path $root 'docs') -File -Filter '*.md' -ErrorAction SilentlyContinue) +
            @(Get-ChildItem $root -File -Filter 'ПАМЯТЬ.md' -ErrorAction SilentlyContinue)
foreach ($md in $docFiles) { $docs += Read-Text $md.FullName }
$undocumented = @()
foreach ($fl in $flags) {
    if ($docs -notmatch ('--' + [regex]::Escape($fl))) { $undocumented += $fl }
}
Add-Line ("- флагов в Program.cs: " + $flags.Count + "; не упомянуты в документации: " + $undocumented.Count)
foreach ($fl in $undocumented) { Add-Line ("    · --" + $fl) }
Add-Line ''

# ---------- 5. TODO / пустые catch ----------
Add-Line '## 5. Заметки в коде и глушение ошибок'
Add-Line ''
$todos = 0
$emptyCatch = 0
foreach ($t in $csText.Values) {
    if (-not $t) { continue }
    $todos += [regex]::Matches($t, '\b(TODO|FIXME|HACK|XXX)\b').Count
    $emptyCatch += [regex]::Matches($t, 'catch\s*(?:\([^)]*\))?\s*\{\s*\}').Count
}
Add-Line ("- TODO/FIXME/HACK: " + $todos)
Add-Line ("- пустых catch { }: " + $emptyCatch + " (часть — осознанная: чужие окна/процессы)")
Add-Line ''

# ---------- 6. Виджеты ----------
Add-Line '## 6. Виджеты: заявленные типы против реализации'
Add-Line ''
$wmFile = Find-File 'src/DotShell.Island.Wpf/Widgets/WidgetsManager.cs'
if ($wmFile) {
    $wmText = $csText[$wmFile.FullName]
    $typeBlock = [regex]::Match($wmText, 'Types\s*=\s*(?:\r?\n\s*)?\{(.*?)\}', 'Singleline')
    $types = @([regex]::Matches($typeBlock.Groups[1].Value, '"([a-z]+)"') |
        ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
    Add-Line ("- типов в WidgetsManager: " + $types.Count + " (" + ($types -join ', ') + ")")
    $widgetText = ($csFiles | Where-Object { $_.FullName -match '\\Widgets\\' } |
        ForEach-Object { $csText[$_.FullName] }) -join "`n"
    $missing = @($types | Where-Object { $widgetText -notmatch ('"' + $_ + '"') })
    Add-Line ("- типов без обработки в Widgets/: " + $missing.Count +
              $(if ($missing.Count -gt 0) { " (" + ($missing -join ', ') + ")" } else { '' }))
}
Add-Line ''

# ---------- 7. Предупреждения сборки ----------
Add-Line '## 7. Предупреждения сборки'
Add-Line ''
if ($Build) {
    $art = Join-Path $env:TEMP 'dotshell-cruft'
    $buildOut = & dotnet build (Join-Path $src 'DotShell.Island.Wpf\DotShell.Island.Wpf.csproj') -c Debug --no-incremental --artifacts-path $art 2>&1
    $unique = @{}
    foreach ($line in ($buildOut | Select-String -Pattern ': warning ')) {
        $m = [regex]::Match($line.Line, '([^()]+)\((\d+),(\d+)\): warning (CS\d+)')
        if ($m.Success) {
            $rel = Get-Relative $m.Groups[1].Value.Trim()
            $key = $rel + ':' + $m.Groups[2].Value + ' ' + $m.Groups[4].Value
            $unique[$key] = $m.Groups[4].Value
        }
    }
    $groups = $unique.GetEnumerator() | Group-Object { $_.Value } | Sort-Object Name
    Add-Line ("- уникальных предупреждений: " + $unique.Count + " (в выводе MSBuild дублируются через wpftmp)")
    foreach ($g in $groups) {
        Add-Line ("    · " + $g.Name + " ×" + $g.Count)
        foreach ($k in ($g.Group | ForEach-Object { $_.Key } | Sort-Object)) { Add-Line ("        - " + $k) }
    }
} else {
    Add-Line '- сборка не запускалась (флаг -Build)'
}
Add-Line ''

# ---------- вывод ----------
$text = $report -join "`n"
Write-Host $text
if ($OutFile) {
    $full = if ([System.IO.Path]::IsPathRooted($OutFile)) { $OutFile } else { Join-Path $root $OutFile }
    Set-Content -Path $full -Value $text -Encoding UTF8
    Write-Host ("`nотчёт: " + $full)
}
exit 0


