# Asterisk Dashboard — Real-Time Monitoring with Blazor Server

A full-featured **Blazor Server** dashboard that demonstrates the **Asterisk.Sdk** Live API: multi-server AMI monitoring, queue management, agent tracking, channel visualization, conference rooms, SDK metrics, and live event log — all in real-time via SignalR.

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
  - [Environment Variables](#environment-variables)
- [Running the Dashboard](#running-the-dashboard)
  - [Development](#development)
  - [Docker](#docker)
  - [Reverse Proxy (Production)](#reverse-proxy-production)
- [Pages Reference](#pages-reference)
  - [Home — KPI Dashboard](#home--kpi-dashboard)
  - [Queues — Queue Overview](#queues--queue-overview)
  - [Queue Detail — Agent & Caller Management](#queue-detail--agent--caller-management)
  - [Channels — Active Calls](#channels--active-calls)
  - [Agents — Agent Monitoring](#agents--agent-monitoring)
  - [Conferences — MeetMe / ConfBridge Rooms](#conferences--meetme--confbridge-rooms)
  - [Metrics — SDK Instrumentation](#metrics--sdk-instrumentation)
  - [Events — Live AMI Event Log](#events--live-ami-event-log)
- [SDK Features Demonstrated](#sdk-features-demonstrated)
- [Project Structure](#project-structure)
- [How It Works](#how-it-works)
  - [Connection Lifecycle](#connection-lifecycle)
  - [Real-Time Updates](#real-time-updates)
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

Open [http://localhost:8080](http://localhost:8080). You should see:

- **demo-pbx** with a green connection dot in the header
- **2 queues** — `sales` (3 members, ringall) and `support` (3 members, leastrecent)
- **6 PJSIP endpoints** — 2001-2003 (sales) and 3001-3003 (support)
- **4 agents** — 1001-1004 (available after login via `*11`)

The Docker setup includes a pre-configured Asterisk 20 instance with AMI, queues, agents, conferences, and a demo dialplan. No Asterisk installation required.

To stop:

```bash
docker compose -f docker/docker-compose.dashboard.yml down
```

---

## Features

| Feature | Description |
|---------|-------------|
| **Multi-server monitoring** | Connect to 1–N Asterisk PBX servers simultaneously |
| **Real-time KPIs** | Active calls, queues, waiting callers, agents by state — updated every 2s |
| **Queue management** | View queue stats, pause/unpause agents, add/remove members, live caller wait timers |
| **Channel visualization** | All active channels with state colors, duration timers, bridged pairs, state filtering |
| **Call origination** | Originate outbound calls from the UI via `OriginateAsync()` |
| **Agent tracking** | Agent cards with state colors, reverse queue lookup (`GetQueuesForMember`) |
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
         │  │ EventLogService│ │  Circular buffer (200 entries)
         │  └────────────────┘ │
         └──────────┬──────────┘
                    │ In-memory (singleton)
         ┌──────────▼──────────┐
         │  Blazor Server      │  SignalR (built-in)
         │  7 pages + layout   │  Timer-based refresh (1–2s)
         └──────────┬──────────┘
                    │ WebSocket
         ┌──────────▼────────────────┐
         │  Browser 1  │  Browser 2  │  ... N browsers
         └─────────────┴─────────────┘
```

**Key design point:** Only 1 AMI connection per PBX server, shared across all browser sessions. The `AsteriskServer` maintains in-memory domain objects that Blazor pages read directly — no extra AMI connections per browser.

---

## Screenshots

> **Note:** Replace these placeholder images with actual screenshots after running the dashboard against your Asterisk servers.

| Page | Screenshot |
|------|-----------|
| Home (KPIs) | ![Home](docs/screenshots/home.png) |
| Queues | ![Queues](docs/screenshots/queues.png) |
| Queue Detail | ![Queue Detail](docs/screenshots/queue-detail.png) |
| Channels | ![Channels](docs/screenshots/channels.png) |
| Agents | ![Agents](docs/screenshots/agents.png) |
| Conferences | ![Conferences](docs/screenshots/conferences.png) |
| Metrics | ![Metrics](docs/screenshots/metrics.png) |
| Events | ![Events](docs/screenshots/events.png) |

---

## Prerequisites

- [.NET 10 SDK](https://dot.net/download) (10.0.100+)
- Asterisk PBX 13+ with AMI enabled (16+ recommended)
- Network access from the dashboard host to Asterisk AMI port (default: 5038)

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
  "Asterisk": {
    "Servers": [
      {
        "Id": "pbx-main",
        "Hostname": "192.168.1.10",
        "Port": 5038,
        "Username": "dashboard",
        "Password": "YourSecurePassword"
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
        "Password": "SecretEast"
      },
      {
        "Id": "pbx-west",
        "Hostname": "pbx-west.example.com",
        "Port": 5038,
        "Username": "dashboard",
        "Password": "SecretWest"
      },
      {
        "Id": "pbx-dr",
        "Hostname": "10.20.30.40",
        "Port": 5038,
        "Username": "dashboard",
        "Password": "SecretDR"
      }
    ]
  }
}
```

### Environment Variables

Override settings via environment variables (ASP.NET Core convention):

```bash
export Asterisk__Servers__0__Id=pbx-main
export Asterisk__Servers__0__Hostname=192.168.1.10
export Asterisk__Servers__0__Port=5038
export Asterisk__Servers__0__Username=dashboard
export Asterisk__Servers__0__Password=YourSecurePassword
```

Or via command-line:

```bash
dotnet run --project Examples/DashboardExample/ \
  --Asterisk:Servers:0:Hostname=192.168.1.10 \
  --Asterisk:Servers:0:Username=dashboard \
  --Asterisk:Servers:0:Password=YourSecurePassword
```

---

## Running the Dashboard

### Development

```bash
# From the repository root
dotnet run --project Examples/DashboardExample/

# Opens at http://localhost:5000
```

To specify a custom port:

```bash
dotnet run --project Examples/DashboardExample/ --urls "http://0.0.0.0:8080"
```

### Docker

Create a `Dockerfile` in the `Examples/DashboardExample/` directory:

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish Examples/DashboardExample/DashboardExample.csproj \
    -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "DashboardExample.dll"]
```

Build and run:

```bash
# From repository root
docker build -t asterisk-dashboard -f Examples/DashboardExample/Dockerfile .
docker run -d \
  --name dashboard \
  -p 8080:8080 \
  -e Asterisk__Servers__0__Hostname=192.168.1.10 \
  -e Asterisk__Servers__0__Username=dashboard \
  -e Asterisk__Servers__0__Password=YourSecurePassword \
  asterisk-dashboard
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

Agent state colors:

| State | Dot | Background |
|-------|-----|------------|
| Available | `#22c55e` | `#f0fdf4` |
| On Call | `#ef4444` | `#fef2f2` |
| Paused | `#eab308` | `#fefce8` |
| Logged Off | `#9ca3af` | `#f9fafb` |

**Refresh interval:** 2 seconds.

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
| `IAmiConnection.Subscribe()` | `AsteriskMonitorService` — event log observer |
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
Examples/DashboardExample/
├── DashboardExample.csproj          # Blazor Server, IsAotCompatible=false
├── Program.cs                       # DI setup: AddRazorComponents, AddAsteriskMultiServer
├── appsettings.json                 # Server connection configuration
├── Services/
│   ├── AsteriskMonitorService.cs    # IHostedService: connects servers, event subscriptions
│   └── EventLogService.cs           # Thread-safe circular buffer (ConcurrentQueue)
├── Components/
│   ├── App.razor                    # HTML root with InteractiveServer render mode
│   ├── Routes.razor                 # Router with MainLayout default
│   ├── _Imports.razor               # Global using directives
│   ├── Layout/
│   │   └── MainLayout.razor         # Sidebar nav + header with connection dots
│   ├── Pages/
│   │   ├── Home.razor               # KPI cards + queue summary table
│   │   ├── Queues.razor             # Queue card grid with health indicators
│   │   ├── QueueDetail.razor        # Agent cards, callers, pause/add/remove actions
│   │   ├── Channels.razor           # Channel table, state filters, originate form
│   │   ├── Agents.razor             # Agent cards, state filters, queue membership
│   │   ├── Conferences.razor        # MeetMe/ConfBridge rooms and participants
│   │   ├── Metrics.razor            # MeterListener for AmiMetrics + LiveMetrics
│   │   └── Events.razor             # Live AMI event log with type filtering
│   └── Shared/
│       └── ServerSelector.razor     # Reusable server dropdown with two-way binding
├── wwwroot/
│   └── css/
│       └── dashboard.css            # Complete design system (~330 lines)
├── docs/
│   └── screenshots/                 # Place screenshots here
└── README.md                        # This file
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
  │     └── server.StartAsync()
  │           ├── Subscribe to AMI events (NewChannel, Hangup, QueueMember*, Agent*, MeetMe*)
  │           ├── Register LiveMetrics observable gauges
  │           ├── Send StatusAction → populate ChannelManager
  │           ├── Send QueueStatusAction → populate QueueManager
  │           └── Send AgentsAction → populate AgentManager
  │
  └── Servers stored in ConcurrentDictionary<string, ServerEntry>
        └── ServerEntry = (IAmiConnection, AsteriskServer, IDisposable subscription)
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

---

## Customization

**Change refresh intervals** — Edit the `TimeSpan.FromSeconds()` values in each page's `@code` block. Faster intervals (500ms) give snappier UIs but increase CPU usage.

**Add new pages** — Create a `.razor` file in `Components/Pages/` with an `@page "/your-route"` directive. Inject `AsteriskMonitorService` to access all servers.

**Modify colors** — Edit `wwwroot/css/dashboard.css`. All colors are defined as CSS custom properties in `:root`.

**Add authentication** — Blazor Server supports ASP.NET Core authentication. Add `builder.Services.AddAuthentication()` and `[Authorize]` attributes to pages.

**Connect to more servers** — Add entries to the `Asterisk:Servers` array in `appsettings.json`. The dashboard handles N servers with no code changes.

---

## Troubleshooting

| Problem | Cause | Solution |
|---------|-------|----------|
| Dashboard starts but shows 0 calls/queues/agents | AMI connection failed silently | Check console logs for `Failed to connect to server` errors |
| `Connection refused` on port 5038 | AMI not enabled or firewall blocking | Verify `enabled = yes` in `manager.conf`, check firewall rules |
| `Authentication failed` | Wrong username/password | Check `manager.conf` credentials and `permit` ACL |
| Queues show 0 members | AMI user lacks `read = user` | Add `user` to the `read` line in `manager.conf` |
| Agents always show "Logged Off" | Agents not configured or AMI user lacks `read = agent` | Check `agents.conf` and AMI permissions |
| Originate fails with "Permission denied" | AMI user lacks `write = originate` | Add `originate` to the `write` line |
| Page updates stop after a while | SignalR circuit disconnected | Blazor automatically reconnects; check browser console for errors |
| High CPU usage | Timer interval too fast | Increase timer intervals from 1s to 2s or 3s |
| Events page shows no events | No call activity on PBX | Make a test call; events only appear when there is AMI activity |
| Conference page empty | No active MeetMe/ConfBridge rooms | Dial into a conference room to create activity |
| Multiple browsers show same data | Expected behavior | All browsers share the same singleton — this is by design |
