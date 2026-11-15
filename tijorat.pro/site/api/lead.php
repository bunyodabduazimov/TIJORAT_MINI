<?php
// Receives the demo request form and forwards it to Telegram.
// Token and chat id live in config.php next to this file.

header('Content-Type: application/json; charset=utf-8');
header('Cache-Control: no-store');

function out($body, $status = 200) {
    http_response_code($status);
    echo json_encode($body, JSON_UNESCAPED_UNICODE);
    exit;
}

function clean($v, $max) {
    $v = preg_replace('/[\x00-\x1F\x7F]/u', ' ', (string)($v ?? ''));
    return mb_substr(trim($v), 0, $max);
}

function tg($token, $method, $params) {
    $url = "https://api.telegram.org/bot{$token}/{$method}";
    $body = json_encode($params, JSON_UNESCAPED_UNICODE);
    if (function_exists('curl_init')) {
        $ch = curl_init($url);
        curl_setopt_array($ch, [
            CURLOPT_POST => true,
            CURLOPT_POSTFIELDS => $body,
            CURLOPT_HTTPHEADER => ['Content-Type: application/json'],
            CURLOPT_RETURNTRANSFER => true,
            CURLOPT_TIMEOUT => 15,
        ]);
        $res = curl_exec($ch);
        $err = curl_error($ch);
        curl_close($ch);
    } else {
        $ctx = stream_context_create(['http' => [
            'method' => 'POST', 'header' => "Content-Type: application/json\r\n",
            'content' => $body, 'timeout' => 15, 'ignore_errors' => true,
        ]]);
        $res = @file_get_contents($url, false, $ctx);
        $err = $res === false ? 'request failed' : '';
    }
    if ($res === false || $res === null) return ['ok' => false, 'description' => 'network: ' . $err];
    $j = json_decode($res, true);
    return is_array($j) ? $j : ['ok' => false, 'description' => 'bad response'];
}

$cfg = require __DIR__ . '/config.php';
$token = trim($cfg['telegram_bot_token'] ?? '');
$chat = trim((string)($cfg['telegram_chat_id'] ?? ''));
$configured = $token !== '' && strpos($token, 'ВСТАВЬТЕ') === false && $chat !== '';

// Self-check: open api/lead.php?check=1 in a browser.
if ($_SERVER['REQUEST_METHOD'] === 'GET' && isset($_GET['check'])) {
    if (!$configured) out(['ok' => false, 'step' => 'config', 'hint' => 'Заполните api/config.php']);
    $me = tg($token, 'getMe', []);
    if (empty($me['ok'])) out(['ok' => false, 'step' => 'token', 'telegram' => $me['description'] ?? '']);
    $ch = tg($token, 'getChat', ['chat_id' => $chat]);
    if (empty($ch['ok'])) out(['ok' => false, 'step' => 'chat', 'bot' => '@' . $me['result']['username'], 'telegram' => $ch['description'] ?? '',
        'hint' => 'Откройте бота в Telegram и нажмите Start, или добавьте бота в группу и укажите её ID']);
    out(['ok' => true, 'bot' => '@' . $me['result']['username'], 'chat' => $ch['result']['title'] ?? ($ch['result']['first_name'] ?? $chat)]);
}

if ($_SERVER['REQUEST_METHOD'] !== 'POST') out(['ok' => false, 'error' => 'method'], 405);

$d = json_decode(file_get_contents('php://input'), true);
if (!is_array($d)) out(['ok' => false, 'error' => 'bad_json'], 400);

// Hidden field that only bots fill in: pretend success, send nothing.
if (!empty($d['website'])) out(['ok' => true]);

// Simple limit: at most 5 requests per IP in 10 minutes.
$ip = $_SERVER['REMOTE_ADDR'] ?? 'x';
$f = sys_get_temp_dir() . '/tp_lead_' . md5($ip);
$now = time();
$hits = array_filter(array_map('intval', explode(',', @file_get_contents($f) ?: '')), fn($t) => $t > $now - 600);
if (count($hits) >= 5) out(['ok' => false, 'error' => 'rate_limited'], 429);
$hits[] = $now;
@file_put_contents($f, implode(',', $hits));

$name = clean($d['name'] ?? '', 80);
$company = clean($d['company'] ?? '', 120);
$sphere = clean($d['sphere'] ?? '', 80);
$msg = clean($d['msg'] ?? '', 1000);
$lang = clean($d['lang'] ?? 'ru', 5);

$phone = preg_replace('/\D/', '', (string)($d['phone'] ?? ''));
if (strlen($phone) === 9) $phone = '992' . $phone;

if (mb_strlen($name) < 2) out(['ok' => false, 'error' => 'name'], 400);
if (!preg_match('/^992\d{9}$/', $phone)) out(['ok' => false, 'error' => 'phone'], 400);
if (!$configured) out(['ok' => false, 'error' => 'not_configured'], 500);

$e = fn($s) => htmlspecialchars($s, ENT_QUOTES | ENT_HTML5, 'UTF-8');
$pretty = '+992 ' . substr($phone, 3, 2) . ' ' . substr($phone, 5, 3) . ' ' . substr($phone, 8, 2) . ' ' . substr($phone, 10);
$time = (new DateTime('now', new DateTimeZone('Asia/Dushanbe')))->format('d.m.Y H:i');

$lines = ['<b>Новая заявка на демо</b>', '', '<b>Имя:</b> ' . $e($name), '<b>Телефон:</b> ' . $pretty];
if ($company !== '') $lines[] = '<b>Компания:</b> ' . $e($company);
if ($sphere !== '') $lines[] = '<b>Сфера:</b> ' . $e($sphere);
if ($msg !== '') $lines[] = '<b>Что автоматизировать:</b> ' . $e($msg);
$lines[] = '';
$lines[] = 'Язык сайта: ' . strtoupper($e($lang)) . ' · ' . $time;

$r = tg($token, 'sendMessage', ['chat_id' => $chat, 'text' => implode("\n", $lines), 'parse_mode' => 'HTML', 'disable_web_page_preview' => true]);
if (empty($r['ok'])) {
    error_log('TIJORAT lead: Telegram error: ' . ($r['description'] ?? ''));
    out(['ok' => false, 'error' => 'telegram'], 502);
}
out(['ok' => true]);
