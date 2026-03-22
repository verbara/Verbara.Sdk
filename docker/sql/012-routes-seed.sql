-- Route seed data for demo PbxAdmin environment
-- Mirrors the static dialplan entries in extensions.conf for both servers

-- =====================================================
-- pbx-realtime: Inbound routes (from trunk-file)
-- =====================================================
INSERT INTO routes_inbound (server_id, name, did_pattern, destination_type, destination, priority, enabled, notes) VALUES
    ('pbx-realtime', 'Local Extensions 2XXX', '_2XXX', 'extension', '${EXTEN}', 10, true, 'Direct dial to realtime sales extensions'),
    ('pbx-realtime', 'Local Extensions 3XXX', '_3XXX', 'extension', '${EXTEN}', 20, true, 'Direct dial to realtime support extensions'),
    ('pbx-realtime', 'Sales Queue', '102', 'queue', 'sales', 30, true, 'Route DID 102 to sales queue'),
    ('pbx-realtime', 'Support Queue', '103', 'queue', 'support', 40, true, 'Route DID 103 to support queue');

-- =====================================================
-- pbx-realtime: Outbound routes
-- =====================================================
INSERT INTO routes_outbound (server_id, name, dial_pattern, priority, enabled, notes) VALUES
    ('pbx-realtime', 'To File PBX (4XXX)', '_4XXX', 10, true, 'Route 4XXX extensions via trunk to file PBX'),
    ('pbx-realtime', 'To File PBX (5XXX)', '_5XXX', 20, true, 'Route 5XXX extensions via trunk to file PBX'),
    ('pbx-realtime', 'PSTN Emulator', '_100X', 30, true, 'Route 100X to PSTN emulator (test scenarios)'),
    ('pbx-realtime', 'PSTN Emulator 1010', '1010', 40, true, 'Route 1010 to PSTN emulator (quick answer)');

INSERT INTO route_trunks (outbound_route_id, trunk_name, trunk_technology, sequence)
SELECT id, 'trunk-file', 'PjSip', 1 FROM routes_outbound WHERE server_id = 'pbx-realtime' AND name = 'To File PBX (4XXX)'
UNION ALL
SELECT id, 'trunk-file', 'PjSip', 1 FROM routes_outbound WHERE server_id = 'pbx-realtime' AND name = 'To File PBX (5XXX)'
UNION ALL
SELECT id, 'pstn-trunk-db', 'PjSip', 1 FROM routes_outbound WHERE server_id = 'pbx-realtime' AND name = 'PSTN Emulator'
UNION ALL
SELECT id, 'pstn-trunk-db', 'PjSip', 1 FROM routes_outbound WHERE server_id = 'pbx-realtime' AND name = 'PSTN Emulator 1010';

-- =====================================================
-- pbx-file: Inbound routes (from trunks)
-- =====================================================
INSERT INTO routes_inbound (server_id, name, did_pattern, destination_type, destination, priority, enabled, notes) VALUES
    ('pbx-file', 'Local Extensions 4XXX', '_4XXX', 'extension', '${EXTEN}', 10, true, 'Direct dial to file sales extensions'),
    ('pbx-file', 'Local Extensions 5XXX', '_5XXX', 'extension', '${EXTEN}', 20, true, 'Direct dial to file support extensions'),
    ('pbx-file', 'Sales2 Queue', '102', 'queue', 'sales2', 30, true, 'Route DID 102 to sales2 queue'),
    ('pbx-file', 'Support2 Queue', '103', 'queue', 'support2', 40, true, 'Route DID 103 to support2 queue');

-- =====================================================
-- pbx-file: Outbound routes
-- =====================================================
INSERT INTO routes_outbound (server_id, name, dial_pattern, priority, enabled, notes) VALUES
    ('pbx-file', 'To Realtime PBX (2XXX)', '_2XXX', 10, true, 'Route 2XXX extensions via trunk to realtime PBX'),
    ('pbx-file', 'To Realtime PBX (3XXX)', '_3XXX', 20, true, 'Route 3XXX extensions via trunk to realtime PBX'),
    ('pbx-file', 'PSTN Emulator', '_100X', 30, true, 'Route 100X to PSTN emulator (test scenarios)'),
    ('pbx-file', 'PSTN Emulator 1010', '1010', 40, true, 'Route 1010 to PSTN emulator (quick answer)');

INSERT INTO route_trunks (outbound_route_id, trunk_name, trunk_technology, sequence)
SELECT id, 'trunk-realtime', 'PjSip', 1 FROM routes_outbound WHERE server_id = 'pbx-file' AND name = 'To Realtime PBX (2XXX)'
UNION ALL
SELECT id, 'trunk-realtime', 'PjSip', 1 FROM routes_outbound WHERE server_id = 'pbx-file' AND name = 'To Realtime PBX (3XXX)'
UNION ALL
SELECT id, 'pstn-trunk', 'PjSip', 1 FROM routes_outbound WHERE server_id = 'pbx-file' AND name = 'PSTN Emulator'
UNION ALL
SELECT id, 'pstn-trunk', 'PjSip', 1 FROM routes_outbound WHERE server_id = 'pbx-file' AND name = 'PSTN Emulator 1010';
