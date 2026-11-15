<?php
require __DIR__ . '/../lib/admin.php';

$hash = setting('admin_password_hash');
$firstRun = $hash === null;
$error = null;

if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    check_csrf();
    if (!rate_limit('login', 10, 300)) {
        $error = 'Слишком много попыток. Подождите 5 минут.';
    } elseif ($firstRun) {
        $p = (string)($_POST['password'] ?? '');
        if (strlen($p) < 8) $error = 'Пароль — не короче 8 символов.';
        elseif ($p !== ($_POST['password2'] ?? '')) $error = 'Пароли не совпадают.';
        else {
            set_setting('admin_password_hash', password_hash($p, PASSWORD_DEFAULT));
            session_regenerate_id(true);
            $_SESSION['admin'] = true;
            $_SESSION['last_activity'] = time();
            redirect('index.php', 'Пароль администратора сохранён.');
        }
    } elseif (password_verify((string)($_POST['password'] ?? ''), $hash)) {
        session_regenerate_id(true);
        $_SESSION['admin'] = true;
            $_SESSION['last_activity'] = time();
        log_event('admin_login', null, null, null);
        redirect('index.php');
    } else {
        $error = 'Неверный пароль.';
    }
}

page_start('Вход');
?>
<form method="post" class="card login">
    <img class="login-logo" src="../img/logo-mark.png" alt="">
    <h1>TIJORAT <b>LITE</b></h1>
    <p class="muted center">Панель лицензий</p>
    <?= csrf() ?>
    <?php if ($firstRun): ?>
        <p class="muted">Первый запуск — задайте пароль администратора.</p>
        <label>Пароль<input type="password" name="password" required minlength="8" autocomplete="new-password" autofocus></label>
        <label>Повторите пароль<input type="password" name="password2" required minlength="8" autocomplete="new-password"></label>
    <?php else: ?>
        <label>Пароль<input type="password" name="password" required autocomplete="current-password" autofocus></label>
    <?php endif ?>
    <?php if ($error): ?><div class="error"><?= h($error) ?></div><?php endif ?>
    <button class="btn primary"><?= $firstRun ? 'Сохранить и войти' : 'Войти' ?></button>
</form>
<?php page_end();
