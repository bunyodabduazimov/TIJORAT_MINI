# Экспортирует публичный ключ сервера лицензий в клиент (src/MiniTrade.Desktop/Resources/license-public.pem).
# Запускайте с теми же настройками (Licensing:PrivateKeyPath), что и рабочий сервер. После — пересоберите клиент.
$root = Split-Path -Parent $PSScriptRoot
$server = Join-Path $root "src/MiniTrade.LicenseServer"
$target = Join-Path $root "src/MiniTrade.Desktop/Resources/license-public.pem"
$pem = dotnet run --project $server --no-launch-profile -- --export-public-key | Out-String
if ($pem -notmatch "BEGIN PUBLIC KEY") { Write-Error "Не удалось получить ключ:`n$pem"; exit 1 }
$pem = $pem.Substring($pem.IndexOf("-----BEGIN")).Trim()
Set-Content -Path $target -Value $pem -Encoding ascii
Write-Host "Публичный ключ записан в $target"
