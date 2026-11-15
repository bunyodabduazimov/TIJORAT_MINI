<?php
// Ядро сервера лицензий MiniTrade: база, подпись токенов, регистрация/активация/проверка.
// Формат токена совместим с программой (MiniTrade.Licensing.LicenseTokenCodec):
//   base64url(JSON) "." base64url(подпись ECDSA P-256 / SHA-256 в формате r||s, 64 байта)

declare(strict_types=1);

const LT_TRIAL = 1, LT_ANNUAL = 2, LT_PERPETUAL = 3;
const LICENSE_TYPES = [LT_TRIAL => 'Пробная', LT_ANNUAL => 'Годовая', LT_PERPETUAL => 'Бессрочная'];

function cfg(?string $key = null)
{
    static $cfg = null;
    if ($cfg === null) {
        $file = __DIR__ . '/config.php';
        if (!is_file($file)) {
            http_response_code(500);
            exit('Не найден lib/config.php — скопируйте lib/config.sample.php в lib/config.php и заполните.');
        }
        $cfg = require $file;
    }
    return $key === null ? $cfg : ($cfg[$key] ?? null);
}

function db(): PDO
{
    static $pdo = null;
    if ($pdo === null) {
        $c = cfg('db');
        $pdo = new PDO($c['dsn'], $c['user'], $c['password'], [
            PDO::ATTR_ERRMODE => PDO::ERRMODE_EXCEPTION,
            PDO::ATTR_DEFAULT_FETCH_MODE => PDO::FETCH_ASSOC,
        ]);
        $pdo->exec("SET time_zone = '+00:00'");
        if (!$pdo->query("SHOW TABLES LIKE 'settings'")->fetchColumn()) ensure_schema($pdo);
    }
    return $pdo;
}

