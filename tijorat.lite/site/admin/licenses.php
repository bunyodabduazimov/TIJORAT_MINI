<?php
require __DIR__ . '/../lib/admin.php';
require_login();
$pdo = db();

// Новая лицензия
if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    $name = trim($_POST['full_name'] ?? '');
    $customerId = (int)($_POST['customer_id'] ?? 0);
    if (!$customerId) {
        if ($name === '') redirect('licenses.php?new', 'Выберите клиента или укажите ФИО нового.');
        $pdo->prepare('INSERT INTO customers (full_name, company, phone, email, created_at) VALUES (?, ?, ?, ?, ?)')
            ->execute([$name, trim($_POST['company'] ?? ''), trim($_POST['phone'] ?? ''), trim($_POST['email'] ?? '') ?: null, now_utc()]);
        $customerId = (int)$pdo->lastInsertId();
    }
    $type = (int)$_POST['type'];
    $months = (int)($_POST['months'] ?? 12);
    $expires = $type === LT_PERPETUAL ? null : (($_POST['expires_at'] ?? '') !== ''
        ? gmdate('Y-m-d H:i:s', strtotime($_POST['expires_at'] . ' 23:59:59 Asia/Dushanbe'))
        : ($type === LT_TRIAL ? gmdate('Y-m-d H:i:s', time() + 86400 * (int)cfg('trial_days')) : gmdate('Y-m-d H:i:s', strtotime("+$months months"))));
    $pdo->prepare('INSERT INTO licenses (license_key, customer_id, type, status, created_at, expires_at, max_devices, notes) VALUES (?, ?, ?, ?, ?, ?, ?, ?)')
        ->execute([generate_key(), $customerId, $type, 'active', now_utc(), $expires, max(1, (int)$_POST['max_devices']), trim($_POST['notes'] ?? '') ?: null]);
    $id = (int)$pdo->lastInsertId();
    log_event('admin_created', $id, null, LICENSE_TYPES[$type], 'admin');
    redirect("license.php?id=$id", 'Лицензия создана — скопируйте ключ и отправьте клиенту.');
}

$now = now_utc();
$soon = gmdate('Y-m-d H:i:s', time() + 14 * 86400);
$filters = [
    '' => ['Все', '1=1'],
    'active' => ['Действующие', "l.status='active' AND (l.expires_at IS NULL OR l.expires_at > '$now')"],
    'paid' => ['Платные', 'l.type <> 1'],
    'trial' => ['Пробные', 'l.type = 1'],
    'soon' => ['Истекают ≤14 дн', "l.status='active' AND l.expires_at > '$now' AND l.expires_at < '$soon'"],
    'expired' => ['Истёкшие', "l.expires_at <= '$now'"],
    'blocked' => ['Заблокированные', "l.status = 'blocked'"],
];
$counts = [];
foreach ($filters as $k => [, $cond])
    $counts[$k] = (int)$pdo->query("SELECT COUNT(*) FROM licenses l WHERE $cond")->fetchColumn();

