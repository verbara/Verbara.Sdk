-- Asterisk Realtime PJSIP tables (minimal for SDK functional tests)

CREATE TABLE IF NOT EXISTS ps_endpoints (
    id                          VARCHAR(40) NOT NULL PRIMARY KEY,
    transport                   VARCHAR(40),
    aors                        VARCHAR(200),
    auth                        VARCHAR(40),
    context                     VARCHAR(40) DEFAULT 'default',
    disallow                    VARCHAR(200) DEFAULT 'all',
    allow                       VARCHAR(200) DEFAULT 'ulaw,alaw',
    direct_media                VARCHAR(3) DEFAULT 'no',
    dtmf_mode                   VARCHAR(10) DEFAULT 'rfc4733',
    force_rport                 VARCHAR(3) DEFAULT 'yes',
    rewrite_contact             VARCHAR(3) DEFAULT 'yes',
    rtp_symmetric               VARCHAR(3) DEFAULT 'yes',
    callerid                    VARCHAR(100),
    mailboxes                   VARCHAR(200),
    voicemail_extension         VARCHAR(40),
    from_user                   VARCHAR(40),
    from_domain                 VARCHAR(40),
    language                    VARCHAR(10),
    accountcode                 VARCHAR(80),
    named_call_group            VARCHAR(40),
    named_pickup_group          VARCHAR(40),
    device_state_busy_at        INTEGER,
    t38_udptl                   VARCHAR(3),
    t38_udptl_ec                VARCHAR(20),
    t38_udptl_maxdatagram       INTEGER,
    t38_udptl_nat               VARCHAR(3),
    tone_zone                   VARCHAR(40),
    identify_by                 VARCHAR(80),
    ice_support                 VARCHAR(3),
    send_pai                    VARCHAR(3),
    send_rpid                   VARCHAR(3),
    send_diversion              VARCHAR(3),
    moh_suggest                 VARCHAR(40),
    outbound_auth               VARCHAR(40),
    outbound_proxy              VARCHAR(256),
    media_address               VARCHAR(40),
    external_media_address      VARCHAR(40),
    connected_line_method       VARCHAR(40),
    direct_media_method         VARCHAR(40),
    direct_media_glare_mitigation VARCHAR(40),
    disable_direct_media_on_nat VARCHAR(3),
    rtp_ipv6                    VARCHAR(3),
    trust_id_inbound            VARCHAR(3),
    trust_id_outbound           VARCHAR(3),
    use_ptime                   VARCHAR(3),
    use_avpf                    VARCHAR(3),
    media_use_received_transport VARCHAR(3),
    media_encryption            VARCHAR(40)
);

CREATE TABLE IF NOT EXISTS ps_auths (
    id                  VARCHAR(40) NOT NULL PRIMARY KEY,
    auth_type           VARCHAR(40) DEFAULT 'userpass',
    password            VARCHAR(80),
    md5_cred            VARCHAR(80),
    username            VARCHAR(40),
    realm               VARCHAR(40),
    nonce_lifetime      INTEGER DEFAULT 32
);

CREATE TABLE IF NOT EXISTS ps_aors (
    id                   VARCHAR(40) NOT NULL PRIMARY KEY,
    contact              VARCHAR(255),
    default_expiration   INTEGER DEFAULT 3600,
    mailboxes            VARCHAR(80),
    max_contacts         INTEGER DEFAULT 1,
    minimum_expiration   INTEGER DEFAULT 60,
    remove_existing      VARCHAR(3) DEFAULT 'yes',
    qualify_frequency    INTEGER DEFAULT 60,
    authenticate_qualify VARCHAR(3),
    maximum_expiration   INTEGER DEFAULT 7200,
    outbound_proxy       VARCHAR(40),
    support_path         VARCHAR(3)
);

CREATE TABLE IF NOT EXISTS ps_contacts (
    id                  VARCHAR(255) NOT NULL PRIMARY KEY,
    uri                 VARCHAR(255),
    expiration_time     BIGINT,
    qualify_frequency   INTEGER DEFAULT 60,
    outbound_proxy      VARCHAR(40),
    path                TEXT,
    user_agent          VARCHAR(255),
    qualify_timeout     FLOAT DEFAULT 3.0,
    reg_server          VARCHAR(20),
    authenticate_qualify VARCHAR(3),
    via_addr            VARCHAR(40),
    via_port            INTEGER,
    call_id             VARCHAR(255),
    endpoint_name       VARCHAR(40),
    prune_on_boot       VARCHAR(3)
);

CREATE TABLE IF NOT EXISTS ps_domain_aliases (
    id      VARCHAR(40) NOT NULL PRIMARY KEY,
    domain  VARCHAR(80)
);

CREATE TABLE IF NOT EXISTS ps_endpoint_id_ips (
    id              VARCHAR(40) NOT NULL PRIMARY KEY,
    endpoint        VARCHAR(40),
    match           VARCHAR(80),
    srv_lookups     VARCHAR(3),
    match_header    VARCHAR(255)
);

CREATE TABLE IF NOT EXISTS ps_registrations (
    id                      VARCHAR(40) NOT NULL PRIMARY KEY,
    auth_rejection_permanent VARCHAR(3),
    client_uri              VARCHAR(255),
    contact_user            VARCHAR(40),
    expiration              INTEGER,
    max_retries             INTEGER,
    outbound_auth           VARCHAR(40),
    outbound_proxy          VARCHAR(40),
    retry_interval          INTEGER,
    forbidden_retry_interval INTEGER,
    server_uri              VARCHAR(255),
    transport               VARCHAR(40),
    support_path            VARCHAR(3),
    line                    VARCHAR(3),
    endpoint                VARCHAR(40)
);

-- Queue Realtime tables (Asterisk default table names)

CREATE TABLE IF NOT EXISTS queue_table (
    name                VARCHAR(128) NOT NULL PRIMARY KEY,
    musiconhold         VARCHAR(128) DEFAULT 'default',
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
    queue_reporthold    VARCHAR(128),
    announce_frequency  INTEGER DEFAULT 0,
    announce_round_seconds INTEGER DEFAULT 0,
    announce_holdtime   VARCHAR(128),
    retry               INTEGER DEFAULT 5,
    wrapuptime          INTEGER DEFAULT 0,
    maxlen              INTEGER DEFAULT 0,
    servicelevel        INTEGER DEFAULT 60,
    strategy            VARCHAR(40) DEFAULT 'ringall',
    joinempty           VARCHAR(40),
    leavewhenempty      VARCHAR(40),
    reportholdtime      VARCHAR(3) DEFAULT 'no',
    weight              INTEGER DEFAULT 0,
    timeoutrestart      VARCHAR(3) DEFAULT 'no',
    periodic_announce   VARCHAR(128),
    periodic_announce_frequency INTEGER DEFAULT 0,
    autopause           VARCHAR(3) DEFAULT 'no',
    autopausedelay      INTEGER DEFAULT 0,
    autopausebusy       VARCHAR(3) DEFAULT 'no',
    autopauseunavail    VARCHAR(3) DEFAULT 'no'
);

CREATE TABLE IF NOT EXISTS queue_members (
    queue_name      VARCHAR(128) NOT NULL,
    interface       VARCHAR(128) NOT NULL,
    membername      VARCHAR(128),
    state_interface VARCHAR(128),
    penalty         INTEGER DEFAULT 0,
    paused          INTEGER DEFAULT 0,
    uniqueid        VARCHAR(40),
    wrapuptime      INTEGER,
    ringinuse       VARCHAR(3),
    PRIMARY KEY (queue_name, interface)
);
