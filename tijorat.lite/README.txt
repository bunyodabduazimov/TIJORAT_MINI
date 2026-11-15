TIJORAT LITE — сайт lite.tijorat.pro (сайт + сервер лицензий)

site/ — загрузите ВСЁ содержимое в корень сайта lite.tijorat.pro
        (FastPanel: /var/www/<user>/data/www/lite.tijorat.pro/).

Адреса:
  https://lite.tijorat.pro/             — сайт (скачать, видеоуроки, инструкции)
  https://lite.tijorat.pro/admin/       — панель лицензий
  https://lite.tijorat.pro/api/v1/...   — API лицензий для программы
  https://lite.tijorat.pro/api/v1/health — проверка: {"status":"ok",...,"db":true}

Настройка сервера лицензий (один раз):
  1. PHP 8.1+ (pdo_mysql, openssl, mbstring) и база MySQL/MariaDB.
     Чтобы сохранить уже выданные лицензии — используйте ТУ ЖЕ базу,
     что и на api.tijorat.pro.
  2. lib/config.sample.php → скопировать в lib/config.php и вписать базу.
     (можно просто перенести config.php со старого api.tijorat.pro)
  3. Ключ подписи license-signing.pem положить в lib/keys/ (тот же, что был).
  4. nginx без Apache — добавить блок из nginx.conf.example.
  5. HTTPS: Let's Encrypt.
  6. Проверить, что https://lite.tijorat.pro/lib/config.php даёт 403/404.

Программа обращается к https://lite.tijorat.pro (appsettings.json →
LicenseServerUrl). Уже установленные старые версии ходят на api.tijorat.pro —
не отключайте его, пока все клиенты не обновятся (или настройте редирект
api.tijorat.pro → lite.tijorat.pro для /api/).

Сайт (index.html, блок CONFIG в <script>):
  version / downloadUrl — установщик кладите в site/downloads/
  videos  — ID роликов YouTube (пустой = «Скоро»)
  GUIDES / FAQ — инструкции и вопросы
