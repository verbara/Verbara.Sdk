-- Phase 2 seed data for the Realtime PBX (server_id = 'pbx-realtime')

-- Recording policies
INSERT INTO recording_policies (server_id, name, mode, format, storage_path, retention_days, mix_monitor_options) VALUES
    ('pbx-realtime', 'Sales Calls', 'Always', 'wav', '/var/spool/asterisk/monitor/', 90, 'b'),
    ('pbx-realtime', 'Support Calls', 'OnDemand', 'wav', '/var/spool/asterisk/monitor/', 30, NULL);

INSERT INTO recording_policy_targets (policy_id, target_type, target_value) VALUES
    (1, 'queue', 'sales'),
    (2, 'queue', 'support');

-- MOH classes
INSERT INTO moh_classes (server_id, name, mode, directory, sort) VALUES
    ('pbx-realtime', 'default', 'files', '/var/lib/asterisk/moh', 'random'),
    ('pbx-realtime', 'jazz', 'files', '/var/lib/asterisk/moh/jazz', 'random');

-- Conference configs
INSERT INTO conference_configs (server_id, name, number, max_members, pin, admin_pin, record, music_on_hold) VALUES
    ('pbx-realtime', 'general', '801', 0, NULL, NULL, false, 'default'),
    ('pbx-realtime', 'admin', '802', 10, '1234', '9999', true, 'default');

-- Feature codes
INSERT INTO feature_codes (server_id, code, name, description, enabled) VALUES
    ('pbx-realtime', '*72', 'Call Forward Activate', 'Forward all calls to a specified number', true),
    ('pbx-realtime', '*73', 'Call Forward Deactivate', 'Cancel call forwarding', true),
    ('pbx-realtime', '*67', 'Caller ID Block', 'Block caller ID for the next call', true),
    ('pbx-realtime', '*69', 'Last Call Return', 'Return the last received call', true),
    ('pbx-realtime', '*70', 'Call Waiting Toggle', 'Enable or disable call waiting', true),
    ('pbx-realtime', '*78', 'Do Not Disturb', 'Enable do not disturb mode', true);

-- Parking lot
INSERT INTO parking_lot_configs (server_id, name, parking_start_slot, parking_end_slot, parking_timeout, music_on_hold, context) VALUES
    ('pbx-realtime', 'default', 701, 720, 45, 'default', 'parkedcalls');
