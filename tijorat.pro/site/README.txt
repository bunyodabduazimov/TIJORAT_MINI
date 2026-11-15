TIJORAT PRO — сайт для своего сервера

СОСТАВ
  index.html        — сайт (тексты RU / EN / TJ внутри)
  img/              — фото направлений
  api/lead.php      — отправка заявок в Telegram (нужен PHP 7.4+)
  api/config.php    — токен бота и ID чата  <-- ЗАПОЛНИТЬ
  api/.htaccess     — запрет открывать config.php из браузера (Apache)

ШАГ 1. Заполните api/config.php
  'telegram_bot_token' => 'токен от @BotFather',
  'telegram_chat_id'   => 'ID чата или группы (у группы начинается с -100...)',
  Владелец личного чата должен один раз нажать Start у бота.

ШАГ 2. Загрузите ВСЕ файлы в корень сайта
  Хостинг с панелью (cPanel, ISPmanager, Plesk): Файловый менеджер ->
  папка public_html (или www, или папка домена) -> загрузить и распаковать архив.

ШАГ 3. Проверьте
  Откройте https://ВАШ_ДОМЕН/api/lead.php?check=1
  {"ok":true, ...}            — всё готово, заявки будут приходить
  "step":"config"             — не заполнен api/config.php
  "step":"token"              — неверный токен бота
  "step":"chat"               — бот не может писать в чат: нажмите Start у бота
                                 или добавьте бота в группу и укажите её ID
  "network: ..."              — сервер не может выйти в интернет к api.telegram.org

СВОЙ СЕРВЕР (VPS, Ubuntu + Nginx)
  sudo apt install -y nginx php-fpm php-curl php-mbstring
  sudo mkdir -p /var/www/tijorat   и скопируйте туда файлы
  В конфиг сайта nginx добавьте:
      root /var/www/tijorat;
      index index.html;
      location ~ ^/api/config\.php$ { deny all; }
      location ~ \.php$ { include snippets/fastcgi-php.conf; fastcgi_pass unix:/run/php/php-fpm.sock; }
  sudo nginx -t && sudo systemctl reload nginx
  HTTPS: sudo apt install -y certbot python3-certbot-nginx && sudo certbot --nginx -d ВАШ_ДОМЕН
