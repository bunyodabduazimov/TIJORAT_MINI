<?php
require __DIR__ . '/../lib/admin.php';
require_login();
$q = trim($_GET['q'] ?? '');
$st = db()->prepare("SELECT d.*, a.license_id, l.license_key, c.full_name
    FROM devices d
    LEFT JOIN activations a ON a.id = (SELECT MAX(a2.id) FROM activations a2 WHERE a2.device_id = d.id AND a2.is_active = 1)
    LEFT JOIN licenses l ON l.id = a.license_id LEFT JOIN customers c ON c.id = l.customer_id
    WHERE ? = '' OR d.machine_name LIKE ? OR d.last_ip LIKE ? OR c.full_name LIKE ? OR l.license_key LIKE ?
    ORDER BY d.last_seen_at DESC LIMIT 500");
$st->execute([$q, "%$q%", "%$q%", "%$q%", "%$q%"]);
$rows = $st->fetchAll();
$geo = geo_countries(array_column($rows, 'last_ip'));

page_start('Устройства', 'devices.php');
?>
<div class="head"><h1>Устройства <span class="muted sm"><?= count($rows) ?></span></h1></div>
<form class="toolbar"><input type="search" name="q" value="<?= h($q) ?>" placeholder="Поиск: компьютер, клиент, ключ, IP…"><button class="btn">Найти</button></form>
<div class="card flat">
<table>
    <tr><th>Компьютер</th><th>Клиент / лицензия</th><th>Версия</th><th>Связь</th><th>Впервые</th><th>Страна</th><th>IP</th></tr>
    <?php foreach ($rows as $d): ?>
    <tr><td><?= h($d['machine_name']) ?></td>
        <td><?= $d['license_id'] ? '<a href="license.php?id=' . $d['license_id'] . '">' . h($d['full_name']) . '</a><span class="sub mono">' . h($d['license_key']) . '</span>' : '<span class="muted">не привязано</span>' ?></td>
        <td><?= h($d['app_version']) ?></td>
        <td class="nowrap" title="<?= dt($d['last_seen_at']) ?>"><?= ago($d['last_seen_at']) ?></td>
        <td class="muted sm nowrap"><?= dt($d['first_seen_at'], false) ?></td><td class="nowrap"><?= country_cell($geo[$d['last_ip']] ?? null) ?></td><td class="muted sm"><?= h($d['last_ip']) ?></td></tr>
    <?php endforeach ?>
    <?php if (!$rows): ?><tr><td colspan="7" class="empty">Ничего не найдено</td></tr><?php endif ?>
</table>
</div>
<?php page_end();