/** Таблицы создаются автоматически при первом обращении. */
function ensure_schema(PDO $pdo): void
{
    $pdo->exec(<<<SQL
CREATE TABLE IF NOT EXISTS customers (
  id INT AUTO_INCREMENT PRIMARY KEY,
  full_name VARCHAR(200) NOT NULL,
  company VARCHAR(200) NOT NULL DEFAULT '',
  phone VARCHAR(50) NOT NULL DEFAULT '',
  email VARCHAR(200) NULL,
  notes TEXT NULL,
  created_at DATETIME NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
CREATE TABLE IF NOT EXISTS licenses (
  id INT AUTO_INCREMENT PRIMARY KEY,
  license_key VARCHAR(40) NOT NULL UNIQUE,
  customer_id INT NOT NULL,
  type TINYINT NOT NULL,
  status VARCHAR(20) NOT NULL DEFAULT 'active',
  created_at DATETIME NOT NULL,
  expires_at DATETIME NULL,
  max_devices INT NOT NULL DEFAULT 1,
  block_reason VARCHAR(500) NULL,
  notes TEXT NULL,
  FOREIGN KEY (customer_id) REFERENCES customers(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
CREATE TABLE IF NOT EXISTS devices (
  id INT AUTO_INCREMENT PRIMARY KEY,
  installation_id VARCHAR(64) NOT NULL UNIQUE,
  fingerprint VARCHAR(128) NOT NULL,
  machine_name VARCHAR(200) NOT NULL DEFAULT '',
  app_version VARCHAR(32) NOT NULL DEFAULT '',
  first_seen_at DATETIME NOT NULL,
  last_seen_at DATETIME NOT NULL,
  last_ip VARCHAR(64) NULL,
  INDEX (fingerprint)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
CREATE TABLE IF NOT EXISTS activations (
  id INT AUTO_INCREMENT PRIMARY KEY,
  license_id INT NOT NULL,
  device_id INT NOT NULL,
  activated_at DATETIME NOT NULL,
  last_check_at DATETIME NOT NULL,
  is_active TINYINT(1) NOT NULL DEFAULT 1,
  deactivated_at DATETIME NULL,
  INDEX (license_id), INDEX (device_id),
  FOREIGN KEY (license_id) REFERENCES licenses(id),
  FOREIGN KEY (device_id) REFERENCES devices(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
CREATE TABLE IF NOT EXISTS events (
  id BIGINT AUTO_INCREMENT PRIMARY KEY,
  at DATETIME NOT NULL,
  license_id INT NULL,
  device_id INT NULL,
  action VARCHAR(50) NOT NULL,
  details VARCHAR(1000) NULL,
  ip VARCHAR(64) NULL,
  INDEX (at), INDEX (license_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
CREATE TABLE IF NOT EXISTS rate_limits (
  ip VARCHAR(64) NOT NULL,
  bucket VARCHAR(20) NOT NULL,
  window_start INT NOT NULL,
  hits INT NOT NULL,
  PRIMARY KEY (ip, bucket)
) ENGINE=InnoDB;
CREATE TABLE IF NOT EXISTS settings (
  name VARCHAR(50) PRIMARY KEY,
  value TEXT NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
SQL);
}

function now_utc(): string { return gmdate('Y-m-d H:i:s'); }
function client_ip(): string { return substr($_SERVER['HTTP_X_REAL_IP'] ?? $_SERVER['REMOTE_ADDR'] ?? '', 0, 64); }

function setting(string $name): ?string
{
    $st = db()->prepare('SELECT value FROM settings WHERE name = ?');
    $st->execute([$name]);
    $v = $st->fetchColumn();
    return $v === false ? null : $v;
}

function set_setting(string $name, string $value): void
{
    db()->prepare('REPLACE INTO settings (name, value) VALUES (?, ?)')->execute([$name, $value]);
}

/** Ограничение частоты: true — можно, false — превышен лимит. */
function rate_limit(string $bucket, int $limit, int $windowSec): bool
{
    $ip = client_ip();
    $now = time();
    $pdo = db();
    $st = $pdo->prepare('SELECT window_start, hits FROM rate_limits WHERE ip = ? AND bucket = ?');
    $st->execute([$ip, $bucket]);
    $row = $st->fetch();
    if (!$row || $now - (int)$row['window_start'] >= $windowSec) {
        $pdo->prepare('REPLACE INTO rate_limits (ip, bucket, window_start, hits) VALUES (?, ?, ?, 1)')->execute([$ip, $bucket, $now]);
        return true;
    }
    if ((int)$row['hits'] >= $limit) return false;
    $pdo->prepare('UPDATE rate_limits SET hits = hits + 1 WHERE ip = ? AND bucket = ?')->execute([$ip, $bucket]);
    return true;
}

function log_event(string $action, ?int $licenseId, ?int $deviceId, ?string $details, ?string $ip = null): void
{
    db()->prepare('INSERT INTO events (at, license_id, device_id, action, details, ip) VALUES (?, ?, ?, ?, ?, ?)')
        ->execute([now_utc(), $licenseId, $deviceId, $action, $details === null ? null : mb_substr($details, 0, 1000), $ip ?? client_ip()]);
}

// ===================== Ключи =====================

const KEY_ALPHABET = 'ABCDEFGHJKMNPQRSTUVWXYZ23456789';

function generate_key(): string
{
    do {
        $groups = [];
        for ($g = 0; $g < 4; $g++) {
            $s = '';
            for ($i = 0; $i < 5; $i++) $s .= KEY_ALPHABET[random_int(0, strlen(KEY_ALPHABET) - 1)];
            $groups[] = $s;
        }
        $key = 'MT-' . implode('-', $groups);
        $st = db()->prepare('SELECT 1 FROM licenses WHERE license_key = ?');
        $st->execute([$key]);
    } while ($st->fetchColumn());
    return $key;
}

function normalize_key(?string $key): string { return str_replace(' ', '-', strtoupper(trim((string)$key))); }
function key_well_formed(?string $key): bool { return (bool)preg_match('/^MT(-[A-Z0-9]{5}){4}$/', normalize_key($key)); }

// ===================== Подпись токена =====================

function b64url(string $data): string { return rtrim(strtr(base64_encode($data), '+/', '-_'), '='); }

/** DER-подпись OpenSSL → формат IEEE P1363 (r||s), который ожидает .NET ECDsa.VerifyData. */
function der_to_p1363(string $der, int $partLen = 32): string
{
    $pos = 2;
    if (ord($der[1]) & 0x80) $pos += ord($der[1]) & 0x7f;
    $read = function () use ($der, &$pos) {
        $pos++; // 0x02
        $len = ord($der[$pos++]);
        $v = substr($der, $pos, $len);
        $pos += $len;
        return $v;
    };
    $r = ltrim($read(), "\x00");
    $s = ltrim($read(), "\x00");
    return str_pad($r, $partLen, "\x00", STR_PAD_LEFT) . str_pad($s, $partLen, "\x00", STR_PAD_LEFT);
}

function sign_token(array $payload): string
{
    static $key = null;
    if ($key === null) {
        $pem = @file_get_contents(cfg('private_key_path'));
        $key = $pem ? openssl_pkey_get_private($pem) : false;
        if (!$key) throw new RuntimeException('Не удалось загрузить ключ подписи (private_key_path).');
    }
    $body = json_encode($payload, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
    if (!openssl_sign($body, $der, $key, OPENSSL_ALGO_SHA256)) throw new RuntimeException('Ошибка подписи токена.');
    return b64url($body) . '.' . b64url(der_to_p1363($der));
}

/** Дата для токена: ISO-8601 UTC, как DateTime в .NET. */
function iso(?string $mysqlDate): ?string
{
    return $mysqlDate === null ? null : gmdate('Y-m-d\TH:i:s\Z', strtotime($mysqlDate . ' UTC'));
}

function public_key_pem(): string
{
    $key = openssl_pkey_get_private((string)@file_get_contents(cfg('private_key_path')));
    return $key ? openssl_pkey_get_details($key)['key'] : '';
}

// ===================== Логика лицензий =====================

function is_expired(array $lic): bool
{
    return $lic['expires_at'] !== null && strtotime($lic['expires_at'] . ' UTC') <= time();
}

function fail(string $message, string $code): array
{
    return ['success' => false, 'message' => $message, 'token' => null, 'errorCode' => $code];
}

function ok(string $message, array $lic, array $device): array
{
    $now = time();
    $payload = [
        'key' => $lic['license_key'],
        'type' => (int)$lic['type'],
        'customer' => $lic['full_name'] ?? '',
        'company' => $lic['company'] ?? '',
        'iid' => $device['installation_id'],
        'dfp' => $device['fingerprint'],
        'iat' => gmdate('Y-m-d\TH:i:s\Z', $now),
        'exp' => iso($lic['expires_at']),
        'chk' => gmdate('Y-m-d\TH:i:s\Z', $now + 86400 * (int)cfg('offline_days')),
    ];
    return ['success' => true, 'message' => $message, 'token' => sign_token($payload), 'errorCode' => null];
}

function valid_device($d): bool
{
    return is_array($d)
        && preg_match('/^[0-9a-fA-F]{8}-?([0-9a-fA-F]{4}-?){3}[0-9a-fA-F]{12}$/', (string)($d['installationId'] ?? ''))
        && strlen((string)($d['deviceFingerprint'] ?? '')) >= 16 && strlen((string)$d['deviceFingerprint']) <= 128;
}

function load_license(string $where, array $args): ?array
{
    $st = db()->prepare("SELECT l.*, c.full_name, c.company, c.phone FROM licenses l JOIN customers c ON c.id = l.customer_id WHERE $where");
    $st->execute($args);
    return $st->fetch() ?: null;
}

function upsert_device(array $info): array
{
    $pdo = db();
    $now = now_utc();
    $st = $pdo->prepare('SELECT * FROM devices WHERE installation_id = ?');
    $st->execute([$info['installationId']]);
    $dev = $st->fetch();
    $fields = [$info['deviceFingerprint'], mb_substr((string)($info['machineName'] ?? ''), 0, 200),
        mb_substr((string)($info['appVersion'] ?? ''), 0, 32), $now, client_ip()];
    if ($dev) {
        $pdo->prepare('UPDATE devices SET fingerprint = ?, machine_name = ?, app_version = ?, last_seen_at = ?, last_ip = ? WHERE id = ?')
            ->execute([...$fields, $dev['id']]);
    } else {
        $pdo->prepare('INSERT INTO devices (fingerprint, machine_name, app_version, last_seen_at, last_ip, installation_id, first_seen_at) VALUES (?, ?, ?, ?, ?, ?, ?)')
            ->execute([...$fields, $info['installationId'], $now]);
    }
    $st->execute([$info['installationId']]);
    return $st->fetch();
}

function bind(int $licenseId, int $deviceId): void
{
    $pdo = db();
    $st = $pdo->prepare('SELECT id FROM activations WHERE license_id = ? AND device_id = ? AND is_active = 1');
    $st->execute([$licenseId, $deviceId]);
    if ($id = $st->fetchColumn()) {
        $pdo->prepare('UPDATE activations SET last_check_at = ? WHERE id = ?')->execute([now_utc(), $id]);
    } else {
        $pdo->prepare('INSERT INTO activations (license_id, device_id, activated_at, last_check_at, is_active) VALUES (?, ?, ?, ?, 1)')
            ->execute([$licenseId, $deviceId, now_utc(), now_utc()]);
    }
}

function api_register(array $req): array
{
    $name = trim((string)($req['fullName'] ?? ''));
    $phone = trim((string)($req['phone'] ?? ''));
    if (!valid_device($req['device'] ?? null) || $name === '' || $phone === '')
        return fail('Заполните ФИО и телефон.', 'invalid_request');

    $device = upsert_device($req['device']);
    // Пробный период выдаётся одному устройству (по отпечатку оборудования) только один раз
    $lic = load_license('l.type = ? AND l.id IN (SELECT a.license_id FROM activations a JOIN devices d ON d.id = a.device_id WHERE d.fingerprint = ?) ORDER BY l.id DESC LIMIT 1',
        [LT_TRIAL, $device['fingerprint']]);
    if ($lic) {
        if ($lic['status'] === 'blocked') return fail('Лицензия заблокирована администратором.', 'blocked');
        if (is_expired($lic)) return fail('Пробный период на этом устройстве уже использован. Введите лицензионный ключ.', 'trial_used');
        bind((int)$lic['id'], (int)$device['id']);
        log_event('trial_resumed', (int)$lic['id'], (int)$device['id'], null);
        return ok('Пробный период продолжен.', $lic, $device);
    }

    $pdo = db();
    $company = trim((string)($req['company'] ?? ''));
    $pdo->prepare('INSERT INTO customers (full_name, company, phone, created_at) VALUES (?, ?, ?, ?)')
        ->execute([mb_substr($name, 0, 200), mb_substr($company, 0, 200), mb_substr($phone, 0, 50), now_utc()]);
    $customerId = (int)$pdo->lastInsertId();
    $days = (int)cfg('trial_days');
    $pdo->prepare('INSERT INTO licenses (license_key, customer_id, type, status, created_at, expires_at, max_devices, notes) VALUES (?, ?, ?, ?, ?, ?, 1, ?)')
        ->execute([generate_key(), $customerId, LT_TRIAL, 'active', now_utc(), gmdate('Y-m-d H:i:s', time() + 86400 * $days), 'Создана автоматически при регистрации']);
    $licenseId = (int)$pdo->lastInsertId();
    bind($licenseId, (int)$device['id']);
    log_event('trial_registered', $licenseId, (int)$device['id'], "$name, $company, $phone");
    return ok("Пробный период активирован на $days дней.", load_license('l.id = ?', [$licenseId]), $device);
}

function api_activate(array $req): array
{
    if (!valid_device($req['device'] ?? null) || !key_well_formed($req['licenseKey'] ?? ''))
        return fail('Неверный формат лицензионного ключа.', 'invalid_request');
    $key = normalize_key($req['licenseKey']);
    $lic = load_license('l.license_key = ?', [$key]);
    if (!$lic) {
        log_event('activation_failed', null, null, "Ключ не найден: $key");
        return fail('Лицензионный ключ не найден.', 'key_not_found');
    }
    if ($lic['status'] === 'blocked') return fail('Лицензия заблокирована. Обратитесь к поставщику.', 'blocked');
    if (is_expired($lic)) return fail('Срок действия лицензии истёк.', 'expired');

    $pdo = db();
    $device = upsert_device($req['device']);
    // Тот же компьютер после переустановки (новый ID установки, тот же отпечаток оборудования)
    // занимает своё прежнее место, а не считается вторым устройством
    $pdo->prepare('UPDATE activations a JOIN devices d ON d.id = a.device_id SET a.is_active = 0, a.deactivated_at = ?
        WHERE a.license_id = ? AND a.is_active = 1 AND d.fingerprint = ? AND d.id <> ?')
        ->execute([now_utc(), $lic['id'], $device['fingerprint'], $device['id']]);
    $st = $pdo->prepare('SELECT device_id FROM activations WHERE license_id = ? AND is_active = 1');
    $st->execute([$lic['id']]);
    $active = array_map('intval', $st->fetchAll(PDO::FETCH_COLUMN));
    if (!in_array((int)$device['id'], $active, true) && count($active) >= (int)$lic['max_devices']) {
        log_event('activation_denied', (int)$lic['id'], (int)$device['id'], 'Превышен лимит устройств');
        return fail("Ключ уже активирован на максимальном числе устройств ({$lic['max_devices']}).", 'device_limit');
    }

    // Данные клиента, если ключ выдан без них
    if ($lic['phone'] === '' && trim((string)($req['phone'] ?? '')) !== '')
        $pdo->prepare('UPDATE customers SET phone = ? WHERE id = ?')->execute([trim($req['phone']), $lic['customer_id']]);
    if ($lic['company'] === '' && trim((string)($req['company'] ?? '')) !== '')
        $pdo->prepare('UPDATE customers SET company = ? WHERE id = ?')->execute([trim($req['company']), $lic['customer_id']]);

    // Устройство переходит на новую лицензию — прежние активации (например, триал) закрываем
    $pdo->prepare('UPDATE activations SET is_active = 0, deactivated_at = ? WHERE device_id = ? AND is_active = 1 AND license_id <> ?')
        ->execute([now_utc(), $device['id'], $lic['id']]);
    bind((int)$lic['id'], (int)$device['id']);
    log_event('activated', (int)$lic['id'], (int)$device['id'], $device['machine_name']);
    return ok('Лицензия успешно активирована.', load_license('l.id = ?', [$lic['id']]), $device);
}

function api_validate(array $req): array
{
    if (!valid_device($req['device'] ?? null)) return fail('Некорректный запрос.', 'invalid_request');
    $lic = load_license('l.license_key = ?', [normalize_key($req['licenseKey'] ?? '')]);
    if (!$lic) return fail('Лицензия не найдена.', 'key_not_found');

    $pdo = db();
    $st = $pdo->prepare('SELECT * FROM devices WHERE installation_id = ?');
    $st->execute([$req['device']['installationId']]);
    $device = $st->fetch();
    if (!$device || strcasecmp($device['fingerprint'], (string)$req['device']['deviceFingerprint']) !== 0)
        return fail('Устройство не активировано.', 'not_activated');

    $st = $pdo->prepare('SELECT id FROM activations WHERE license_id = ? AND device_id = ? AND is_active = 1');
    $st->execute([$lic['id'], $device['id']]);
    $actId = $st->fetchColumn();
    if (!$actId) return fail('Активация отозвана администратором.', 'not_activated');
    if ($lic['status'] === 'blocked') return fail(trim('Лицензия заблокирована. ' . ($lic['block_reason'] ?? '')), 'blocked');
    if (is_expired($lic)) return fail('Срок действия лицензии истёк.', 'expired');

    $now = now_utc();
    $pdo->prepare('UPDATE activations SET last_check_at = ? WHERE id = ?')->execute([$now, $actId]);
    $pdo->prepare('UPDATE devices SET last_seen_at = ?, last_ip = ?, app_version = ? WHERE id = ?')
        ->execute([$now, client_ip(), mb_substr((string)($req['device']['appVersion'] ?? ''), 0, 32), $device['id']]);
    return ok('Лицензия действительна.', $lic, $device);
}
