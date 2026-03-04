-- Asterisk Realtime schema for PostgreSQL
-- Compatible with res_config_odbc / Sorcery

-- PJSIP Endpoints
CREATE TABLE IF NOT EXISTS ps_endpoints (
    id          VARCHAR(40) NOT NULL PRIMARY KEY,
    transport   VARCHAR(40),
    aors        VARCHAR(200),
    auth        VARCHAR(40),
    context     VARCHAR(40) DEFAULT 'default',
    disallow    VARCHAR(200) DEFAULT 'all',
    allow       VARCHAR(200) DEFAULT 'ulaw,alaw',
    direct_media VARCHAR(3) DEFAULT 'no',
    dtmf_mode   VARCHAR(10) DEFAULT 'rfc4733',
    rtp_symmetric VARCHAR(3) DEFAULT 'yes',
    force_rport VARCHAR(3) DEFAULT 'yes',
    rewrite_contact VARCHAR(3) DEFAULT 'yes',
    callerid    VARCHAR(100),
    language    VARCHAR(10),
    mailboxes   VARCHAR(200),
    named_call_group VARCHAR(200),
    named_pickup_group VARCHAR(200),
    call_group  VARCHAR(200),
    pickup_group VARCHAR(200),
    device_state_busy_at INTEGER,
    max_contacts INTEGER DEFAULT 1,
    trust_id_inbound VARCHAR(3),
    send_pai     VARCHAR(3),
    outbound_auth VARCHAR(40),
    outbound_proxy VARCHAR(256)
);

-- PJSIP Auth
CREATE TABLE IF NOT EXISTS ps_auths (
    id          VARCHAR(40) NOT NULL PRIMARY KEY,
    auth_type   VARCHAR(20) DEFAULT 'userpass',
    password    VARCHAR(80),
    username    VARCHAR(40),
    realm       VARCHAR(40),
    md5_cred    VARCHAR(80),
    nonce_lifetime INTEGER
);

-- PJSIP AORs (Address of Record)
CREATE TABLE IF NOT EXISTS ps_aors (
    id              VARCHAR(40) NOT NULL PRIMARY KEY,
    max_contacts    INTEGER DEFAULT 1,
    remove_existing VARCHAR(3) DEFAULT 'yes',
    contact         VARCHAR(256),
    qualify_frequency INTEGER DEFAULT 60,
    qualify_timeout FLOAT DEFAULT 3.0,
    default_expiration INTEGER DEFAULT 3600,
    minimum_expiration INTEGER DEFAULT 60,
    maximum_expiration INTEGER DEFAULT 7200,
    support_path    VARCHAR(3)
);

-- PJSIP Registrations (outbound)
CREATE TABLE IF NOT EXISTS ps_registrations (
    id                  VARCHAR(40) NOT NULL PRIMARY KEY,
    transport           VARCHAR(40),
    outbound_auth       VARCHAR(40),
    server_uri          VARCHAR(256),
    client_uri          VARCHAR(256),
    contact_user        VARCHAR(40),
    retry_interval      INTEGER DEFAULT 60,
    forbidden_retry_interval INTEGER DEFAULT 10,
    max_retries         INTEGER DEFAULT 10,
    expiration          INTEGER DEFAULT 3600,
    auth_rejection_permanent VARCHAR(3) DEFAULT 'yes',
    line                VARCHAR(3),
    endpoint            VARCHAR(40)
);

-- PJSIP Transports (reference only — typically file-based)
CREATE TABLE IF NOT EXISTS ps_transports (
    id          VARCHAR(40) NOT NULL PRIMARY KEY,
    protocol    VARCHAR(10) DEFAULT 'udp',
    bind        VARCHAR(40) DEFAULT '0.0.0.0',
    local_net   VARCHAR(40),
    external_media_address VARCHAR(40),
    external_signaling_address VARCHAR(40)
);

-- chan_sip peers
CREATE TABLE IF NOT EXISTS sippeers (
    id          SERIAL PRIMARY KEY,
    name        VARCHAR(40) NOT NULL UNIQUE,
    type        VARCHAR(10) DEFAULT 'peer',
    host        VARCHAR(40) DEFAULT 'dynamic',
    secret      VARCHAR(80),
    context     VARCHAR(40) DEFAULT 'default',
    dtmfmode    VARCHAR(20) DEFAULT 'rfc2833',
    disallow    VARCHAR(200) DEFAULT 'all',
    allow       VARCHAR(200) DEFAULT 'ulaw,alaw',
    nat         VARCHAR(30) DEFAULT 'force_rport,comedia',
    qualify     VARCHAR(10) DEFAULT 'yes',
    directmedia VARCHAR(3) DEFAULT 'no',
    port        INTEGER DEFAULT 5060,
    callerid    VARCHAR(100),
    insecure    VARCHAR(40),
    fromdomain  VARCHAR(40),
    fromuser    VARCHAR(40)
);
CREATE INDEX IF NOT EXISTS idx_sippeers_name ON sippeers (name);
CREATE INDEX IF NOT EXISTS idx_sippeers_host ON sippeers (host);

