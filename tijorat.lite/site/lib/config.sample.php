<?php
// Скопируйте в lib/config.php и заполните. lib/ закрыта от доступа из браузера (.htaccess).
return [
    'db' => [
        'dsn'      => 'mysql:host=localhost;dbname=minitrade_license;charset=utf8mb4',
        'user'     => 'minitrade',
        'password' => 'CHANGE_ME',
    ],
    // Приватный ключ подписи — тот же, чья пара встроена в программу (Resources/license-public.pem).
    // Положите файл в lib/keys/license-signing.pem или укажите полный путь вне папки сайта.
    'private_key_path' => __DIR__ . '/keys/license-signing.pem',

    'trial_days'        => 7,
    'offline_days'      => 30,   // сколько дней токен работает без связи с сервером
    'rate_limit_minute' => 30,   // запросов к API в минуту с одного IP
];
