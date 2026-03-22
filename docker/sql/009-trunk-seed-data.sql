-- Trunk seed data for realtime database
-- These trunks supplement the file-based definitions in pjsip.conf.
-- Sorcery loads from both sources (config first, then realtime).

-- AOR for trunk-file (endpoint + identify already in 002-seed-data.sql)
INSERT INTO ps_aors (id, max_contacts, contact, qualify_frequency)
VALUES ('trunk-file', 1, 'sip:asterisk-file:5060', 30)
ON CONFLICT (id) DO NOTHING;

-- PSTN Emulator trunk (connects to demo-pstn container)
INSERT INTO ps_endpoints (id, transport, aors, context, disallow, allow, direct_media, rtp_symmetric, force_rport, callerid)
VALUES ('pstn-trunk-db', 'transport-udp', 'pstn-trunk-db', 'from-trunk', 'all', 'ulaw,alaw', 'no', 'yes', 'yes', '"Realtime PBX" <8888>')
ON CONFLICT (id) DO NOTHING;

INSERT INTO ps_aors (id, max_contacts, contact, qualify_frequency)
VALUES ('pstn-trunk-db', 1, 'sip:demo-pstn:5060', 30)
ON CONFLICT (id) DO NOTHING;

INSERT INTO ps_endpoint_id_ips (id, endpoint, match)
VALUES ('pstn-trunk-db-identify', 'pstn-trunk-db', 'demo-pstn')
ON CONFLICT (id) DO NOTHING;
