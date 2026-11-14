# Сервер лицензий MiniTrade (PHP)

Обычный PHP-сайт: PHP 8.1+ (расширения `pdo_mysql`, `openssl`, `mbstring`) и MySQL/MariaDB.
Таблицы создаются автоматически при первом обращении.

## Установка в FastPanel (api.tijorat.pro)

1. **База данных.** FastPanel → «Базы данных» → создать MySQL-базу и пользователя.
2. **Файлы.** Загрузить содержимое папки `license-site` в корень сайта
   `/var/www/fastuser/data/www/api.tijorat.pro/` (через «Файловый менеджер» FastPanel или SFTP).
3. **Ключ подписи.** Загрузить `src/MiniTrade.LicenseServer/keys/license-signing.pem` в `lib/keys/`.
   Это тот же ключ, чья пара встроена в программу — с другим ключом лицензии программа не примет.
4. **Настройки.** Скопировать `lib/config.sample.php` в `lib/config.php`, вписать имя базы, пользователя и пароль.
5. **Бэкенд сайта.** Сайт → «Бэкенд» → PHP (версия 8.1 или новее). Если выбран режим
   **nginx + PHP-FPM без Apache** — добавить в конфигурацию nginx сайта блок из `nginx.conf.example`
   (с Apache хватает `.htaccess`, он уже в комплекте).
6. **HTTPS.** Сайт → «HTTPS» → Let's Encrypt + перенаправление на HTTPS.
7. **Проверка.**
   - `https://api.tijorat.pro/api/v1/health` → `{"status":"ok",...,"db":true}`
   - `https://api.tijorat.pro/lib/config.php` → должно быть **403/404** (папка закрыта)
   - `https://api.tijorat.pro/admin/` → при первом входе задаётся пароль администратора
8. **Программа.** В `appsettings.json`: `"LicenseServerUrl": "https://api.tijorat.pro"`.

## Адреса API (для программы)
| Метод | Адрес | Назначение |
|---|---|---|
| POST | `/api/v1/license/register` | пробный период (14 дней) |
| POST | `/api/v1/license/activate` | активация ключа |
| POST | `/api/v1/license/validate` | периодическая проверка |
| GET  | `/api/v1/health` | проверка работы сервера |

## Панель администратора `/admin/`
Обзор, лицензии (создание, продление, блокировка, лимит устройств, отвязка устройства),
клиенты, устройства, журнал событий. Защита: пароль (bcrypt), CSRF, ограничение попыток входа.

## Резервная копия
FastPanel → «Базы данных» → экспорт, или `mysqldump <база> > backup.sql`.
