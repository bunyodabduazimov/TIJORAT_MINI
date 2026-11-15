<?php
require __DIR__ . '/../lib/admin.php';
require_login();
$q = trim($_GET['q'] ?? '');
$now = now_utc();
$st = db()->prepare("SELECT c.*,
    (SELECT COUNT(*) FROM licenses l WHERE l.customer_id = c.id) AS licenses,
    (SELECT COUNT(*) FROM licenses l WHERE l.customer_id = c.id AND l.status = 'active' AND (l.expires_at IS NULL OR l.expires_at > '$now')) AS active,
    (SELECT MAX(l.type) FROM licenses l WHERE l.customer_id = c.id) AS best_type,
    (SELECT d.last_ip FROM devices d JOIN activations a ON a.device_id = d.id JOIN licenses l ON l.id = a.license_id
        WHERE l.customer_id = c.id ORDER BY d.last_seen_at DESC LIMIT 1) AS last_ip
    FROM customers c WHERE ? = '' OR c.full_name LIKE ? OR c.company LIKE ? OR c.phone LIKE ? OR c.email LIKE ? ORDER BY c.id DESC LIMIT 500");
$st->execute([$q, "%$q%", "%$q%", "%$q%", "%$q%"]);
$rows = $st->fetchAll();

// Страна — по IP последнего устройства клиента; фильтр по коду страны
$geo = geo_countries(array_column($rows, 'last_ip'));
$countries = [];
foreach ($rows as $c) if ($g = $geo[$c['last_ip'] ?? ''] ?? null) $countries[$g[0]] = $g;
uasort($countries, fn($a, $b) => strcmp($a[1], $b[1]));
$country = $_GET['country'] ?? '';
if ($country !== '') $rows = array_values(array_filter($rows, fn($c) => ($country === '-' ? !isset($geo[$c['last_ip'] ?? '']) : ($geo[$c['last_ip'] ?? ''][0] ?? '') === $country)));

page_start('Клиенты', 'customers.php');
?>
<div class="head"><h1>Клиенты <span class="muted sm"><?= count($rows) ?></span></h1></div>
<form class="toolbar"><input type="search" name="q" value="<?= h($q) ?>" placeholder="Поиск: ФИО, компания, телефон, e-mail…">
    <select name="country" onchange="this.form.submit()">
        <option value="">Все страны</option>
        <?php foreach ($countries as $code => $g): ?><option value="<?= h($code) ?>"<?= $country === $code ? ' selected' : '' ?>><?= h($g[1]) ?></option><?php endforeach ?>
        <option value="-"<?= $country === '-' ? ' selected' : '' ?>>Не определена</option>
    </select><button class="btn">Найти</button></form>
<div class="card flat">
<table>
    <tr><th>Клиент</th><th>Телефон</th><th>E-mail</th><th>Страна</th><th>Лицензии</th><th>Уровень</th><th>С нами с</th></tr>
    <?php foreach ($rows as $c): ?>
    <tr class="click" onclick="location='customer.php?id=<?= $c['id'] ?>'">
        <td><a href="customer.php?id=<?= $c['id'] ?>"><?= h($c['full_name']) ?></a><span class="sub"><?= h($c['company']) ?></span></td>
        <td class="nowrap"><?= h($c['phone']) ?></td><td class="sm"><?= h($c['email']) ?></td>
        <td class="nowrap"><?= country_cell($geo[$c['last_ip'] ?? ''] ?? null) ?></td>
        <td><?= $c['active'] ?> <span class="muted">/ <?= $c['licenses'] ?></span></td>
        <td><?= $c['best_type'] ? type_badge((int)$c['best_type']) : '' ?></td>
        <td class="muted sm"><?= dt($c['created_at'], false) ?></td></tr>
    <?php endforeach ?>
    <?php if (!$rows): ?><tr><td colspan="7" class="empty">Ничего не найдено</td></tr><?php endif ?>
</table>
</div>
<?php page_end();
