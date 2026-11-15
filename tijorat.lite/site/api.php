<?php
// Публичный API для программы: POST /api/v1/license/{register|activate|validate}, GET /api/v1/health
declare(strict_types=1);
require __DIR__ . '/lib/core.php';

header('Content-Type: application/json; charset=utf-8');
header('Cache-Control: no-store');

function respond(array $body, int $status = 200): never
{
    http_response_code($status);
    echo json_encode($body, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
    exit;
}

$route = trim((string)($_GET['route'] ?? ''), '/');

try {
    if ($route === 'health') {
        $path = (string)cfg('private_key_path');
        respond([
            'status' => 'ok',
            'time' => gmdate('Y-m-d\TH:i:s\Z'),
            'db' => (bool)db(),
            'php' => PHP_VERSION,
            'openssl' => extension_loaded('openssl'),
            'keyFile' => is_file($path),
            'keyReadable' => is_readable($path),
            'keyValid' => is_readable($path) && openssl_pkey_get_private((string)file_get_contents($path)) !== false,
        ]);
    }

    // Проверка обновлений: самый новый downloads/TijoratLite-Setup-X.Y.Z.exe
    if ($route === 'update') {
        $best = null;
        foreach (glob(__DIR__ . '/downloads/TijoratLite-Setup-*.exe') ?: [] as $file) {
            if (!preg_match('/TijoratLite-Setup-(\d+\.\d+\.\d+)\.exe$/', $file, $m)) continue;
            if ($best === null || version_compare($m[1], $best[0], '>')) $best = [$m[1], $file];
        }
        if ($best === null) respond(['success' => false, 'version' => null]);
        // Хэш считаем один раз и кладём рядом (пересчёт, если файл заменили)
        $hashFile = $best[1] . '.sha256';
        $sha = is_file($hashFile) && filemtime($hashFile) >= filemtime($best[1]) ? trim((string)file_get_contents($hashFile)) : '';
        if ($sha === '') { $sha = hash_file('sha256', $best[1]); @file_put_contents($hashFile, $sha); }
        $scheme =(!empty($_SERVER['HTTPS']) && $_SERVER['HTTPS'] !== 'off') ? 'https' : 'http';
        respond([
            'success' => true,
            'version' => $best[0],
            'url' => $scheme . '://' . $_SERVER['HTTP_HOST'] . '/downloads/' . rawurlencode(basename($best[1])),
            'size' => filesize($best[1]),
            'sha256' => $sha,
        ]);
    }

    if ($_SERVER['REQUEST_METHOD'] !== 'POST') respond(fail('Метод не поддерживается.', 'invalid_request'), 405);
    if (!rate_limit('api', (int)cfg('rate_limit_minute'), 60)) respond(fail('Слишком много попыток. Повторите через минуту.', 'rate_limited'), 429);

    $req = json_decode((string)file_get_contents('php://input'), true);
    if (!is_array($req)) respond(fail('Некорректный запрос.', 'invalid_request'), 400);

    $result = match ($route) {
        'license/register' => api_register($req),
        'license/activate' => api_activate($req),
        'license/validate' => api_validate($req),
        default => respond(fail('Неизвестный адрес API.', 'invalid_request'), 404),
    };

    $status = $result['success'] ? 200 : match ($result['errorCode']) {
        'invalid_request' => 400,
        'key_not_found' => 404,
        default => 403,
    };
    respond($result, $status);
} catch (RuntimeException $e) {
    // Ошибки настройки (ключ подписи) — показываем текст, чтобы было понятно, что исправить
    error_log('MiniTrade license API: ' . $e->getMessage());
    respond(fail($e->getMessage(), 'server_error'), 500);
} catch (Throwable $e) {
    error_log('MiniTrade license API: ' . $e->getMessage());
    respond(fail('Внутренняя ошибка сервера лицензий.', 'server_error'), 500);
}
