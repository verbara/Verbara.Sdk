-- Seed data for queues_config (PbxAdmin queue configuration)
-- Maps to the Asterisk queues defined in queue_table (realtime) and queues.conf (file)

-- pbx-realtime: sales queue
INSERT INTO queues_config (server_id, name, strategy, timeout, retry, wrapuptime, servicelevel, musiconhold, ringinuse, notes)
VALUES ('pbx-realtime', 'sales', 'ringall', 15, 5, 5, 60, 'default', 'no', 'Sales team queue — ring all agents')
ON CONFLICT (server_id, name) DO NOTHING;

-- pbx-realtime: support queue
INSERT INTO queues_config (server_id, name, strategy, timeout, retry, wrapuptime, servicelevel, musiconhold, ringinuse, notes)
VALUES ('pbx-realtime', 'support', 'leastrecent', 20, 5, 10, 90, 'default', 'no', 'Support team queue — least recent agent')
ON CONFLICT (server_id, name) DO NOTHING;

-- pbx-file: sales2 queue
INSERT INTO queues_config (server_id, name, strategy, timeout, retry, wrapuptime, servicelevel, musiconhold, ringinuse, notes)
VALUES ('pbx-file', 'sales2', 'ringall', 15, 5, 5, 60, 'default', 'no', 'Sales team queue (file server)')
ON CONFLICT (server_id, name) DO NOTHING;

-- pbx-file: support2 queue
INSERT INTO queues_config (server_id, name, strategy, timeout, retry, wrapuptime, servicelevel, musiconhold, ringinuse, notes)
VALUES ('pbx-file', 'support2', 'leastrecent', 20, 5, 10, 90, 'default', 'no', 'Support team queue (file server)')
ON CONFLICT (server_id, name) DO NOTHING;

-- Queue members for pbx-realtime sales
INSERT INTO queue_members_config (queue_config_id, interface, membername, penalty)
SELECT id, 'PJSIP/2001', 'Sales Agent 1', 0 FROM queues_config WHERE server_id='pbx-realtime' AND name='sales'
ON CONFLICT (queue_config_id, interface) DO NOTHING;

INSERT INTO queue_members_config (queue_config_id, interface, membername, penalty)
SELECT id, 'PJSIP/2002', 'Sales Agent 2', 0 FROM queues_config WHERE server_id='pbx-realtime' AND name='sales'
ON CONFLICT (queue_config_id, interface) DO NOTHING;

INSERT INTO queue_members_config (queue_config_id, interface, membername, penalty)
SELECT id, 'PJSIP/2003', 'Sales Agent 3', 1 FROM queues_config WHERE server_id='pbx-realtime' AND name='sales'
ON CONFLICT (queue_config_id, interface) DO NOTHING;

-- Queue members for pbx-realtime support
INSERT INTO queue_members_config (queue_config_id, interface, membername, penalty)
SELECT id, 'PJSIP/3001', 'Support Agent 1', 0 FROM queues_config WHERE server_id='pbx-realtime' AND name='support'
ON CONFLICT (queue_config_id, interface) DO NOTHING;

INSERT INTO queue_members_config (queue_config_id, interface, membername, penalty)
SELECT id, 'PJSIP/3002', 'Support Agent 2', 0 FROM queues_config WHERE server_id='pbx-realtime' AND name='support'
ON CONFLICT (queue_config_id, interface) DO NOTHING;

INSERT INTO queue_members_config (queue_config_id, interface, membername, penalty)
SELECT id, 'PJSIP/3003', 'Support Agent 3', 1 FROM queues_config WHERE server_id='pbx-realtime' AND name='support'
ON CONFLICT (queue_config_id, interface) DO NOTHING;

-- Queue members for pbx-file sales2
INSERT INTO queue_members_config (queue_config_id, interface, membername, penalty)
SELECT id, 'PJSIP/4001', 'Sales2 Agent 1', 0 FROM queues_config WHERE server_id='pbx-file' AND name='sales2'
ON CONFLICT (queue_config_id, interface) DO NOTHING;

INSERT INTO queue_members_config (queue_config_id, interface, membername, penalty)
SELECT id, 'PJSIP/4002', 'Sales2 Agent 2', 0 FROM queues_config WHERE server_id='pbx-file' AND name='sales2'
ON CONFLICT (queue_config_id, interface) DO NOTHING;

-- Queue members for pbx-file support2
INSERT INTO queue_members_config (queue_config_id, interface, membername, penalty)
SELECT id, 'PJSIP/5001', 'Support2 Agent 1', 0 FROM queues_config WHERE server_id='pbx-file' AND name='support2'
ON CONFLICT (queue_config_id, interface) DO NOTHING;

INSERT INTO queue_members_config (queue_config_id, interface, membername, penalty)
SELECT id, 'PJSIP/5002', 'Support2 Agent 2', 0 FROM queues_config WHERE server_id='pbx-file' AND name='support2'
ON CONFLICT (queue_config_id, interface) DO NOTHING;
