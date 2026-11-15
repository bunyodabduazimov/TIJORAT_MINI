<?php
require __DIR__ . '/../lib/admin.php';
require_login();
$pdo = db();
$id = (int)($_GET['id'] ?? 0);
$st = $pdo->prepare('SELECT * FROM customers WHERE id = ?');
$st->execute([$id]);
$c = $st->fetch() ?: redirect('customers.php', 'Клиент не найден.');

if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    $name = trim($_POST['full_name'] ?? '');
    if ($name === '') redirect("customer.php?id=$id", 'ФИО обязательно.');
    $pdo->prepare('UPDATE customers SET full_name = ?, company = ?, phone = ?, email = ?, notes = ? WHERE id = ?')
        ->execute([$name, trim($_POST['company'] ?? ''), trim($_POST['phone'] ?? ''), trim($_POST['email'] ?? '') ?: null, trim($_POST['notes'] ?? '') ?: null, $id]);
    redirect("customer.php?id=$id", 'Сохранено.');
}

$st = $pdo->prepare('SELECT l.*, (SELECT COUNT(*) FROM activations a WHERE a.license_id = l.id AND a.is_active = 1) AS devices FROM licenses l WHERE customer_id = ? ORDER BY id DESC');
$st->execute([$id]);
$licenses = $st->fetchAll();
$phoneDigits = preg_replace('/\D/', '', $c['phone']);

page_start($c['full_name'], 'customers.php');
?>
<div class="head">
    <a href="customers.php" class="btn sm">←</a>
    <h1><?= h($c['full_name']) ?> <span class="muted sm"><?= h($c['company']) ?></span></h1>
    <span class="sp"></span>
    <?php if ($phoneDigits): ?>
        <a class="btn sm" href="tel:+<?= $phoneDigits ?>">📞 Позвонить</a>
        <a class="btn sm" href="https://wa.me/<?= $phoneDigits ?>" target="_blank">WhatsApp</a>
    <?php endif ?>
    <a class="btn sm primary" href="licenses.php?new&customer=<?= $id ?>">+ Лицензия</a>
</div>
<div class="cols">
    <div class="card flat">
        <div style="padding:12px 14px 0"><h2>Лицензии</h2></div>
        <table>
            <tr><th>Ключ</th><th>Тип</th><th>Осталось</th><th>Устр.</th><th>Статус</th></tr>
            <?php foreach ($licenses as $l): ?>
            <tr><td><?= key_cell($l['license_key'], (int)$l['id']) ?></td><td><?= type_badge((int)$l['type']) ?></td>
                <td><?= days_left($l) ?></td><td><?= $l['devices'] ?>/<?= $l['max_devices'] ?></td><td><?= license_badge($l) ?></td></tr>
            <?php endforeach ?>
            <?php if (!$licenses): ?><tr><td colspan="5" class="empty">Нет лицензий</td></tr><?php endif ?>
        </table>
    </div>
    <form method="post" class="card grid">
        <h2 class="wide">Данные клиента</h2>
        <?= csrf() ?>
        <label class="wide">ФИО<input name="full_name" value="<?= h($c['full_name']) ?>" required></label>
        <label class="wide">Компания<input name="company" value="<?= h($c['company']) ?>"></label>
        <label>Телефон<input name="phone" value="<?= h($c['phone']) ?>"></label>
        <label>E-mail<input name="email" type="email" value="<?= h($c['email']) ?>"></label>
        <label class="wide">Заметки<input name="notes" value="<?= h($c['notes']) ?>"></label>
        <div class="wide"><button class="btn">Сохранить</button> <span class="muted sm">Клиент с <?= dt($c['created_at'], false) ?></span></div>
    </form>
</div>
<?php page_end();
