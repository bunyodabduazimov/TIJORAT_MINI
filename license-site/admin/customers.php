<?php
require __DIR__ . '/../lib/admin.php';
require_login();
$q = trim($_GET['q'] ?? '');
$now = now_utc();
$st = db()->prepare("SELECT c.*,
    (SELECT COUNT(*) FROM licenses l WHERE l.customer_id = c.id) AS licenses,
    (SELECT COUNT(*) FROM licenses l WHERE l.customer_id = c.id AND l.status = 'active' AND (l.expires_at IS NULL OR l.expires_at > '$now')) AS active,
    (SELECT MAX(l.type) FROM licenses l WHERE l.customer_id = c.id) AS best_type
    FROM customers c WHERE ? = '' OR c.full_name LIKE ? OR c.company LIKE ? OR c.phone LIKE ? OR c.email LIKE ? ORDER BY c.id DESC LIMIT 500");
$st->execute([$q, "%$q%", "%$q%", "%$q%", "%$q%"]);
$rows = $st->fetchAll();

page_start('Клиенты', 'customers.php');
?>
<div class="head"><h1>Клиенты <span class="muted sm"><?= count($rows) ?></span></h1></div>
<form class="toolbar"><input type="search" name="q" value="<?= h($q) ?>" placeholder="Поиск: ФИО, компания, телефон, e-mail…"><button class="btn">Найти</button></form>
<div class="card flat">
<table>
    <tr><th>Клиент</th><th>Телефон</th><th>E-mail</th><th>Лицензии</th><th>Уровень</th><th>С нами с</th></tr>
    <?php foreach ($rows as $c): ?>
    <tr class="click" onclick="location='customer.php?id=<?= $c['id'] ?>'">
        <td><a href="customer.php?id=<?= $c['id'] ?>"><?= h($c['full_name']) ?></a><span class="sub"><?= h($c['company']) ?></span></td>
        <td class="nowrap"><?= h($c['phone']) ?></td><td class="sm"><?= h($c['email']) ?></td>
        <td><?= $c['active'] ?> <span class="muted">/ <?= $c['licenses'] ?></span></td>
        <td><?= $c['best_type'] ? type_badge((int)$c['best_type']) : '' ?></td>
        <td class="muted sm"><?= dt($c['created_at'], false) ?></td></tr>
    <?php endforeach ?>
    <?php if (!$rows): ?><tr><td colspan="6" class="empty">Ничего не найдено</td></tr><?php endif ?>
</table>
</div>
<?php page_end();
