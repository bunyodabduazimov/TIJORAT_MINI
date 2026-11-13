#!/usr/bin/env bash
# Экспортирует публичный ключ сервера лицензий в клиент. После — пересоберите клиент.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PEM="$(dotnet run --project "$ROOT/src/MiniTrade.LicenseServer" --no-launch-profile -- --export-public-key | sed -n '/-----BEGIN PUBLIC KEY-----/,/-----END PUBLIC KEY-----/p')"
[ -n "$PEM" ] || { echo "Не удалось получить ключ" >&2; exit 1; }
printf '%s\n' "$PEM" > "$ROOT/src/MiniTrade.Desktop/Resources/license-public.pem"
echo "Публичный ключ записан в src/MiniTrade.Desktop/Resources/license-public.pem"
