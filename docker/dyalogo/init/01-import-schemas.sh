#!/bin/bash
set -e

DDLS_DIR="/docker-entrypoint-initdb.d/ddl"

echo "Importing dyalogo_telefonia..."
mysql -u root -p"$MYSQL_ROOT_PASSWORD" dyalogo_telefonia < "$DDLS_DIR/ddl_telefonia.sql"

echo "Importing DYALOGOCRM_SISTEMA..."
mysql -u root -p"$MYSQL_ROOT_PASSWORD" DYALOGOCRM_SISTEMA < "$DDLS_DIR/ddl_crm.sql"

echo "Importing dyalogo_general..."
mysql -u root -p"$MYSQL_ROOT_PASSWORD" dyalogo_general < "$DDLS_DIR/ddl_general.sql"

echo "Importing asterisk..."
mysql -u root -p"$MYSQL_ROOT_PASSWORD" asterisk < "$DDLS_DIR/ddl_asterisk.sql"

echo "All schemas imported successfully."
