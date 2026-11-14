<?php
require __DIR__ . '/../lib/admin.php';
require_login();
$pdo = db();
$id = (int)($_GET['id'] ?? 0);
$lic = load_license('l.id = ?', [$id]) ?? redirect('licenses.php', 'Лицензия не найдена.');

if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    switch ($_POST['action'] ?? '') {
        case 'save':
            $type = (int)$_POST['type'];
            $expires = $type === LT_PERPETUAL || ($_POST['expires_at'] ?? '') === ''
                ? null : gmdate('Y-m-d H:i:s', strtotime($_POST['expires_at'] . ' 23:59:59 Asia/Dushanbe'));
            $pdo->prepare('UPDATE licenses SET type = ?, expires_at = ?, max_devices = ?, notes = ? WHERE id = ?')
                ->execute([$type, $expires, max(1, (int)$_POST['max_devices']), trim($_POST['notes'] ?? '') ?: null, $id]);
            log_event('admin_updated', $id, null, LICENSE_TYPES[$type] . ', до ' . ($expires ? dt($expires, false) : '∞') . ", устройств {$_POST['max_devices']}", 'admin');
            redirect("license.php?id=$id", 'Сохранено.');
        case 'extend':
            $base = $lic['expires_at'] && !is_expired($lic) ? strtotime($lic['expires_at'] . ' UTC') : time();
            $new = gmdate('Y-m-d H:i:s', strtotime('+' . (int)$_POST['months'] . ' months', $base));
            $pdo->prepare('UPDATE licenses SET expires_at = ?, type = IF(type = 1, 2, type) WHERE id = ?')->execute([$new, $id]);
            log_event('admin_extended', $id, null, 'до ' . dt($new, false), 'admin');
            redirect("license.php?id=$id", 'Продлена до ' . dt($new, false) . '.');
        case 'perpetual':
            $pdo->prepare('UPDATE licenses SET expires_at = NULL, type = 3 WHERE id = ?')->execute([$id]);
            log_event('admin_updated', $id, null, 'Сделана бессрочной', 'admin');
            redirect("license.php?id=$id", 'Лицензия теперь бессрочная.');
        case 'block':
            $pdo->prepare("UPDATE licenses SET status = 'blocked', block_reason = ? WHERE id = ?")->execute([trim($_POST['reason'] ?? '') ?: null, $id]);
            log_event('admin_blocked', $id, null, $_POST['reason'] ?? null, 'admin');
            redirect("license.php?id=$id", 'Лицензия заблокирована.');
        case 'unblock':
            $pdo->prepare("UPDATE licenses SET status = 'active', block_reason = NULL WHERE id = ?")->execute([$id]);
            log_event('admin_unblocked', $id, null, null, 'admin');
            redirect("license.php?id=$id", 'Лицензия разблокирована.');
        case 'deactivate':
            $pdo->prepare('UPDATE activations SET is_active = 0, deactivated_at = ? WHERE id = ? AND license_id = ?')
                ->execute([now_utc(), (int)$_POST['activation'], $id]);
            log_event('admin_deactivated', $id, null, $_POST['machine'] ?? null, 'admin');
            redirect("license.php?id=$id", 'Устройство отвязано — на его место можно активировать другое.');
    }
}

