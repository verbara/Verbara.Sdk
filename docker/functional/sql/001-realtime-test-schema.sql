-- Realtime functional test schema
-- Only tables needed for Phase 5A tests

CREATE TABLE IF NOT EXISTS ps_endpoints (
    id VARCHAR(40) NOT NULL PRIMARY KEY,
    transport VARCHAR(40),
    aors VARCHAR(200),
    auth VARCHAR(40),
    context VARCHAR(40) DEFAULT 'default',
    disallow VARCHAR(200) DEFAULT 'all',
    allow VARCHAR(200) DEFAULT 'ulaw,alaw',
    direct_media VARCHAR(3) DEFAULT 'no',
    dtmf_mode VARCHAR(10) DEFAULT 'rfc4733',
    force_rport VARCHAR(3) DEFAULT 'yes',
    rewrite_contact VARCHAR(3) DEFAULT 'yes',
    rtp_symmetric VARCHAR(3) DEFAULT 'yes',
    callerid VARCHAR(100),
    mailboxes VARCHAR(200),
    language VARCHAR(10)
);

CREATE TABLE IF NOT EXISTS ps_auths (
    id VARCHAR(40) NOT NULL PRIMARY KEY,
    auth_type VARCHAR(40) DEFAULT 'userpass',
    password VARCHAR(80),
    username VARCHAR(40)
);

CREATE TABLE IF NOT EXISTS ps_aors (
    id VARCHAR(40) NOT NULL PRIMARY KEY,
    max_contacts INTEGER DEFAULT 1,
    remove_existing VARCHAR(3) DEFAULT 'yes'
);

CREATE TABLE IF NOT EXISTS queue_table (
    name VARCHAR(128) NOT NULL PRIMARY KEY,
    strategy VARCHAR(40) DEFAULT 'ringall',
    timeout INTEGER DEFAULT 30,
    wrapuptime INTEGER DEFAULT 0,
    maxlen INTEGER DEFAULT 0,
    musicclass VARCHAR(80) DEFAULT 'default'
);

CREATE TABLE IF NOT EXISTS queue_members (
    queue_name VARCHAR(128) NOT NULL,
    interface VARCHAR(128) NOT NULL,
    membername VARCHAR(40),
    state_interface VARCHAR(128),
    penalty INTEGER DEFAULT 0,
    paused INTEGER DEFAULT 0,
    PRIMARY KEY (queue_name, interface)
);