-- chan_iax2 peers
CREATE TABLE IF NOT EXISTS iaxpeers (
    id          SERIAL PRIMARY KEY,
    name        VARCHAR(40) NOT NULL UNIQUE,
    type        VARCHAR(10) DEFAULT 'peer',
    host        VARCHAR(40) DEFAULT 'dynamic',
    secret      VARCHAR(80),
    context     VARCHAR(40) DEFAULT 'default',
    disallow    VARCHAR(200) DEFAULT 'all',
    allow       VARCHAR(200) DEFAULT 'ulaw,alaw',
    qualify     VARCHAR(10) DEFAULT 'yes',
    port        INTEGER DEFAULT 4569,
    trunk       VARCHAR(3) DEFAULT 'yes',
    auth        VARCHAR(20) DEFAULT 'md5',
    encryption  VARCHAR(10),
    transfer    VARCHAR(10)
);
CREATE INDEX IF NOT EXISTS idx_iaxpeers_name ON iaxpeers (name);
CREATE INDEX IF NOT EXISTS idx_iaxpeers_host ON iaxpeers (host);

-- Queue definitions
CREATE TABLE IF NOT EXISTS queue_table (
    name                VARCHAR(128) NOT NULL PRIMARY KEY,
    musiconhold         VARCHAR(128),
    announce            VARCHAR(128),
    context             VARCHAR(128),
    timeout             INTEGER DEFAULT 15,
    ringinuse           VARCHAR(3) DEFAULT 'no',
    setinterfacevar     VARCHAR(3) DEFAULT 'yes',
    setqueuevar         VARCHAR(3) DEFAULT 'yes',
    setqueueentryvar    VARCHAR(3) DEFAULT 'yes',
    monitor_format      VARCHAR(10),
    membermacro         VARCHAR(128),
    membergosubcontext  VARCHAR(128),
    queue_youarenext    VARCHAR(128),
    queue_thereare      VARCHAR(128),
    queue_callswaiting  VARCHAR(128),
    queue_holdtime      VARCHAR(128),
    queue_minutes       VARCHAR(128),
    queue_seconds       VARCHAR(128),
    queue_thankyou      VARCHAR(128),
    strategy            VARCHAR(20) DEFAULT 'ringall',
    joinempty           VARCHAR(40),
    leavewhenempty      VARCHAR(40),
    eventwhencalled     VARCHAR(3) DEFAULT 'yes',
    eventmemberstatus   VARCHAR(3) DEFAULT 'yes',
    reportholdtime      VARCHAR(3) DEFAULT 'yes',
    weight              INTEGER DEFAULT 0,
    wrapuptime          INTEGER DEFAULT 0,
    maxlen              INTEGER DEFAULT 0,
    servicelevel        INTEGER DEFAULT 60,
    retry               INTEGER DEFAULT 5,
    autopause           VARCHAR(3) DEFAULT 'no'
);

-- Queue member assignments
CREATE TABLE IF NOT EXISTS queue_members (
    queue_name  VARCHAR(128) NOT NULL,
    interface   VARCHAR(128) NOT NULL,
    membername  VARCHAR(128),
    state_interface VARCHAR(128),
    penalty     INTEGER DEFAULT 0,
    paused      INTEGER DEFAULT 0,
    uniqueid    VARCHAR(40),
    PRIMARY KEY (queue_name, interface)
);
CREATE INDEX IF NOT EXISTS idx_queue_members_queue ON queue_members (queue_name);
CREATE INDEX IF NOT EXISTS idx_queue_members_interface ON queue_members (interface);

-- Voicemail boxes
CREATE TABLE IF NOT EXISTS voicemail (
    id          SERIAL PRIMARY KEY,
    context     VARCHAR(40) NOT NULL DEFAULT 'default',
    mailbox     VARCHAR(40) NOT NULL,
    password    VARCHAR(40) DEFAULT '1234',
    fullname    VARCHAR(80),
    email       VARCHAR(80),
    pager       VARCHAR(80),
    tz          VARCHAR(10) DEFAULT 'central',
    attach      VARCHAR(3) DEFAULT 'yes',
    saycid      VARCHAR(3) DEFAULT 'yes',
    dialout     VARCHAR(40),
    callback    VARCHAR(40),
    review      VARCHAR(3) DEFAULT 'no',
    operator    VARCHAR(3) DEFAULT 'no',
    envelope    VARCHAR(3) DEFAULT 'no',
    sayduration VARCHAR(3) DEFAULT 'no',
    saydurationm INTEGER DEFAULT 1,
    maxmsg      INTEGER DEFAULT 100,
    UNIQUE (context, mailbox)
);
CREATE INDEX IF NOT EXISTS idx_voicemail_mailbox ON voicemail (context, mailbox);
