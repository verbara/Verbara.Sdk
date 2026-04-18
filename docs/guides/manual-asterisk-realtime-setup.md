# Asterisk Realtime Setup Manual

> **Scope note.** The Asterisk.Sdk core (AMI / AGI / ARI / Live / Sessions / Push / VoiceAi) does **not** read configuration from Asterisk Realtime databases — that is the PBX's concern, not the SDK's. The `Asterisk.Sdk.Config` package only parses flat `.conf` files.
>
> This guide describes how to run Asterisk in Realtime mode so that external tooling (dashboards, provisioning services, admin panels) can manage PJSIP endpoints, queues, and voicemail via SQL instead of config files. The companion admin dashboard referenced below — `Asterisk.Sdk.PbxAdmin` — lives in its own repository: [github.com/Harol-Reina/Asterisk.Sdk.PbxAdmin](https://github.com/Harol-Reina/Asterisk.Sdk.PbxAdmin). Consult that repo for schema DDL, `docker-compose.dashboard.yml`, and seed data referenced in the Docker section.

## Overview

Asterisk **Realtime** allows configuration to be stored in an external database (PostgreSQL, MySQL) instead of flat `.conf` files. A typical deployment splits the concern between Asterisk (reads) and an admin dashboard (writes):

- **Asterisk** reads configuration via ODBC (`res_config_odbc`)
- **Admin dashboard** (e.g. PbxAdmin) reads/writes configuration via Npgsql/Dapper

This eliminates file-based configuration and enables hot changes without module reloads for dynamic Realtime objects (PJSIP endpoints, queue members, SIP peers).

```
+-------------------+     ODBC      +-------------------+
|   Asterisk PBX    |──────────────>|   PostgreSQL DB   |
|  (res_config_odbc)|               |  (asterisk db)    |
+-------------------+               +-------------------+
                                           ^
                                           | Npgsql/Dapper
                                    +-------------------+
                                    | PbxAdmin (own     |
                                    |   repository)     |
                                    +-------------------+
```

## Types of Realtime

| Type | Behavior | Use Case |
|------|----------|----------|
| **Dynamic** | Queried per-use, no cache | PJSIP endpoints, SIP peers, queue members |
| **Static** | Loaded once, requires `reload` | Queue definitions, voicemail |

PJSIP endpoints use **Sorcery** (dynamic by default). SIP/IAX peers and queue members are also dynamic. Queue definitions (`queue_table`) are static — changes require `module reload app_queue.so`.

## Prerequisites

- Asterisk 20.x or later
- PostgreSQL 13+ (tested with 17)
- `unixodbc` and `odbc-postgresql` packages
- Asterisk modules: `res_odbc.so`, `res_config_odbc.so`

## Step-by-Step Setup

### Step 1: Install ODBC Packages

```bash
apt-get update
apt-get install -y unixodbc odbc-postgresql
```

### Step 2: Configure ODBC

**/etc/odbcinst.ini** — Register the PostgreSQL driver:
```ini
[PostgreSQL]
Description = PostgreSQL ODBC driver
Driver = /usr/lib/x86_64-linux-gnu/odbc/psqlodbca.so
```

**/etc/odbc.ini** — Define the DSN:
```ini
[asterisk-connector]
Driver = PostgreSQL
Database = asterisk
Servername = your-postgres-host
UserName = asterisk
Password = your-secure-password
Port = 5432
```

Verify the connection:
```bash
isql -v asterisk-connector asterisk your-secure-password
```

### Step 3: Create Database and Schema

The Asterisk.Sdk repo does **not** ship a Realtime DDL. Use the Asterisk official schema (from `contrib/scripts/`) or the ready-made schema from the PbxAdmin companion repo:

```bash
psql -U postgres -c "CREATE USER asterisk WITH PASSWORD 'your-secure-password';"
psql -U postgres -c "CREATE DATABASE asterisk OWNER asterisk;"

# Option A: use PbxAdmin-provided schema (see PbxAdmin repo docker/sql/)
# Option B: use Asterisk upstream schema:
#   https://github.com/asterisk/asterisk/tree/master/contrib/scripts
psql -U asterisk -d asterisk -f path/to/001-asterisk-realtime-schema.sql
psql -U asterisk -d asterisk -f path/to/002-seed-data.sql  # optional demo data
```

### Step 4: Configure Asterisk

**res_odbc.conf** — ODBC connection pool:
```ini
[asterisk]
enabled => yes
dsn => asterisk-connector
pre-connect => yes
max_connections => 5
```

**sorcery.conf** — PJSIP Realtime mapping (Asterisk 20+):
```ini
[res_pjsip]
endpoint=realtime,ps_endpoints
auth=realtime,ps_auths
aor=realtime,ps_aors
registration=realtime,ps_registrations
transport=config,transport-udp,pjsip.conf
```

> **Note:** Transports must remain file-based (`config,transport-udp,pjsip.conf`). Loading transports from Realtime causes race conditions during startup because endpoints may load before the transport is available.

**extconfig.conf** — Non-PJSIP Realtime mapping:
```ini
[settings]
sippeers => odbc,asterisk,sippeers
iaxpeers => odbc,asterisk,iaxpeers
queues => odbc,asterisk,queue_table
queue_members => odbc,asterisk,queue_members
voicemail => odbc,asterisk,voicemail
```

**modules.conf** — Ensure ODBC modules are loaded:
```ini
[modules]
autoload = yes
load = res_odbc.so
load = res_config_odbc.so
```

### Step 5: Reload Asterisk

```bash
asterisk -rx "module reload res_odbc.so"
asterisk -rx "module reload res_config_odbc.so"
asterisk -rx "core reload"
```

### Step 6: Verify

```bash
# Check ODBC connection
asterisk -rx "odbc show"

# Check PJSIP endpoints loaded from database
asterisk -rx "pjsip show endpoints"

# Check queues loaded from database
asterisk -rx "queue show"

# Check SIP peers (if using chan_sip)
asterisk -rx "sip show peers"
```

### Step 7: Configure the Admin Dashboard (optional)

If you're using PbxAdmin as the admin front-end, configure its provider in `appsettings.json`:

```json
{
  "ConfigProvider": {
    "Type": "Database",
    "ConnectionString": "Host=your-postgres-host;Database=asterisk;Username=asterisk;Password=your-secure-password"
  }
}
```

Or via environment variables: `ConfigProvider__Type=Database` and `ConfigProvider__ConnectionString=...`. To use the AMI-based provider instead (default), set `Type` to `"Ami"`.

For other admin tools (custom .NET apps, Flask, Node, etc.), any SQL client that can read/write the Realtime tables will work — Asterisk picks up changes automatically for dynamic objects, and via `module reload` for static ones.

## Docker Quick Start

The Asterisk.Sdk repo ships `docker/Dockerfile.asterisk` and `docker/docker-compose.test.yml` that bring up Asterisk in Realtime mode backed by PostgreSQL — this is what the SDK's functional and integration tests use via Testcontainers. To run them by hand:

```bash
cd /path/to/Asterisk.Sdk
docker compose -f docker/docker-compose.test.yml up --build
```

For a full admin-dashboard deployment (Asterisk + PostgreSQL + PbxAdmin UI), use the compose file in the PbxAdmin companion repo; this repo no longer ships that stack.

## Troubleshooting

### ODBC Connection Fails
```
ERROR: res_odbc: No such DSN 'asterisk-connector'
```
- Verify `/etc/odbc.ini` has the `[asterisk-connector]` section
- Verify `/etc/odbcinst.ini` points to the correct driver path
- Test with `isql -v asterisk-connector username password`

### PJSIP Endpoints Not Loading
```
WARNING: res_pjsip: No configured transports
```
- Ensure `pjsip.conf` has the `[transport-udp]` section (file-based)
- Check `sorcery.conf` has `transport=config,transport-udp,pjsip.conf`
- Verify ODBC is connected: `asterisk -rx "odbc show"`

### Database Connection Errors
- Check PostgreSQL is accepting connections from the Asterisk host
- Verify `pg_hba.conf` allows the connection
- Test: `psql -h hostname -U asterisk -d asterisk`

### Queue Members Not Appearing
- Queue definitions (`queue_table`) are **static** — run `module reload app_queue.so`
- Queue members are dynamic but need the queue to exist first
- Verify: `SELECT * FROM queue_members;`

## Column Reference

### ps_endpoints (key columns)
| Column | Type | Default | Description |
|--------|------|---------|-------------|
| `id` | VARCHAR(40) | — | Endpoint name (PK) |
| `transport` | VARCHAR(40) | — | Transport name |
| `aors` | VARCHAR(200) | — | Associated AOR(s) |
| `auth` | VARCHAR(40) | — | Auth section name |
| `context` | VARCHAR(40) | `default` | Dialplan context |
| `allow` | VARCHAR(200) | `ulaw,alaw` | Allowed codecs |

### ps_auths (key columns)
| Column | Type | Default | Description |
|--------|------|---------|-------------|
| `id` | VARCHAR(40) | — | Auth name (PK) |
| `auth_type` | VARCHAR(20) | `userpass` | `userpass` or `md5` |
| `password` | VARCHAR(80) | — | Cleartext password |
| `username` | VARCHAR(40) | — | Auth username |

### ps_aors (key columns)
| Column | Type | Default | Description |
|--------|------|---------|-------------|
| `id` | VARCHAR(40) | — | AOR name (PK) |
| `max_contacts` | INTEGER | `1` | Max simultaneous registrations |
| `contact` | VARCHAR(256) | — | Static contact URI |
| `qualify_frequency` | INTEGER | `60` | OPTIONS interval (seconds) |

### queue_members (key columns)
| Column | Type | Default | Description |
|--------|------|---------|-------------|
| `queue_name` | VARCHAR(128) | — | Queue name (PK part) |
| `interface` | VARCHAR(128) | — | Channel interface (PK part) |
| `membername` | VARCHAR(128) | — | Display name |
| `penalty` | INTEGER | `0` | Member priority |

## Security Considerations

1. **Dedicated PostgreSQL user** — Create a user with minimum required privileges:
   ```sql
   CREATE USER asterisk WITH PASSWORD 'strong-random-password';
   GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO asterisk;
   GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO asterisk;
   ```

2. **SSL connections** — Enable SSL in both `odbc.ini` (`SSLMode=require`) and the Npgsql connection string (`SSL Mode=Require`).

3. **Network isolation** — Do not expose PostgreSQL port (5432) to the public internet. Use a private network between Asterisk, Dashboard, and PostgreSQL.

4. **Password management** — Use environment variables or a secrets manager for database passwords. Never commit passwords to source control.
