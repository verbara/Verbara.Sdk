-- Seed data: equivalent to dashboard-config/pjsip.conf demo endpoints

-- Sales team endpoints (2001-2003)
INSERT INTO ps_endpoints (id, transport, aors, auth, context, disallow, allow, direct_media, callerid) VALUES
    ('2001', 'transport-udp', '2001', '2001', 'from-internal', 'all', 'ulaw,alaw,opus', 'no', '"Sales Agent 1" <2001>'),
    ('2002', 'transport-udp', '2002', '2002', 'from-internal', 'all', 'ulaw,alaw,opus', 'no', '"Sales Agent 2" <2002>'),
    ('2003', 'transport-udp', '2003', '2003', 'from-internal', 'all', 'ulaw,alaw,opus', 'no', '"Sales Agent 3" <2003>');

INSERT INTO ps_auths (id, auth_type, password, username) VALUES
    ('2001', 'userpass', 'secret2001', '2001'),
    ('2002', 'userpass', 'secret2002', '2002'),
    ('2003', 'userpass', 'secret2003', '2003');

INSERT INTO ps_aors (id, max_contacts, remove_existing, qualify_frequency) VALUES
    ('2001', 1, 'yes', 60),
    ('2002', 1, 'yes', 60),
    ('2003', 1, 'yes', 60);

-- Support team endpoints (3001-3003)
INSERT INTO ps_endpoints (id, transport, aors, auth, context, disallow, allow, direct_media, callerid) VALUES
    ('3001', 'transport-udp', '3001', '3001', 'from-internal', 'all', 'ulaw,alaw,opus', 'no', '"Support Agent 1" <3001>'),
    ('3002', 'transport-udp', '3002', '3002', 'from-internal', 'all', 'ulaw,alaw,opus', 'no', '"Support Agent 2" <3002>'),
    ('3003', 'transport-udp', '3003', '3003', 'from-internal', 'all', 'ulaw,alaw,opus', 'no', '"Support Agent 3" <3003>');

INSERT INTO ps_auths (id, auth_type, password, username) VALUES
    ('3001', 'userpass', 'secret3001', '3001'),
    ('3002', 'userpass', 'secret3002', '3002'),
    ('3003', 'userpass', 'secret3003', '3003');

INSERT INTO ps_aors (id, max_contacts, remove_existing, qualify_frequency) VALUES
    ('3001', 1, 'yes', 60),
    ('3002', 1, 'yes', 60),
    ('3003', 1, 'yes', 60);

-- Queues
INSERT INTO queue_table (name, strategy, timeout, ringinuse, wrapuptime, servicelevel, maxlen) VALUES
    ('sales', 'ringall', 15, 'no', 5, 60, 0),
    ('support', 'leastrecent', 20, 'no', 10, 90, 0);

-- Queue members
INSERT INTO queue_members (queue_name, interface, membername, penalty) VALUES
    ('sales', 'PJSIP/2001', 'Sales Agent 1', 0),
    ('sales', 'PJSIP/2002', 'Sales Agent 2', 0),
    ('sales', 'PJSIP/2003', 'Sales Agent 3', 1),
    ('support', 'PJSIP/3001', 'Support Agent 1', 0),
    ('support', 'PJSIP/3002', 'Support Agent 2', 0),
    ('support', 'PJSIP/3003', 'Support Agent 3', 1);
