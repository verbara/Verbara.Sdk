# Asterisk PBX Admin — Real-Time Administration Panel with Blazor Server

A full-featured **Blazor Server** PBX administration panel that demonstrates the **Asterisk.Sdk** Live API: multi-server AMI monitoring, queue management, agent tracking, channel visualization, call flow correlation, SIP trunk CRUD, campaign analytics, conference rooms, SDK metrics, and live event log — all in real-time via SignalR.

![Dashboard Home](docs/screenshots/home.png)

---

## Table of Contents

- [Quick Start with Docker](#quick-start-with-docker)
- [Features](#features)
- [Architecture](#architecture)
- [Screenshots](#screenshots)
- [Prerequisites](#prerequisites)
- [Asterisk Configuration](#asterisk-configuration)
  - [AMI User Setup](#ami-user-setup)
  - [Queue Configuration](#queue-configuration)
  - [Agent Configuration](#agent-configuration)
  - [MeetMe / ConfBridge Configuration](#meetme--confbridge-configuration)
  - [Dialplan for Testing](#dialplan-for-testing)
- [Dashboard Configuration](#dashboard-configuration)
  - [Single Server](#single-server)
  - [Multi-Server](#multi-server)
  - [File vs Realtime Config Mode](#file-vs-realtime-config-mode)
  - [Authentication](#authentication)
  - [Environment Variables](#environment-variables)
- [Running the Dashboard](#running-the-dashboard)
  - [Development](#development)
  - [Docker](#docker)
  - [Reverse Proxy (Production)](#reverse-proxy-production)
- [Pages Reference](#pages-reference)
  - [Login — Authentication](#login--authentication)
  - [Select Server — Multi-Server Selection](#select-server--multi-server-selection)
  - [Home — KPI Dashboard](#home--kpi-dashboard)
  - [Queues — Queue Overview](#queues--queue-overview)
  - [Queue Detail — Agent & Caller Management](#queue-detail--agent--caller-management)
  - [Channels — Active Calls](#channels--active-calls)
  - [Agents — Agent Monitoring](#agents--agent-monitoring)
  - [Calls — Call Flow Tracking](#calls--call-flow-tracking)
  - [Call Detail — Call Flow Analysis](#call-detail--call-flow-analysis)
  - [Trunks — SIP Trunk Management](#trunks--sip-trunk-management)
  - [Trunk Detail — Registration & Config](#trunk-detail--registration--config)
  - [Trunk Edit — Create & Modify Trunks](#trunk-edit--create--modify-trunks)
  - [Campaign Metrics — Outbound Analytics](#campaign-metrics--outbound-analytics)
  - [Conferences — MeetMe / ConfBridge Rooms](#conferences--meetme--confbridge-rooms)
  - [Metrics — SDK Instrumentation](#metrics--sdk-instrumentation)
  - [Events — Live AMI Event Log](#events--live-ami-event-log)
- [Services Reference](#services-reference)
- [SDK Features Demonstrated](#sdk-features-demonstrated)
- [Project Structure](#project-structure)
- [How It Works](#how-it-works)
  - [Connection Lifecycle](#connection-lifecycle)
  - [Real-Time Updates](#real-time-updates)
  - [Call Flow Correlation](#call-flow-correlation)
  - [Trunk Configuration Management](#trunk-configuration-management)
  - [AMI Actions from the UI](#ami-actions-from-the-ui)
  - [Metrics Collection](#metrics-collection)
- [Color Reference](#color-reference)
- [Customization](#customization)
- [Troubleshooting](#troubleshooting)

---

## Quick Start with Docker

Run the full demo (Asterisk PBX + Dashboard) with zero local dependencies:

```bash
docker compose -f docker/docker-compose.dashboard.yml up --build
```

Open [http://localhost:8080](http://localhost:8080). Default credentials: `admin` / `**AdmIn**`.

You should see:

- **Server selector** with available PBX servers and connection status
- **demo-pbx** with a green connection dot in the header
- **2 queues** — `sales` (3 members, ringall) and `support` (3 members, leastrecent)
- **6 PJSIP endpoints** — 2001-2003 (sales) and 3001-3003 (support)
- **4 agents** — 1001-1004 (available after login via `*11`)

The Docker setup includes a pre-configured Asterisk instance with AMI, queues, agents, conferences, and a demo dialplan. Supports both File and Realtime (PostgreSQL) config modes.

To stop:

```bash
docker compose -f docker/docker-compose.dashboard.yml down
```

---

## Features

| Feature | Description |
|---------|-------------|
| **Authentication** | Cookie-based login with 8-hour session expiry |
| **Multi-server monitoring** | Connect to 1–N Asterisk PBX servers simultaneously with per-server config mode |
| **Real-time KPIs** | Active calls, queues, waiting callers, agents by state — updated every 2s |
| **Queue management** | View queue stats, pause/unpause agents, add/remove members, live caller wait timers |
| **Channel visualization** | All active channels with state colors, duration timers, bridged pairs, state filtering |
| **Call origination** | Originate outbound calls from the UI via `OriginateAsync()` |
| **Call flow tracking** | Correlate AMI events into logical call flows via LinkedId, search by caller/agent/queue |
| **Call detail + ladder diagram** | UML-style sequence diagram showing message flow between participants |
| **Agent tracking** | Agent cards with state colors, reverse queue lookup (`GetQueuesForMember`) |
| **SIP trunk management** | Full CRUD for PJSIP/SIP/IAX2 trunks with status monitoring |
| **File & Realtime config** | Manage trunks via config files or PostgreSQL database (Asterisk Realtime) |
| **Campaign analytics** | Outbound campaign metrics: answer rate, abandon rate, avg duration, top queues |
| **Conference rooms** | MeetMe and ConfBridge rooms with participant state (Talking/Joined/Left) |
| **SDK metrics** | `MeterListener` capturing `AmiMetrics` + `LiveMetrics` counters, histograms, gauges |
| **Live event log** | Last 50 AMI events with type filtering, circular buffer of 200 |
| **Connection health** | Header dots showing per-server `AmiConnectionState` (green/yellow/red) |
| **Server filtering** | Dropdown to filter any page by a specific server or "All Servers" |
| **Responsive layout** | Sidebar collapses to top-bar on mobile (< 768px) |
| **Zero JavaScript** | Pure Blazor Server + CSS — no JS dependencies |

---

## Architecture

```
┌─────────────────┐     ┌─────────────────┐
│ Asterisk PBX 1  │     │ Asterisk PBX 2  │     ... N servers
│   (AMI :5038)   │     │   (AMI :5038)   │
│   File config   │     │   Realtime (PG)  │
└────────┬────────┘     └────────┬────────┘
         │ TCP                    │ TCP
         └──────────┬─────────────┘
                    │
         ┌──────────▼──────────┐
         │  AsteriskMonitor    │  IHostedService (singleton)
         │  Service            │  1 IAmiConnection per server
         │  ┌────────────────┐ │
         │  │ AsteriskServer │─┤  Live domain objects:
         │  │  .Channels     │ │  - ChannelManager
         │  │  .Queues       │ │  - QueueManager
         │  │  .Agents       │ │  - AgentManager
         │  │  .MeetMe       │ │  - MeetMeManager
         │  └────────────────┘ │
         │  ┌────────────────┐ │
         │  │ CallFlowTracker│ │  LinkedId correlation (500 calls)
         │  └────────────────┘ │
         │  ┌────────────────┐ │
         │  │ EventLogService│ │  Circular buffer (200 entries)
         │  └────────────────┘ │
         │  ┌────────────────┐ │
         │  │ TrunkService   │ │  PJSIP/SIP/IAX2 CRUD
         │  │  ├ FileProvider │ │  ↔ .conf files
         │  │  └ DbProvider  │ │  ↔ PostgreSQL (Realtime)
         │  └────────────────┘ │
         └──────────┬──────────┘
                    │ In-memory (singleton)
         ┌──────────▼──────────┐
         │  Blazor Server      │  SignalR (built-in)
         │  15 pages + layout  │  Timer-based refresh (1–2s)
         │  Cookie auth (8h)   │
         └──────────┬──────────┘
                    │ WebSocket
         ┌──────────▼────────────────┐
         │  Browser 1  │  Browser 2  │  ... N browsers
         └─────────────┴─────────────┘
```

**Key design points:**

- Only 1 AMI connection per PBX server, shared across all browser sessions
- `AsteriskServer` maintains in-memory domain objects that Blazor pages read directly
- `CallFlowTracker` correlates AMI events into logical call flows using Asterisk's `LinkedId`
- `TrunkService` supports both File and Realtime config modes per server
- Cookie-based authentication gates all pages except `/login`

---

## Screenshots

> **Note:** Replace these placeholder images with actual screenshots after running the dashboard against your Asterisk servers.

| Page | Screenshot |
|------|-----------|
| Login | ![Login](docs/screenshots/login.png) |
| Select Server | ![Select Server](docs/screenshots/select-server.png) |
| Home (KPIs) | ![Home](docs/screenshots/home.png) |
| Queues | ![Queues](docs/screenshots/queues.png) |
| Queue Detail | ![Queue Detail](docs/screenshots/queue-detail.png) |
| Channels | ![Channels](docs/screenshots/channels.png) |
| Agents | ![Agents](docs/screenshots/agents.png) |
| Calls | ![Calls](docs/screenshots/calls.png) |
| Call Detail | ![Call Detail](docs/screenshots/call-detail.png) |
| Trunks | ![Trunks](docs/screenshots/trunks.png) |
| Trunk Detail | ![Trunk Detail](docs/screenshots/trunk-detail.png) |
| Trunk Edit | ![Trunk Edit](docs/screenshots/trunk-edit.png) |
| Campaign Metrics | ![Campaign Metrics](docs/screenshots/campaign-metrics.png) |
| Conferences | ![Conferences](docs/screenshots/conferences.png) |
| Metrics | ![Metrics](docs/screenshots/metrics.png) |
| Events | ![Events](docs/screenshots/events.png) |

---

## Prerequisites

- [.NET 10 SDK](https://dot.net/download) (10.0.100+)
- Asterisk PBX 13+ with AMI enabled (16+ recommended, 23+ for chan_websocket)
- Network access from the dashboard host to Asterisk AMI port (default: 5038)
- **Optional:** PostgreSQL for Realtime config mode (trunk management via database)

---

## Asterisk Configuration

### AMI User Setup

Edit `/etc/asterisk/manager.conf` to create an AMI user for the dashboard:

```ini
[general]
enabled = yes
port = 5038
bindaddr = 0.0.0.0       ; or restrict to dashboard IP

[dashboard]
secret = YourSecurePassword
deny = 0.0.0.0/0.0.0.0
permit = 10.0.0.0/255.255.255.0    ; dashboard network
read = system,call,agent,user,config,dtmf,reporting,cdr,dialplan,originate
write = system,call,agent,user,config,originate,command
writetimeout = 5000
```

**Permissions explained:**

| Permission | Used For |
|-----------|----------|
| `system` | Connection management, version detection |
| `call` | Channel events (`NewChannel`, `Hangup`, `Newstate`, `Bridge`) |
| `agent` | Agent events (`AgentLogin`, `AgentLogoff`, `AgentConnect`, `AgentComplete`) |
| `user` | Queue events (`QueueMember*`, `QueueCaller*`, `QueueParams`) |
| `config` | Trunk configuration read/write (`GetConfig`, `UpdateConfig`) |
| `originate` | Originate calls from the Channels page |
| `reporting` | `StatusAction`, `QueueStatusAction` for initial state |
| `command` | `AgentsAction` for initial agent state |

After editing, reload the AMI module:

```bash
asterisk -rx "manager reload"
```

### Queue Configuration

Edit `/etc/asterisk/queues.conf`:

```ini
[general]
persistentmembers = yes
autofill = yes
monitor-type = MixMonitor

[sales]
musicclass = default
strategy = ringall
timeout = 15
retry = 5
wrapuptime = 10
maxlen = 0
announce-frequency = 30
announce-holdtime = yes
member => PJSIP/2001,0,Agent 2001
member => PJSIP/2002,0,Agent 2002
member => PJSIP/2003,0,Agent 2003

[support]
musicclass = default
strategy = leastrecent
timeout = 20
retry = 5
wrapuptime = 15
maxlen = 20
member => PJSIP/3001,0,Support 3001
member => PJSIP/3002,0,Support 3002
member => PJSIP/3003,1,Support 3003
member => PJSIP/3004,1,Support 3004

[billing]
musicclass = default
strategy = roundrobin
timeout = 30
retry = 5
member => PJSIP/4001,0,Billing 4001
member => PJSIP/4002,0,Billing 4002
```

Reload:

```bash
asterisk -rx "queue reload all"
```

### Agent Configuration

Edit `/etc/asterisk/agents.conf`:

```ini
[general]
multiplelogin = no

[agents]
maxlogintries = 3
autologoff = 0
ackcall = yes
wrapuptime = 5000

agent => 1001,1234,John Smith
agent => 1002,1234,Jane Doe
agent => 1003,1234,Bob Wilson
agent => 1004,1234,Alice Brown
```

### MeetMe / ConfBridge Configuration

**MeetMe** — edit `/etc/asterisk/meetme.conf`:

```ini
[general]

conf => 800
conf => 801
conf => 802
```

**ConfBridge** (Asterisk 11+) — edit `/etc/asterisk/confbridge.conf`:

```ini
[general]

[default_bridge]
type = bridge
max_members = 50

[default_user]
type = user
announce_user_count = yes
announce_join_leave = yes
music_on_hold_when_empty = yes
```

### Dialplan for Testing

Add to `/etc/asterisk/extensions.conf` to enable testing:

```ini
[default]
; Inbound to queues
exten => 100,1,Answer()
 same => n,Queue(sales,,,,300)
 same => n,Hangup()

exten => 101,1,Answer()
 same => n,Queue(support,,,,300)
 same => n,Hangup()

exten => 102,1,Answer()
 same => n,Queue(billing,,,,300)
 same => n,Hangup()

; Conference rooms
exten => 800,1,Answer()
 same => n,MeetMe(800,dM)
 same => n,Hangup()

exten => 801,1,Answer()
 same => n,ConfBridge(801,default_bridge,default_user)
 same => n,Hangup()

; Direct extension dialing (PJSIP)
exten => _2XXX,1,Dial(PJSIP/${EXTEN},30)
 same => n,Hangup()

exten => _3XXX,1,Dial(PJSIP/${EXTEN},30)
 same => n,Hangup()

exten => _4XXX,1,Dial(PJSIP/${EXTEN},30)
 same => n,Hangup()

; Agent login/logout
exten => *11,1,AgentLogin()
exten => *12,1,AgentLogoff()
```

Reload:

```bash
asterisk -rx "dialplan reload"
```

---

## Dashboard Configuration

### Single Server

Edit `appsettings.json`:

```json
{
  "Auth": {
    "Username": "admin",
    "Password": "YourAdminPassword"
  },
  "Asterisk": {
    "Servers": [
      {
        "Id": "pbx-main",
        "Hostname": "192.168.1.10",
        "Port": 5038,
        "Username": "dashboard",
        "Password": "YourSecurePassword",
        "ConfigMode": "File"
      }
    ]
  }
}
```

### Multi-Server

```json
{
  "Asterisk": {
    "Servers": [
      {
        "Id": "pbx-east",
        "Hostname": "pbx-east.example.com",
        "Port": 5038,
        "Username": "dashboard",
        "Password": "SecretEast",
        "ConfigMode": "File"
      },
      {
        "Id": "pbx-west",
        "Hostname": "pbx-west.example.com",
        "Port": 5038,
        "Username": "dashboard",
        "Password": "SecretWest",
        "ConfigMode": "Realtime",
        "RealtimeConnectionString": "Host=db.example.com;Database=asterisk;Username=asterisk;Password=secret"
      }
    ]
  }
}
```

### File vs Realtime Config Mode

Each server can operate in one of two config modes, which affects trunk management:

| Mode | Storage | Use Case |
|------|---------|----------|
| **File** | `/etc/asterisk/*.conf` files | Traditional Asterisk config, direct file access required |
| **Realtime** | PostgreSQL database (ODBC tables) | Dynamic config, multiple admin tools, no file access needed |

In **Realtime** mode, trunk CRUD operations go directly to the database (tables: `ps_endpoints`, `ps_aors`, `ps_transports` for PJSIP; `sippeers` for SIP; `iaxpeers` for IAX2).

### Authentication

The dashboard uses ASP.NET Core cookie authentication with an 8-hour session expiry. Configure credentials in `appsettings.json`:

```json
{
  "Auth": {
    "Username": "admin",
    "Password": "YourAdminPassword"
  }
}
```

All pages except `/login` require authentication. Unauthorized requests are redirected to the login page.

### Environment Variables

Override settings via environment variables (ASP.NET Core convention):

```bash
export Auth__Username=admin
export Auth__Password=MySecurePassword
export Asterisk__Servers__0__Id=pbx-main
export Asterisk__Servers__0__Hostname=192.168.1.10
export Asterisk__Servers__0__Port=5038
export Asterisk__Servers__0__Username=dashboard
export Asterisk__Servers__0__Password=YourSecurePassword
export Asterisk__Servers__0__ConfigMode=Realtime
export Asterisk__Servers__0__RealtimeConnectionString="Host=db;Database=asterisk;..."
```

Or via command-line:

```bash
dotnet run --project Examples/PbxAdmin/ \
  --Asterisk:Servers:0:Hostname=192.168.1.10 \
  --Asterisk:Servers:0:Username=dashboard \
  --Asterisk:Servers:0:Password=YourSecurePassword
```

---

## Running the Dashboard

### Development

```bash
# From the repository root
dotnet run --project Examples/PbxAdmin/

# Opens at http://localhost:5000
```

To specify a custom port:

```bash
dotnet run --project Examples/PbxAdmin/ --urls "http://0.0.0.0:8080"
```

### Docker

Create a `Dockerfile` in the `Examples/PbxAdmin/` directory:

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish Examples/PbxAdmin/PbxAdmin.csproj \
    -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "PbxAdmin.dll"]
```

Build and run:

```bash
# From repository root
docker build -t asterisk-pbx-admin -f Examples/PbxAdmin/Dockerfile .
docker run -d \
  --name dashboard \
  -p 8080:8080 \
  -e Auth__Username=admin \
  -e Auth__Password=MySecurePassword \
  -e Asterisk__Servers__0__Hostname=192.168.1.10 \
  -e Asterisk__Servers__0__Username=dashboard \
  -e Asterisk__Servers__0__Password=YourSecurePassword \
  asterisk-pbx-admin
```

### Reverse Proxy (Production)

**Nginx** — Blazor Server requires WebSocket support:

```nginx
server {
    listen 443 ssl;
    server_name dashboard.example.com;

    ssl_certificate     /etc/ssl/certs/dashboard.pem;
    ssl_certificate_key /etc/ssl/private/dashboard.key;

    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_cache_bypass $http_upgrade;

        # SignalR long-polling fallback
        proxy_buffering off;
        proxy_read_timeout 86400s;
    }
}
```

**Important:** The `Upgrade` and `Connection` headers are required for SignalR WebSocket transport. Without them, Blazor Server will fall back to long-polling (slower).

---

## Pages Reference

### Login — Authentication

**Route:** `/login`

Simple credential-based login form. Validates against `Auth:Username` and `Auth:Password` from `appsettings.json`. Creates a signed-in `ClaimsPrincipal` via ASP.NET Core cookie authentication with an 8-hour expiry. On success, redirects to the server selection page.

---

### Select Server — Multi-Server Selection

**Route:** `/select-server`

Card grid showing all available PBX servers from configuration. Each card displays:

- Connection status dot (green = Connected, yellow = Reconnecting, red = Disconnected)
- Server identifier
- Config mode badge (**File** or **Realtime**)
- Connection state text

If only one server is configured, auto-selects and redirects to the home page. The selected server is stored in a circuit-scoped service and persists for the browser session.

---

### Home — KPI Dashboard

**Route:** `/`

![Home](docs/screenshots/home.png)

Global KPIs across all connected servers:

| KPI | Source | Color |
|-----|--------|-------|
| Active Calls | `server.Channels.ChannelCount` | Blue |
| Queues | `server.Queues.QueueCount` | Purple |
| Waiting Callers | `sum(queue.EntryCount)` | Red if > 0 |
| Available Agents | `GetAgentsByState(Available)` | Green |
| On Call Agents | `GetAgentsByState(OnCall)` | Red |
| Paused Agents | `GetAgentsByState(Paused)` | Yellow |

**Call Flow Metrics** — sourced from `CallFlowTracker`:

| Metric | Description |
|--------|-------------|
| Calls Tracked | Total calls correlated |
| Avg Wait Time | Average time in queue before agent connect |
| Avg Talk Time | Average connected call duration |
| Abandon Rate | Percentage of calls that hung up in queue |

Below the KPIs, a **Queue Summary Table** shows per-queue statistics: members, waiting callers, completed/abandoned calls, average hold time, and average talk time.

**Refresh interval:** 2 seconds.

---

### Queues — Queue Overview

**Route:** `/queues`

![Queues](docs/screenshots/queues.png)

Responsive card grid showing all queues across all servers. Each card includes:

- Queue name and strategy
- Member count
- Waiting callers (pulsing red badge if > 0)
- Completed and abandoned counts

**Health indicator** (left border color):

| Condition | Color |
|-----------|-------|
| 0 callers waiting | Green |
| 1–3 callers | Yellow |
| 4+ callers | Red |
| 0 members (unattended) | Gray |

Click a queue card to navigate to the [Queue Detail](#queue-detail--agent--caller-management) page.

**Server filter:** Dropdown to show queues from a specific server.

---

### Queue Detail — Agent & Caller Management

**Route:** `/queue/{ServerId}/{QueueName}`

![Queue Detail](docs/screenshots/queue-detail.png)

Detailed view of a single queue with three sections:

**Stats Bar** — Max capacity, strategy, current calls, avg hold/talk time, completed/abandoned.

**Agents Grid** — Each agent card shows:

- Name/interface with status dot and icon
- Calls taken and penalty
- Paused badge with reason (if paused)
- **Action buttons:**
  - **Pause/Unpause** — sends `QueuePauseAction` via `IAmiConnection.SendActionAsync()`
  - **Remove** — sends `QueueRemoveAction`

Agent status colors by `QueueMemberState`:

| State | Color | Icon |
|-------|-------|------|
| Paused (override) | Yellow `#eab308` | Pause |
| DeviceNotInUse | Green `#22c55e` | Checkmark |
| DeviceInUse | Red `#ef4444` | Phone |
| DeviceBusy | Dark Red `#dc2626` | Stop |
| DeviceRinging | Blue `#3b82f6` | Bell |
| DeviceRingInUse | Blue `#3b82f6` | Bell |
| DeviceOnHold | Orange `#f97316` | Hourglass |
| DeviceUnavailable | Gray `#9ca3af` | Cross |
| DeviceUnknown | Light Gray `#d1d5db` | ? |

**Callers Waiting** — Table of `AsteriskQueueEntry` with position, caller ID, channel, and **live wait timer** (calculated from `JoinedAt`).

**Add Member** — Inline form to add a member via `QueueAddAction`: interface, name, penalty.

**Refresh interval:** 1 second.

---

### Channels — Active Calls

**Route:** `/channels`

![Channels](docs/screenshots/channels.png)

Table of all active channels with:

- UniqueId, channel name, state with color dot
- Caller ID, context, extension
- **Live duration** (calculated from `CreatedAt`)
- **Linked channel** — the bridged pair (`LinkedChannel.Name`)

**Filter chips:** All | Dialing | Ringing | Up | Busy — uses `GetChannelsByState()` (lazy, zero-alloc).

Channel state colors:

| State | Color |
|-------|-------|
| Up | Green |
| Ringing | Blue |
| Ring | Light Blue |
| Dialing | Yellow |
| Busy | Red |
| Down | Gray |

**Originate Call** — Form at the bottom to originate outbound calls:

- Server selector, channel (`PJSIP/2000`), context, extension, caller ID, timeout
- Calls `AsteriskServer.OriginateAsync()` and shows success/failure result

**Refresh interval:** 1 second.

---

### Agents — Agent Monitoring

**Route:** `/agents`

![Agents](docs/screenshots/agents.png)

Counter cards at top showing agents by state. Below, a filterable card grid:

**Filter chips:** All | Available | On Call | Paused | Logged Off — uses `GetAgentsByState()`.

Each agent card shows:

- Agent ID and name
- Current state with colored background
- Current channel and "talking to"
- Login duration (e.g., "2h 15m ago")
- **Queue membership** — calls `GetQueuesForMember(agent.Channel)` (O(1) reverse index lookup)
- Calls taken and average talk time

Agent state colors:

| State | Dot | Background |
|-------|-----|------------|
| Available | `#22c55e` | `#f0fdf4` |
| On Call | `#ef4444` | `#fef2f2` |
| Paused | `#eab308` | `#fefce8` |
| Logged Off | `#9ca3af` | `#f9fafb` |

**Refresh interval:** 2 seconds.

---

### Calls — Call Flow Tracking

**Route:** `/calls`

![Calls](docs/screenshots/calls.png)

Tracks logical call flows by correlating AMI events via Asterisk's `LinkedId`. This provides a unified view of the complete call lifecycle across channels, queues, and bridges.

**KPI cards:** Connected, Ringing, Queued, On Hold, Completed.

**Filter chips:** All | Connected | Ringing | Queued | Hold | Completed.

**Search box:** filters by caller ID, agent name, queue name, or call ID.

Each call card shows:

- State badge with color, live duration
- **Caller** — endpoint info (caller ID, channel name, technology)
- **Direction arrow** — connected, hold, or dialing indicator
- **Destination** — agent name/queue or destination endpoint

Click a card to navigate to [Call Detail](#call-detail--call-flow-analysis).

**Refresh interval:** 2 seconds.

**Data source:** `CallFlowTracker` — maintains up to 500 correlated calls (active + completed with 5-minute retention).

---

### Call Detail — Call Flow Analysis

**Route:** `/calls/{CallId}`

![Call Detail](docs/screenshots/call-detail.png)

Detailed view of a single call flow with:

**Summary Panel:**

- Call ID (LinkedId), Server, Technology
- Caller and Destination identifiers
- Queue/Agent information (if applicable)
- Duration and current state

**Ladder Diagram** — UML-style sequence diagram (`LadderDiagram.razor` component):

- Participant headers with channel names and technology type
- Vertical lifelines per participant
- Bridge blocks (colored rectangles) showing connected periods
- Event arrows between participants:
  - **Green arrows** — connect events (Bridge, AgentConnect)
  - **Orange arrows** — hold events (Hold, Unhold)
  - **Blue arrows** — actions (DTMF, transfer)
- Time offset labels on each arrow

**Event Timeline** — chronological list of all captured AMI events in the call.

**Refresh interval:** 2 seconds.

---

### Trunks — SIP Trunk Management

**Route:** `/trunks`

![Trunks](docs/screenshots/trunks.png)

Full CRUD management for SIP trunks. Supports three technologies:

| Technology | Config File | Realtime Table |
|-----------|-------------|----------------|
| **PJSIP** | `pjsip.conf` | `ps_endpoints`, `ps_aors`, `ps_transports` |
| **SIP** | `sip.conf` | `sippeers` |
| **IAX2** | `iax.conf` | `iaxpeers` |

**Config mode badge** — shows whether the server uses File or Realtime config.

**KPI cards:** Registered, Unreachable, Total Active Calls, Total Trunk Count.

**Filter chips:** All | Registered | Unreachable | Unknown — by `TrunkStatus`.

Each trunk card shows:

- Name and technology badge (PJSIP/SIP/IAX2)
- Status dot (green = Registered, red = Unreachable, gray = Unknown, yellow = Rejected)
- Host:Port and configured codecs
- Active Calls / Max Channels

**Action buttons:** Detail, Edit, Delete.

**New Trunk button** — navigates to the [Trunk Edit](#trunk-edit--create--modify-trunks) form.

**Refresh interval:** 2 seconds.

---

### Trunk Detail — Registration & Config

**Route:** `/trunks/{ServerId}/{TrunkName}`

![Trunk Detail](docs/screenshots/trunk-detail.png)

Deep view of a single trunk:

**Stats Bar:** Status, Host:Port, Transport protocol (UDP/TCP/TLS/WS/WSS), Codecs, Qualify frequency, Active Calls.

**Registration Details** (if available):

- Contact URI (SIP registration address)
- User Agent string
- Roundtrip time (qualify latency in ms)

**Raw Config Display:** Expandable sections showing the full configuration. For PJSIP trunks, this includes `[endpoint]`, `[aor]`, and `[transport]` sections. Data is loaded from either config files or database depending on the server's config mode.

**Action buttons:** Edit, Delete.

---

### Trunk Edit — Create & Modify Trunks

**Route:** `/trunks/new` (create) and `/trunks/edit/{ServerId}/{TrunkName}` (edit)

![Trunk Edit](docs/screenshots/trunk-edit.png)

Form for creating or editing SIP trunks:

**Technology Selector** — Radio buttons: PJSIP, SIP, IAX2.

**Connection Settings:**

- Trunk name (identifier)
- Host address and port
- Transport protocol (UDP, TCP, TLS, WS, WSS)
- Dialplan context
- Max concurrent channels

**Authentication:** Username and password for trunk registration.

**Media:** Codec selection, caller ID presentation.

On save, the trunk is written to the appropriate config file or database table, and an Asterisk reload is triggered.

---

### Campaign Metrics — Outbound Analytics

**Route:** `/campaign-metrics`

![Campaign Metrics](docs/screenshots/campaign-metrics.png)

Analytics dashboard for outbound call campaigns, sourced from `CallFlowTracker` data filtered to originated calls.

**Primary KPIs:**

| Metric | Description |
|--------|-------------|
| Originated | Total outbound calls created |
| Answered | Calls that were answered (with % rate) |
| Unanswered | Calls that were not answered (with % rate) |
| Bridged to Agent | Calls connected to an agent (with % rate) |

**Secondary KPIs:** Active Now, Completed, Avg Duration, Avg Wait Time.

**Advanced Breakdown** (expandable toggle):

- **State Breakdown Table** — Connected, Ringing, Queued, Hold, Abandoned counts with percentages
- **Top Queues Table** — queue name, call count, avg duration, avg hold time

**Server filter:** Scoped to selected server.

---

### Conferences — MeetMe / ConfBridge Rooms

**Route:** `/conferences`

![Conferences](docs/screenshots/conferences.png)

Lists active conference rooms from `MeetMeManager.Rooms`. Each room shows:

- Room number and participant count
- Participant table with channel, state, and muted badge

Participant state colors:

| State | Color |
|-------|-------|
| Talking | Green |
| Joined | Blue |
| Left | Gray |

Supports both **MeetMe** and **ConfBridge** events (the SDK maps both to the same `MeetMeManager`).

**Refresh interval:** 2 seconds.

---

### Metrics — SDK Instrumentation

**Route:** `/metrics`

![Metrics](docs/screenshots/metrics.png)

Uses `System.Diagnostics.Metrics.MeterListener` to capture instruments from the SDK's `AmiMetrics` and `LiveMetrics` meters.

**AMI Connection Health (counters):**

| Metric | Instrument Name | Alert |
|--------|----------------|-------|
| Events Received | `ami.events.received` | — |
| Events Dropped | `ami.events.dropped` | Red if > 0 |
| Events Dispatched | `ami.events.dispatched` | — |
| Actions Sent | `ami.actions.sent` | — |
| Responses Received | `ami.responses.received` | — |
| Reconnections | `ami.reconnections` | Yellow if > 0 |

**AMI Histograms:**

| Metric | Instrument Name |
|--------|----------------|
| Avg Roundtrip | `ami.action.roundtrip` (ms) |
| Avg Event Dispatch | `ami.event.dispatch` (ms) |

**Live State Gauges (observable):**

| Metric | Instrument Name |
|--------|----------------|
| Active Channels | `live.channels.active` |
| Queue Count | `live.queues.count` |
| Total Agents | `live.agents.total` |
| Available Agents | `live.agents.available` |
| On-Call Agents | `live.agents.on_call` |
| Paused Agents | `live.agents.paused` |

**Refresh interval:** 2 seconds (calls `RecordObservableInstruments()` each cycle).

---

### Events — Live AMI Event Log

**Route:** `/events`

![Events](docs/screenshots/events.png)

Scrollable table of the last 50 AMI events from `EventLogService`:

| Column | Source |
|--------|--------|
| Time | `DateTimeOffset.UtcNow` at receive |
| Server | Server ID from config |
| Event | `ManagerEvent` type name (e.g., `NewChannel`, `Hangup`, `QueueMemberPaused`) |
| UniqueId | `ManagerEvent.UniqueId` |
| Channel | `RawFields["Channel"]` |

**Filter chips:** All | Channel | Queue | Agent — filters by event type name.

The `EventLogService` maintains a **thread-safe circular buffer** (`ConcurrentQueue`) of the last 200 events across all servers.

**Refresh interval:** 1 second.

---

## Services Reference

| Service | Scope | Purpose |
|---------|-------|---------|
| **AsteriskMonitorService** | Singleton, `IHostedService` | Central hub managing all server connections, event subscriptions, and lifecycle |
| **EventLogService** | Singleton | Thread-safe circular buffer (200 entries) of recent AMI events |
| **CallFlowTracker** | Singleton | Correlates AMI events into logical call flows via `LinkedId`; 500 call capacity, 5-min retention |
| **SelectedServerService** | Circuit-scoped | Holds the selected server ID per browser session |
| **TrunkService** | Singleton | CRUD operations for PJSIP/SIP/IAX2 trunks with live status |
| **PbxConfigManager** | Singleton | High-level config file management (load, save, reload) |
| **ConfigProviderResolver** | Singleton | Factory that returns the correct `IConfigProvider` (File or Database) per server |
| **DbConfigProvider** | Singleton | Realtime config backend via PostgreSQL + Dapper for ODBC-style tables |

---

## SDK Features Demonstrated

| SDK Feature | Where Used |
|-------------|-----------|
| `AddAsteriskMultiServer()` | `Program.cs` — registers `IAmiConnectionFactory` |
| `IAmiConnectionFactory` | `AsteriskMonitorService` — creates connections per server |
| `AsteriskServer` | All pages — live domain model root |
| `ChannelManager` | Home, Channels — `.ActiveChannels`, `.GetChannelsByState()`, `.ChannelCount` |
| `QueueManager` | Home, Queues, QueueDetail, Agents — `.Queues`, `.GetByName()`, `.GetQueuesForMember()` |
| `AgentManager` | Home, Agents — `.Agents`, `.GetAgentsByState()` |
| `MeetMeManager` | Conferences — `.Rooms`, room users |
| `AsteriskChannel` | Channels — `.UniqueId`, `.Name`, `.State`, `.CallerIdNum`, `.CreatedAt`, `.LinkedChannel` |
| `AsteriskQueue` | Queues — `.Members`, `.Entries`, `.Strategy`, `.HoldTime`, `.TalkTime` |
| `AsteriskQueueMember` | QueueDetail — `.Status`, `.Paused`, `.PausedReason`, `.CallsTaken`, `.Penalty` |
| `AsteriskQueueEntry` | QueueDetail — `.Position`, `.CallerId`, `.JoinedAt` (live wait timer) |
| `AsteriskAgent` | Agents — `.State`, `.Channel`, `.TalkingTo`, `.LoggedInAt` |
| `MeetMeRoom` / `MeetMeUser` | Conferences — `.RoomNumber`, `.UserCount`, `.State`, `.Muted`, `.Talking` |
| `QueueMemberState` enum | QueueDetail — 9-value color mapping |
| `ChannelState` enum | Channels — filter chips + color dots |
| `AgentState` enum | Agents — filter chips + card colors |
| `MeetMeUserState` enum | Conferences — participant state colors |
| `AmiConnectionState` enum | Layout header — connection health dots |
| `IAmiConnection.SendActionAsync()` | QueueDetail — `QueuePauseAction`, `QueueAddAction`, `QueueRemoveAction` |
| `IAmiConnection.Subscribe()` | `AsteriskMonitorService` — event log + call flow observers |
| `AsteriskServer.OriginateAsync()` | Channels — originate modal |
| `AsteriskServer.ConnectionLost` | `AsteriskMonitorService` — reconnection logging |
| `AmiMetrics.Meter` | Metrics — `MeterListener` for AMI counters/histograms |
| `LiveMetrics.Meter` | Metrics — observable gauges for live state |
| `GetQueuesForMember()` | Agents — reverse index O(1) lookup |
| `GetChannelsByState()` | Channels — lazy filter, zero-alloc |
| `GetAgentsByState()` | Agents, Home — lazy filter |

---

## Project Structure

```
Examples/PbxAdmin/
├── PbxAdmin.csproj          # Blazor Server, IsAotCompatible=false
├── Program.cs                       # DI setup + Serilog + Cookie auth
├── appsettings.json                 # Server connections + auth credentials
│
├── Components/
│   ├── App.razor                    # HTML root with InteractiveServer render mode
│   ├── Routes.razor                 # Router with MainLayout default
│   ├── _Imports.razor               # Global using directives
│   ├── Layout/
│   │   ├── MainLayout.razor         # Sidebar nav + header with connection dots
│   │   └── LoginLayout.razor        # Simple centered card layout for login
│   ├── Pages/
│   │   ├── Login.razor              # Credential-based login form
│   │   ├── SelectServer.razor       # Multi-server selection cards
│   │   ├── Home.razor               # KPI cards + call flow metrics + queue summary
│   │   ├── Queues.razor             # Queue card grid with health indicators
│   │   ├── QueueDetail.razor        # Agent cards, callers, pause/add/remove
│   │   ├── Channels.razor           # Channel table, state filters, originate form
│   │   ├── Agents.razor             # Agent cards, state filters, queue membership
│   │   ├── Calls.razor              # Call flow view, search, state filters
│   │   ├── CallDetail.razor         # Call detail with ladder diagram
│   │   ├── Trunks.razor             # Trunk list, status filters, CRUD
│   │   ├── TrunkDetail.razor        # Trunk registration & config sections
│   │   ├── TrunkEdit.razor          # Trunk create/edit form
│   │   ├── CampaignMetrics.razor    # Outbound campaign analytics
│   │   ├── Conferences.razor        # MeetMe/ConfBridge rooms
│   │   ├── Metrics.razor            # MeterListener for SDK metrics
│   │   └── Events.razor             # Live AMI event log with filtering
│   └── Shared/
│       ├── LadderDiagram.razor      # UML sequence diagram for call flows
│       └── RedirectToLogin.razor    # Redirect unauthorized users
│
├── Services/
│   ├── AsteriskMonitorService.cs    # IHostedService: manages all connections
│   ├── EventLogService.cs           # Thread-safe circular buffer (200 events)
│   ├── CallFlowTracker.cs           # LinkedId-based call correlation
│   ├── SelectedServerService.cs     # Circuit-scoped server selection
│   ├── TrunkService.cs              # PJSIP/SIP/IAX2 trunk CRUD
│   ├── PbxConfigManager.cs          # Config file management
│   ├── IConfigProvider.cs           # Config provider abstraction
│   ├── DbConfigProvider.cs          # PostgreSQL realtime config backend
│   ├── ConfigProviderResolver.cs    # File vs. Realtime provider factory
│   ├── ConfigProviderOptions.cs     # Config options model
│   └── RealtimeTableMap.cs          # Database table mappings
│
├── Models/
│   ├── ConfigMode.cs                # Enum: File, Realtime
│   ├── TrunkViewModel.cs            # Trunk display model
│   ├── TrunkTechnology.cs           # Enum: PjSip, Sip, Iax2
│   ├── TrunkStatus.cs               # Enum: Registered, Unreachable, Unknown, Rejected
│   ├── TrunkDetailViewModel.cs      # Detailed trunk with config sections
│   └── TrunkConfig.cs               # Editable trunk configuration
│
├── wwwroot/
│   └── css/
│       └── dashboard.css            # Complete design system (~1500 lines)
│
├── docs/
│   └── screenshots/                 # Place screenshots here
│
└── logs/                            # Runtime Serilog output
```

---

## How It Works

### Connection Lifecycle

```
App Start
  │
  ▼
AsteriskMonitorService.StartAsync()
  │
  ├── For each server in config:
  │     │
  │     ├── IAmiConnectionFactory.CreateAndConnectAsync(options)
  │     │     └── TCP connect → AMI login → MD5 challenge-response
  │     │
  │     ├── new AsteriskServer(connection, logger)
  │     │
  │     ├── connection.Subscribe(EventLogObserver)
  │     │     └── Every AMI event → EventLogService circular buffer
  │     │
  │     ├── connection.Subscribe(CallFlowObserver)
  │     │     └── Every AMI event → CallFlowTracker correlation engine
  │     │
  │     └── server.StartAsync()
  │           ├── Subscribe to AMI events (NewChannel, Hangup, QueueMember*, Agent*, MeetMe*)
  │           ├── Register LiveMetrics observable gauges
  │           ├── Send StatusAction → populate ChannelManager
  │           ├── Send QueueStatusAction → populate QueueManager
  │           └── Send AgentsAction → populate AgentManager
  │
  └── Servers stored in ConcurrentDictionary<string, ServerEntry>
        └── ServerEntry = (IAmiConnection, AsteriskServer, subscriptions, ConfigMode)
```

### Real-Time Updates

Blazor pages use `System.Threading.Timer` to poll the in-memory domain objects:

```csharp
// In each page's @code block:
protected override void OnInitialized()
{
    _timer = new Timer(_ => InvokeAsync(StateHasChanged),
        null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
}
```

This works because:
1. `AsteriskServer` receives AMI events via `IObserver<ManagerEvent>`
2. Events update the in-memory managers (`ChannelManager`, `QueueManager`, etc.)
3. Managers use `ConcurrentDictionary` for thread-safe access
4. Blazor timers call `StateHasChanged()` to re-render with current state
5. SignalR pushes the DOM diff to the browser

**No extra AMI connections per browser.** All browsers share the same singleton.

### Call Flow Correlation

The `CallFlowTracker` uses Asterisk's `LinkedId` (available since Asterisk 12) to group all channels that belong to the same logical call:

```
Caller dials 100 → NewChannel (LinkedId: "1234.5")
  → Queue(sales)  → QueueJoin (LinkedId: "1234.5")
  → Agent answers → AgentConnect (LinkedId: "1234.5")
  → Bridge        → Bridge (LinkedId: "1234.5")
  → Hangup        → Hangup (LinkedId: "1234.5")
```

All events with the same `LinkedId` are grouped into a single `CallFlow` object with participants, state transitions, and timing data. The tracker maintains:

- Up to **500 correlated calls** (active + recently completed)
- **5-minute retention** for completed calls
- O(1) lookup by call ID, O(n) search by caller/agent/queue

### Trunk Configuration Management

Trunk CRUD operates through an abstraction layer that supports two backends:

```
TrunkService
  │
  ├── ConfigProviderResolver.GetProvider(serverId)
  │     │
  │     ├── ConfigMode.File → FileConfigProvider
  │     │     └── Reads/writes /etc/asterisk/*.conf
  │     │
  │     └── ConfigMode.Realtime → DbConfigProvider
  │           └── PostgreSQL via Dapper (ps_endpoints, sippeers, iaxpeers)
  │
  └── After save → triggers Asterisk module reload via AMI
```

For **PJSIP** trunks, a single logical trunk maps to multiple config sections: `[endpoint]`, `[aor]`, and optionally `[transport]`. The `TrunkService` handles this mapping transparently.

### AMI Actions from the UI

The `AsteriskMonitorService` exposes `IAmiConnection` per server, allowing pages to send actions:

```csharp
// QueueDetail.razor — Pause an agent
var entry = Monitor.GetServer(ServerId);
var response = await entry.Connection.SendActionAsync(new QueuePauseAction
{
    Queue = QueueName,
    Interface = member.Interface,
    Paused = true
});
```

Available actions from the UI:
- **QueuePauseAction** — pause/unpause a queue member
- **QueueAddAction** — add a member to a queue
- **QueueRemoveAction** — remove a member from a queue
- **OriginateAsync** — originate an outbound call (via `AsteriskServer` helper)

### Metrics Collection

The Metrics page uses `System.Diagnostics.Metrics.MeterListener`:

```csharp
var listener = new MeterListener();
listener.InstrumentPublished = (instrument, listener) =>
{
    if (instrument.Meter.Name is "Asterisk.Sdk.Ami" or "Asterisk.Sdk.Live")
        listener.EnableMeasurementEvents(instrument);
};
listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
{
    // Accumulate counter values
});
listener.Start();

// Every 2 seconds:
listener.RecordObservableInstruments();  // triggers gauge callbacks
```

---

## Color Reference

### CSS Custom Properties

```css
:root {
    --color-available:  #22c55e;   /* Green  — Available, NotInUse, Up */
    --color-incall:     #ef4444;   /* Red    — On Call, InUse, Busy */
    --color-busy:       #dc2626;   /* Dark Red — DeviceBusy */
    --color-ringing:    #3b82f6;   /* Blue   — Ringing, Ring */
    --color-hold:       #f97316;   /* Orange — OnHold */
    --color-paused:     #eab308;   /* Yellow — Paused, Reconnecting */
    --color-offline:    #9ca3af;   /* Gray   — Unavailable, LoggedOff */
    --color-unknown:    #d1d5db;   /* Light Gray — Unknown, Invalid */
}
```

### Connection Status Dots (Header)

| State | Color | Meaning |
|-------|-------|---------|
| `Connected` | Green | AMI session active |
| `Connecting` / `Reconnecting` | Yellow | Connecting or auto-reconnecting |
| `Disconnected` / `Initial` | Red | No AMI connection |

### Trunk Status

| Status | Color | Meaning |
|--------|-------|---------|
| `Registered` | Green | SIP registration active |
| `Unreachable` | Red | Trunk host not responding |
| `Rejected` | Yellow | Registration rejected by peer |
| `Unknown` | Gray | Status not determined |

---

## Customization

**Change refresh intervals** — Edit the `TimeSpan.FromSeconds()` values in each page's `@code` block. Faster intervals (500ms) give snappier UIs but increase CPU usage.

**Add new pages** — Create a `.razor` file in `Components/Pages/` with an `@page "/your-route"` directive. Inject `AsteriskMonitorService` to access all servers. Add `[Authorize]` to require authentication.

**Modify colors** — Edit `wwwroot/css/dashboard.css`. All colors are defined as CSS custom properties in `:root`.

**Change authentication** — The default uses simple credential-based auth from `appsettings.json`. For production, replace with LDAP, OIDC, or any ASP.NET Core authentication provider in `Program.cs`.

**Add Realtime support** — Set `ConfigMode: "Realtime"` and provide a `RealtimeConnectionString` in server config. The dashboard will use PostgreSQL for trunk management instead of config files.

**Connect to more servers** — Add entries to the `Asterisk:Servers` array in `appsettings.json`. The dashboard handles N servers with no code changes.

---

## Troubleshooting

| Problem | Cause | Solution |
|---------|-------|----------|
| Login page rejects valid credentials | Config mismatch | Check `Auth:Username` and `Auth:Password` in `appsettings.json` |
| Dashboard starts but shows 0 calls/queues/agents | AMI connection failed silently | Check console logs for `Failed to connect to server` errors |
| `Connection refused` on port 5038 | AMI not enabled or firewall blocking | Verify `enabled = yes` in `manager.conf`, check firewall rules |
| `Authentication failed` | Wrong username/password | Check `manager.conf` credentials and `permit` ACL |
| Queues show 0 members | AMI user lacks `read = user` | Add `user` to the `read` line in `manager.conf` |
| Agents always show "Logged Off" | Agents not configured or AMI user lacks `read = agent` | Check `agents.conf` and AMI permissions |
| Originate fails with "Permission denied" | AMI user lacks `write = originate` | Add `originate` to the `write` line |
| Trunks page shows "No trunks found" | Config mode mismatch or missing config permission | Ensure `read = config` in AMI user; verify `ConfigMode` matches server setup |
| Trunk save fails in Realtime mode | Database connection issue | Check `RealtimeConnectionString` and PostgreSQL accessibility |
| Calls page shows no call flows | No active calls or `LinkedId` not available | Make test calls; `LinkedId` requires Asterisk 12+ |
| Ladder diagram is empty | Call has no bridge events | Diagram requires at least one Bridge or AgentConnect event |
| Campaign metrics all zero | No originated calls tracked | Use the Channels page to originate a call, then check Campaign Metrics |
| Page updates stop after a while | SignalR circuit disconnected | Blazor automatically reconnects; check browser console for errors |
| High CPU usage | Timer interval too fast | Increase timer intervals from 1s to 2s or 3s |
| Events page shows no events | No call activity on PBX | Make a test call; events only appear when there is AMI activity |
| Conference page empty | No active MeetMe/ConfBridge rooms | Dial into a conference room to create activity |
| Multiple browsers show same data | Expected behavior | All browsers share the same singleton — this is by design |
