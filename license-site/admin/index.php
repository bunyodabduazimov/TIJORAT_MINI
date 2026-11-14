<?php
require __DIR__ . '/../lib/admin.php';
require_login();
$pdo = db();

$now = now_utc();
$count = fn(string $sql) => (int)$pdo->query($sql)->fetchColumn();
$day = gmdate('Y-m-d H:i:s', time() - 86400);
$stats = [
    ['Действующих', $count("SELECT COUNT(*) FROM licenses WHERE status='active' AND (expires_at IS NULL OR expires_at > '$now')"), 'licenses.php?f=active'],
    ['Платных', $count("SELECT COUNT(*) FROM licenses WHERE type<>1 AND status='active' AND (expires_at IS NULL OR expires_at > '$now')"), 'licenses.php?f=paid'],
    ['Пробных', $count("SELECT COUNT(*) FROM licenses WHERE type=1 AND status='active' AND expires_at > '$now'"), 'licenses.php?f=trial'],
    ['Истекают ≤14 дн', $count("SELECT COUNT(*) FROM licenses WHERE status='active' AND expires_at > '$now' AND expires_at < '" . gmdate('Y-m-d H:i:s', time() + 14 * 86400) . "'"), 'licenses.php?f=soon'],
    ['Новых за сутки', $count("SELECT COUNT(*) FROM licenses WHERE created_at > '$day'"), 'licenses.php'],
    ['Онлайн за сутки', $count("SELECT COUNT(*) FROM devices WHERE last_seen_at > '$day'"), 'devices.php'],
];
$soon = $pdo->query("SELECT l.*, c.full_name, c.company, c.phone FROM licenses l JOIN customers c ON c.id = l.customer_id
    WHERE l.status = 'active' AND l.expires_at IS NOT NULL AND l.expires_at < '" . gmdate('Y-m-d H:i:s', time() + 14 * 86400) . "'
      AND l.expires_at > '" . gmdate('Y-m-d H:i:s', time() - 7 * 86400) . "'
    ORDER BY l.expires_at LIMIT 15")->fetchAll();
$events = $pdo->query('SELECT e.*, l.license_key FROM events e LEFT JOIN licenses l ON l.id = e.license_id
    WHERE e.action <> \'admin_login\' ORDER BY e.id DESC LIMIT 15')->fetchAll();

page_start('Обзор', 'index.php');
?>
<div class="head"><h1>Обзор</h1></div>
<div class="stats">
    <?php foreach ($stats as [$name, $v, $url]): ?>
        <a class="stat" href="<?= $url ?>"><span><?= h($name) ?></span><b><?= $v ?></b></a>
    <?php endforeach ?>
</div>

<div class="cols">
    <div class="card flat">
        <div style="padding:12px 14px 0"><h2>Требуют внимания <span class="muted sm">истекают или истекли за неделю</span></h2></div>
        <table>
            <tr><th>Клиент</th><th>Тип</th><th>Осталось</th><th>Телефон</th><th></th></tr>
            <?php foreach ($soon as $l): ?>
            <tr><td><a href="license.php?id=<?= $l['id'] ?>"><?= h($l['full_name']) ?></a><span class="sub"><?= h($l['company']) ?></span></td>
                <td><?= type_badge((int)$l['type']) ?></td><td><?= days_left($l) ?></td>
                <td class="nowrap"><?= h($l['phone']) ?></td>
                <td class="r"><a class="btn sm" href="license.php?id=<?= $l['id'] ?>#extend">Продлить</a></td></tr>
            <?php endforeach ?>
            <?php if (!$soon): ?><tr><td colspan="5" class="empty">Всё в порядке</td></tr><?php endif ?>
        </table>
    </div>

    <div class="card flat">
        <div style="padding:12px 14px 0"><h2>Последние события <span class="sp"></span><a class="sm" href="events.php">Журнал →</a></h2></div>
        <table>
            <?php foreach ($events as $e): ?>
            <tr><td class="nowrap muted sm" title="<?= dt($e['at']) ?>"><?= ago($e['at']) ?></td>
                <td><?= event_label($e['action']) ?></td>
                <td class="sm"><?= $e['license_id'] ? '<a href="license.php?id=' . $e['license_id'] . '">' . h($e['details'] ?: $e['license_key']) . '</a>' : h($e['details']) ?></td></tr>
            <?php endforeach ?>
            <?php if (!$events): ?><tr><td class="empty">Пока пусто</td></tr><?php endif ?>
        </table>
    </div>
</div>
<?php page_end();