$st = $pdo->prepare('SELECT a.*, d.machine_name, d.app_version, d.last_ip FROM activations a
    JOIN devices d ON d.id = a.device_id WHERE a.license_id = ? ORDER BY a.is_active DESC, a.id DESC');
$st->execute([$id]);
$acts = $st->fetchAll();
$activeCount = count(array_filter($acts, fn($a) => $a['is_active']));
$st = $pdo->prepare('SELECT * FROM events WHERE license_id = ? ORDER BY id DESC LIMIT 50');
$st->execute([$id]);
$events = $st->fetchAll();
$expiresLocal = $lic['expires_at'] ? (new DateTime($lic['expires_at'], new DateTimeZone('UTC')))->setTimezone(new DateTimeZone('Asia/Dushanbe'))->format('Y-m-d') : '';

page_start($lic['license_key'], 'licenses.php');
?>
<div class="head">
    <a href="licenses.php" class="btn sm">←</a>
    <h1><?= key_cell($lic['license_key']) ?> <?= license_badge($lic) ?> <?= type_badge((int)$lic['type']) ?></h1>
</div>
<?php if ($lic['status'] === 'blocked' && $lic['block_reason']): ?><div class="error">Причина блокировки: <?= h($lic['block_reason']) ?></div><?php endif ?>

<div class="cols">
<div>
    <div class="card">
        <h2>Сводка</h2>
        <dl class="info">
            <dt>Клиент</dt><dd><a href="customer.php?id=<?= $lic['customer_id'] ?>"><?= h($lic['full_name']) ?></a> <span class="muted"><?= h($lic['company']) ?></span></dd>
            <dt>Телефон</dt><dd><?= h($lic['phone']) ?: '—' ?></dd>
            <dt>Срок</dt><dd><?= $lic['expires_at'] ? dt($lic['expires_at'], false) . ' · ' : '' ?><?= days_left($lic) ?></dd>
            <dt>Устройства</dt><dd><?= $activeCount ?> из <?= $lic['max_devices'] ?></dd>
            <dt>Создана</dt><dd><?= dt($lic['created_at']) ?></dd>
            <?php if ($lic['notes']): ?><dt>Заметка</dt><dd><?= h($lic['notes']) ?></dd><?php endif ?>
        </dl>
    </div>

    <div class="card flat">
        <div style="padding:12px 14px 0"><h2>Устройства</h2></div>
        <table>
            <tr><th>Компьютер</th><th>Версия</th><th>Активирована</th><th>Связь</th><th>IP</th><th></th></tr>
            <?php foreach ($acts as $a): ?>
            <tr class="<?= $a['is_active'] ? '' : 'off' ?>"><td><?= h($a['machine_name']) ?></td><td><?= h($a['app_version']) ?></td>
                <td class="nowrap"><?= dt($a['activated_at']) ?></td><td class="nowrap" title="<?= dt($a['last_check_at']) ?>"><?= ago($a['last_check_at']) ?></td>
                <td class="muted"><?= h($a['last_ip']) ?></td>
                <td class="r"><?php if ($a['is_active']): ?>
                    <form method="post" onsubmit="return confirm('Отвязать устройство «<?= h($a['machine_name']) ?>»?')"><?= csrf() ?>
                        <input type="hidden" name="action" value="deactivate"><input type="hidden" name="activation" value="<?= $a['id'] ?>">
                        <input type="hidden" name="machine" value="<?= h($a['machine_name']) ?>"><button class="btn sm">Отвязать</button></form>
                    <?php else: ?><span class="muted sm">отвязано</span><?php endif ?></td></tr>
            <?php endforeach ?>
            <?php if (!$acts): ?><tr><td colspan="6" class="empty">Ключ ещё не активирован</td></tr><?php endif ?>
        </table>
    </div>

    <div class="card flat">
        <div style="padding:12px 14px 0"><h2>История</h2></div>
        <table>
            <?php foreach ($events as $e): ?>
            <tr><td class="nowrap muted sm"><?= dt($e['at']) ?></td><td><?= event_label($e['action']) ?></td><td class="sm"><?= h($e['details']) ?></td><td class="muted sm"><?= $e['ip'] === 'admin' ? '' : h($e['ip']) ?></td></tr>
            <?php endforeach ?>
        </table>
    </div>
</div>

<div>
    <div class="card" id="extend">
        <h2>Продление</h2>
        <form method="post" class="row"><?= csrf() ?><input type="hidden" name="action" value="extend">
            <select name="months"><option value="1">+1 месяц</option><option value="3">+3 месяца</option><option value="6">+6 месяцев</option><option value="12" selected>+1 год</option></select>
            <button class="btn primary">Продлить</button></form>
        <p class="muted sm" style="margin:0 0 8px">Считается от текущей даты окончания (или от сегодня, если истекла). Пробная станет годовой.</p>
        <?php if ($lic['expires_at']): ?>
        <form method="post" onsubmit="return confirm('Сделать лицензию бессрочной?')"><?= csrf() ?><input type="hidden" name="action" value="perpetual"><button class="btn sm">Сделать бессрочной</button></form>
        <?php endif ?>
    </div>

    <form method="post" class="card grid">
        <h2 class="wide">Параметры</h2>
        <?= csrf() ?><input type="hidden" name="action" value="save">
        <label>Тип<select name="type">
            <?php foreach (LICENSE_TYPES as $k => $v): ?><option value="<?= $k ?>" <?= $lic['type'] == $k ? 'selected' : '' ?>><?= $v ?></option><?php endforeach ?>
        </select></label>
        <label>Действует до<input type="date" name="expires_at" value="<?= $expiresLocal ?>"></label>
        <label>Устройств<input type="number" name="max_devices" min="1" value="<?= $lic['max_devices'] ?>"></label>
        <label class="wide">Заметка<input name="notes" value="<?= h($lic['notes']) ?>"></label>
        <div class="wide"><button class="btn">Сохранить</button></div>
    </form>

    <div class="card">
        <h2>Блокировка</h2>
        <?php if ($lic['status'] === 'blocked'): ?>
            <form method="post"><?= csrf() ?><input type="hidden" name="action" value="unblock"><button class="btn">Разблокировать</button></form>
        <?php else: ?>
            <form method="post" class="row" onsubmit="return confirm('Заблокировать лицензию?')"><?= csrf() ?><input type="hidden" name="action" value="block">
                <input name="reason" placeholder="Причина (увидит клиент)"><button class="btn danger">Заблокировать</button></form>
        <?php endif ?>
        <p class="muted sm" style="margin:0">Вступает в силу при следующей проверке программы (до 30 дней без интернета).</p>
    </div>
</div>
</div>
<?php page_end();
