<?php
require __DIR__ . '/../lib/admin.php';
$_SESSION = [];
session_destroy();
header('Location: login.php');
