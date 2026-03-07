-- Create the 4 Dyalogo databases and grant permissions
CREATE DATABASE IF NOT EXISTS `dyalogo_telefonia` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
CREATE DATABASE IF NOT EXISTS `DYALOGOCRM_SISTEMA` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
CREATE DATABASE IF NOT EXISTS `dyalogo_general` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
CREATE DATABASE IF NOT EXISTS `asterisk` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;

GRANT ALL PRIVILEGES ON `dyalogo_telefonia`.* TO 'dyalogo'@'%';
GRANT ALL PRIVILEGES ON `DYALOGOCRM_SISTEMA`.* TO 'dyalogo'@'%';
GRANT ALL PRIVILEGES ON `dyalogo_general`.* TO 'dyalogo'@'%';
GRANT ALL PRIVILEGES ON `asterisk`.* TO 'dyalogo'@'%';
FLUSH PRIVILEGES;
