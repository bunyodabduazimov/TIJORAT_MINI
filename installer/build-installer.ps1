# Сборка установщика: публикует программу (с .NET внутри) и собирает Setup.exe через Inno Setup.
# Каждая сборка поднимает последнюю цифру версии в Directory.Build.props (1.0.5 → 1.0.6).
# Запуск:  installer\build-installer.ps1              — новая версия (+1)
#          installer\build-installer.ps1 -NoBump      — пересобрать текущую версию
#          installer\build-installer.ps1 -SetVersion 1.1.0 — задать версию вручную
# Результат: installer\output\TijoratPro-Setup-<версия>.exe
param([switch]$NoBump, [string]$SetVersion)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path "$PSScriptRoot\.."
$publish = Join-Path $PSScriptRoot 'publish'
$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Не найден Inno Setup 6 (https://jrsoftware.org/isdl.php)' }

$props = "$root\Directory.Build.props"
$text = [IO.File]::ReadAllText($props)
$m = [regex]::Match($text, '<Version>(\d+)\.(\d+)\.(\d+)</Version>')
if (-not $m.Success) { throw 'В Directory.Build.props не найдена <Version>X.Y.Z</Version>' }
$version = "$($m.Groups[1].Value).$($m.Groups[2].Value).$($m.Groups[3].Value)"
if ($SetVersion) {
    if ($SetVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Версия должна быть вида 1.2.3' }
    $version = $SetVersion
} elseif (-not $NoBump) {
    $version = "$($m.Groups[1].Value).$($m.Groups[2].Value).$([int]$m.Groups[3].Value + 1)"
}
if ($version -ne $m.Value.Substring(9, $m.Value.Length - 19)) {
    $text = $text.Substring(0, $m.Index) + "<Version>$version</Version>" + $text.Substring($m.Index + $m.Length)
    [IO.File]::WriteAllText($props, $text, (New-Object Text.UTF8Encoding $false))
}
Write-Host "Версия: $version" -ForegroundColor Cyan

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish "$root\src\MiniTrade.Desktop" -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o $publish
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish завершился с ошибкой' }
Remove-Item "$publish\*.pdb" -Force -ErrorAction SilentlyContinue

& $iscc "/DAppVersion=$version" "$PSScriptRoot\MiniTrade.iss"
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup завершился с ошибкой' }
Write-Host "Готово: $PSScriptRoot\output\TijoratPro-Setup-$version.exe" -ForegroundColor Green