$q = trim($_GET['q'] ?? '');
$f = isset($filters[$_GET['f'] ?? '']) ? ($_GET['f'] ?? '') : '';
$where = [$filters[$f][1]];
$args = [];
if ($q !== '') {
    $where[] = '(l.license_key LIKE ? OR c.full_name LIKE ? OR c.company LIKE ? OR c.phone LIKE ?)';
    array_push($args, "%$q%", "%$q%", "%$q%", "%$q%");
}
$st = $pdo->prepare('SELECT l.*, c.full_name, c.company, c.phone,
    (SELECT COUNT(*) FROM activations a WHERE a.license_id = l.id AND a.is_active = 1) AS devices,
    (SELECT MAX(a.last_check_at) FROM activations a WHERE a.license_id = l.id) AS last_check
    FROM licenses l JOIN customers c ON c.id = l.customer_id WHERE ' . implode(' AND ', $where) . ' ORDER BY l.id DESC LIMIT 500');
$st->execute($args);
$rows = $st->fetchAll();
$customers = $pdo->query('SELECT id, full_name, company FROM customers ORDER BY full_name')->fetchAll();
$preCustomer = (int)($_GET['customer'] ?? 0);

page_start('Лицензии', 'licenses.php');
?>
<div class="head"><h1>Лицензии</h1><span class="sp"></span></div>

<details class="card new-form" <?= isset($_GET['new']) ? 'open' : '' ?>>
    <summary><h2 style="margin:0">+ Новая лицензия</h2></summary>
    <form method="post" class="grid" style="margin-top:12px">
        <?= csrf() ?>
        <label>Клиент
            <select name="customer_id" onchange="document.querySelectorAll('.newc').forEach(x=>x.style.display=this.value==0?'':'none')">
                <option value="0">— новый клиент —</option>
                <?php foreach ($customers as $c): ?><option value="<?= $c['id'] ?>" <?= $preCustomer === (int)$c['id'] ? 'selected' : '' ?>><?= h($c['full_name'] . ($c['company'] ? " — {$c['company']}" : '')) ?></option><?php endforeach ?>
            </select></label>
        <label class="newc">ФИО *<input name="full_name"></label>
        <label class="newc">Компания<input name="company"></label>
        <label class="newc">Телефон<input name="phone"></label>
        <label>Тип
            <select name="type"><option value="2">Годовая</option><option value="3">Бессрочная</option><option value="1">Пробная</option></select></label>
        <label>Срок
            <select name="months"><option value="1">1 месяц</option><option value="3">3 месяца</option><option value="6">6 месяцев</option><option value="12" selected>1 год</option><option value="24">2 года</option></select></label>
        <label>Или до даты<input name="expires_at" type="date"></label>
        <label>Устройств<input name="max_devices" type="number" min="1" value="1"></label>
        <label class="wide">Заметка<input name="notes" placeholder="Например: оплата наличными, счёт №…"></label>
        <div class="wide"><button class="btn primary">Создать и получить ключ</button></div>
    </form>
    <?php if ($preCustomer): ?><script>document.querySelectorAll('.newc').forEach(x=>x.style.display='none')</script><?php endif ?>
</details>

<form class="toolbar">
    <input type="hidden" name="f" value="<?= h($f) ?>">
    <input type="search" name="q" value="<?= h($q) ?>" placeholder="Поиск: ключ, клиент, компания, телефон…">
    <button class="btn">Найти</button>
</form>
<div class="chips" style="margin-bottom:10px">
    <?php foreach ($filters as $k => [$name]): ?>
        <a class="chip <?= $f === $k ? 'on' : '' ?>" href="?f=<?= $k ?>&q=<?= urlencode($q) ?>"><?= $name ?><b><?= $counts[$k] ?></b></a>
    <?php endforeach ?>
</div>

<div class="card flat">
<table>
    <tr><th>Ключ</th><th>Клиент</th><th>Телефон</th><th>Тип</th><th>Осталось</th><th>Устр.</th><th>Связь</th><th>Статус</th></tr>
    <?php foreach ($rows as $l): ?>
    <tr class="click" onclick="if(!event.target.closest('a,button'))location='license.php?id=<?= $l['id'] ?>'">
        <td><?= key_cell($l['license_key'], (int)$l['id']) ?></td>
        <td><?= h($l['full_name']) ?><span class="sub"><?= h($l['company']) ?></span></td>
        <td class="nowrap"><?= h($l['phone']) ?></td>
        <td><?= type_badge((int)$l['type']) ?></td>
        <td class="nowrap"><?= days_left($l) ?></td>
        <td><?= $l['devices'] ?>/<?= $l['max_devices'] ?></td>
        <td class="nowrap muted sm"><?= ago($l['last_check']) ?></td>
        <td><?= license_badge($l) ?></td></tr>
    <?php endforeach ?>
    <?php if (!$rows): ?><tr><td colspan="8" class="empty">Ничего не найдено</td></tr><?php endif ?>
</table>
</div>
<?php page_end();
