TIJORAT PRO — полный проект сайта

site/     ГОТОВЫЙ САЙТ ДЛЯ СЕРВЕРА. Загрузите содержимое этой папки в корень
          домена (public_html / www). Перед загрузкой заполните
          site/api/config.php (токен бота и ID чата Telegram).
          Подробная инструкция: site/README.txt

source/   ИСХОДНИКИ ДЛЯ РЕДАКТИРОВАНИЯ.
          data.json  — все тексты (RU / EN / TJ), тарифы, цены, контакты,
                       направления, вопросы. Меняйте тексты здесь.
          index.html — дизайн и код сайта, читает data.json.
          img/       — фото направлений (retail, wholesale, mfg, pharmacy,
                       services, rental, construction, education, horeca, auto).
                       Чтобы заменить фото, положите новое с тем же именем
                       (формат .webp, пропорции 4:3).
          api/       — отправка заявок в Telegram (как в site/).

          Папку source/ тоже можно загрузить на сервер как есть: она работает
          так же, как site/, но тексты берутся из data.json.

Просмотр source/ на своём компьютере (index.html читает data.json, поэтому
двойным щелчком не откроется — нужен локальный сервер):
          cd source
          php -S localhost:8000         (или: python -m http.server 8000)
          открыть http://localhost:8000
