<?php
require __DIR__ . '/../lib/admin.php';
require_login();
$pdo = db();

$groups = [
    '' => ['Все', null],
    'clients' => ['Клиенты', ['trial_registered', 'trial_resumed', 'activated']],
    'problems' => ['Проблемы', ['activation_failed', 'activation_denied']],
    'admin' => ['Действия админа', ['admin_created', 'admin_updated', 'admin_extended', 'admin_blocked', 'admin_unblocked', 'admin_deactivated', 'admin_login']],
];
$g = isset($groups[$_GET['g'] ?? '']) ? ($_GET['g'] ?? '') : '';
$q = trim($_GET['q'] ?? '');
$page = max(1, (int)($_GET['p'] ?? 1));

$where = ['1=1'];
$args = [];
if ($groups[$g][1]) {
    $where[] = 'e.action IN (' . implode(',', array_fill(0, count($groups[$g][1]), '?')) . ')';
    array_push($args, ...$groups[$g][1]);
}
if ($q !== '') {
    $where[] = '(e.details LIKE ? OR l.license_key LIKE ? OR e.ip LIKE ?)';
    array_push($args, "%$q%", "%$q%", "%$q%");
}
$st = $pdo->prepare('SELECT e.*, l.license_key, c.full_name FROM events e
    LEFT JOIN licenses l ON l.id = e.license_id LEFT JOIN customers c ON c.id = l.customer_id
    WHERE ' . implode(' AND ', $where) . ' ORDER BY e.id DESC LIMIT 100 OFFSET ' . (($page - 1) * 100));
$st->execute($args);
$rows = $st->fetchAll();

page_start('Журнал', 'events.php');
?>
<div class="head"><h1>Журнал событий</h1></div>
<form class="toolbar">
    <input type="hidden" name="g" value="<?= h($g) ?>">
    <input type="search" name="q" value="<?= h($q) ?>" placeholder="Поиск: ключ, подробности, IP…">
    <button class="btn">Найти</button>
</form>
<div class="chips" style="margin-bottom:10px">
    <?php foreach ($groups as $k => [$name]): ?><a class="chip <?= $g === $k ? 'on' : '' ?>" href="?g=<?= $k ?>&q=<?= urlencode($q) ?>"><?= $name ?></a><?php endforeach ?>
</div>
<div class="card flat">
<table>
    <tr><th>Время</th><th>Событие</th><th>Клиент / лицензия</th><th>Подробности</th><th>IP</th></tr>
    <?php foreach ($rows as $e): ?>
    <tr><td class="nowrap"><?= dt($e['at']) ?><span class="sub"><?= ago($e['at']) ?></span></td>
        <td><?= event_label($e['action']) ?></td>
        <td><?php if ($e['license_id']): ?><a href="license.php?id=<?= $e['license_id'] ?>"><?= h($e['full_name']) ?></a><span class="sub mono"><?= h($e['license_key']) ?></span><?php endif ?></td>
        <td class="sm"><?= h($e['details']) ?></td><td class="muted sm"><?= $e['ip'] === 'admin' ? '' : h($e['ip']) ?></td></tr>
    <?php endforeach ?>
    <?php if (!$rows): ?><tr><td colspan="5" class="empty">Нет событий</td></tr><?php endif ?>
</table>
<div class="pager">
    <?php if ($page > 1): ?><a class="btn sm" href="?g=<?= $g ?>&q=<?= urlencode($q) ?>&p=<?= $page - 1 ?>">← Новее</a><?php endif ?>
    <?php if (count($rows) === 100): ?><a class="btn sm" href="?g=<?= $g ?>&q=<?= urlencode($q) ?>&p=<?= $page + 1 ?>">Старее →</a><?php endif ?>
</div>
</div>
<?php page_end();
