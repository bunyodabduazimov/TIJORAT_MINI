<?php
// Общее для страниц панели: сессия, вход, CSRF, оформление.
declare(strict_types=1);
require __DIR__ . '/core.php';

session_name('mt_admin');
session_set_cookie_params(['httponly' => true, 'samesite' => 'Strict', 'secure' => !empty($_SERVER['HTTPS'])]);
session_start();

// Выход при бездействии: 30 минут без запросов → сессия удаляется, нужен повторный вход.
const ADMIN_IDLE_TIMEOUT = 30 * 60;
if (!empty($_SESSION['admin'])) {
    if (time() - ($_SESSION['last_activity'] ?? 0) > ADMIN_IDLE_TIMEOUT) {
        $_SESSION = [];
        session_regenerate_id(true);
        $_SESSION['flash'] = 'Сессия завершена: 30 минут без активности. Войдите снова.';
    } else {
        $_SESSION['last_activity'] = time();
    }
}

function h($v): string { return htmlspecialchars((string)$v, ENT_QUOTES, 'UTF-8'); }

function csrf(): string
{
    $_SESSION['csrf'] ??= bin2hex(random_bytes(16));
    return '<input type="hidden" name="csrf" value="' . $_SESSION['csrf'] . '">';
}

function check_csrf(): void
{
    if ($_SERVER['REQUEST_METHOD'] === 'POST' && !hash_equals($_SESSION['csrf'] ?? '', (string)($_POST['csrf'] ?? ''))) {
        http_response_code(400);
        exit('Сессия устарела, обновите страницу.');
    }
}

function require_login(): void
{
    if (empty($_SESSION['admin'])) { header('Location: login.php'); exit; }
    check_csrf();
}

function redirect(string $url, ?string $flash = null): never
{
    if ($flash) $_SESSION['flash'] = $flash;
    header("Location: $url");
    exit;
}

/** Дата из базы (UTC) → местное время (Душанбе, UTC+5). */
function dt(?string $utc, bool $time = true): string
{
    if (!$utc) return '—';
    $d = new DateTime($utc, new DateTimeZone('UTC'));
    $d->setTimezone(new DateTimeZone('Asia/Dushanbe'));
    return $d->format($time ? 'd.m.y H:i' : 'd.m.Y');
}

/** «5 мин назад», «вчера», «3 дн назад». */
function ago(?string $utc): string
{
    if (!$utc) return '—';
    $s = time() - strtotime($utc . ' UTC');
    if ($s < 60) return 'только что';
    if ($s < 3600) return intdiv($s, 60) . ' мин назад';
    if ($s < 86400) return intdiv($s, 3600) . ' ч назад';
    if ($s < 172800) return 'вчера';
    return intdiv($s, 86400) . ' дн назад';
}

/** Остаток срока лицензии: «∞», «12 дн», «истекла 3 дн назад» — с цветом. */
function days_left(array $l): string
{
    if ($l['expires_at'] === null) return '<span class="dl ok">∞ бессрочно</span>';
    $d = (int)floor((strtotime($l['expires_at'] . ' UTC') - time()) / 86400);
    if ($d < 0) return '<span class="dl bad">истекла ' . (-$d) . ' дн назад</span>';
    $cls = $d <= 3 ? 'bad' : ($d <= 14 ? 'warn' : 'ok');
    return '<span class="dl ' . $cls . '">' . $d . ' дн</span>';
}

function license_badge(array $l): string
{
    if ($l['status'] === 'blocked') return '<span class="badge red">Заблокирована</span>';
    if (is_expired($l)) return '<span class="badge gray">Истекла</span>';
    return '<span class="badge green">Активна</span>';
}

function type_badge(int $type): string
{
    $cls = [LT_TRIAL => 'gray', LT_ANNUAL => 'blue', LT_PERPETUAL => 'violet'][$type] ?? 'gray';
    return '<span class="badge ' . $cls . '">' . LICENSE_TYPES[$type] . '</span>';
}

