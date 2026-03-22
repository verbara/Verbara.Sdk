-- Route seed data for demo PbxAdmin environment (Realtime server only)
-- File-mode routes are stored in JSON files, not in the database.

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