/** Ключ с кнопкой копирования. */
function key_cell(string $key, ?int $id = null): string
{
    $k = h($key);
    $text = $id ? '<a class="mono" href="license.php?id=' . $id . '">' . $k . '</a>' : '<span class="mono">' . $k . '</span>';
    return '<span class="key">' . $text . '<button type="button" class="copy" title="Копировать" data-copy="' . $k . '">⧉</button></span>';
}

const EVENT_LABELS = [
    'trial_registered'  => ['Новый пробный период', 'green'],
    'trial_resumed'     => ['Пробный продолжен', 'gray'],
    'activated'         => ['Активация ключа', 'blue'],
    'activation_failed' => ['Неверный ключ', 'red'],
    'activation_denied' => ['Лимит устройств', 'orange'],
    'admin_login'       => ['Вход в панель', 'gray'],
    'admin_created'     => ['Создана лицензия', 'violet'],
    'admin_updated'     => ['Изменена лицензия', 'violet'],
    'admin_extended'    => ['Продлена', 'violet'],
    'admin_blocked'     => ['Заблокирована', 'red'],
    'admin_unblocked'   => ['Разблокирована', 'green'],
    'admin_deactivated' => ['Отвязано устройство', 'orange'],
];

function event_label(string $action): string
{
    [$name, $cls] = EVENT_LABELS[$action] ?? [$action, 'gray'];
    return '<span class="ev ' . $cls . '">' . h($name) . '</span>';
}

function page_start(string $title, string $active = ''): void
{
    $nav = [
        'index.php' => ['Обзор', '◧'],
        'licenses.php' => ['Лицензии', '🔑'],
        'customers.php' => ['Клиенты', '👤'],
        'devices.php' => ['Устройства', '🖥'],
        'events.php' => ['Журнал', '☰'],
    ];
    echo '<!doctype html><html lang="ru"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">'
        . '<title>' . h($title) . ' · TIJORAT LITE</title>'
        . '<link rel="icon" type="image/png" href="../img/logo-mark.png">'
        . '<link rel="preconnect" href="https://fonts.googleapis.com"><link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>'
        . '<link href="https://fonts.googleapis.com/css2?family=Onest:wght@400;500;600;700;800&display=swap" rel="stylesheet">'
        . '<link rel="stylesheet" href="../assets/admin.css?v=3"></head><body>';
    if (!empty($_SESSION['admin'])) {
        echo '<aside><a class="brand" href="../"><img class="logo" src="../img/logo-mark.png" alt=""><div>TIJORAT <b>LITE</b><small>Лицензии</small></div></a><nav>';
        foreach ($nav as $url => [$name, $icon])
            echo '<a href="' . $url . '"' . ($active === $url ? ' class="on"' : '') . '><i>' . $icon . '</i>' . $name . '</a>';
        echo '</nav><a class="new" href="licenses.php?new">+ Новая лицензия</a><a class="logout" href="logout.php">Выйти</a></aside>';
    }
    echo '<main' . (empty($_SESSION['admin']) ? ' class="bare"' : '') . '>';
    if (!empty($_SESSION['flash'])) { echo '<div class="flash">' . h($_SESSION['flash']) . '</div>'; unset($_SESSION['flash']); }
}

function page_end(): void
{
    // Копирование ключа по кнопке ⧉
    echo '</main><script>document.addEventListener("click",e=>{const b=e.target.closest("[data-copy]");if(!b)return;'
        . 'navigator.clipboard.writeText(b.dataset.copy);b.textContent="✓";setTimeout(()=>b.textContent="⧉",1200)});</script>';
    // Открытая вкладка сама вернётся на вход, когда серверная сессия истечёт
    if (!empty($_SESSION['admin']))
        echo '<script>setTimeout(()=>location.reload(),' . (ADMIN_IDLE_TIMEOUT + 5) * 1000 . ');</script>';
    echo '</body></html>';
}
