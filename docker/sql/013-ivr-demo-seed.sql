-- ============================================================
-- Spanish IVR Demo: queues, virtual agents, IVR menus, route
-- ============================================================

-- Asterisk native queue_table (so Asterisk sees them via realtime)
INSERT INTO queue_table (name, strategy, timeout, ringinuse, wrapuptime, servicelevel, maxlen, musiconhold) VALUES
  ('ventas-nuevos', 'ringall', 15, 'no', 2, 30, 10, 'default'),
  ('ventas-existentes', 'leastrecent', 15, 'no', 2, 30, 10, 'default'),
  ('soporte-urgente', 'ringall', 15, 'no', 2, 30, 10, 'default'),
  ('soporte-general', 'leastrecent', 15, 'no', 2, 30, 10, 'default'),
  ('facturacion', 'ringall', 15, 'no', 2, 30, 10, 'default'),
  ('rrhh', 'ringall', 15, 'no', 2, 30, 10, 'default');

-- Asterisk native queue_members (virtual agent Local channels)
INSERT INTO queue_members (queue_name, interface, membername, penalty) VALUES
  ('ventas-nuevos', 'Local/maria@virtual-agent', 'María', 0),
  ('ventas-nuevos', 'Local/carlos@virtual-agent', 'Carlos', 0),
  ('ventas-existentes', 'Local/ana@virtual-agent', 'Ana', 0),
  ('ventas-existentes', 'Local/pedro@virtual-agent', 'Pedro', 0),
  ('soporte-urgente', 'Local/carlos@virtual-agent', 'Carlos', 0),
  ('soporte-urgente', 'Local/lucia@virtual-agent', 'Lucía', 0),
  ('soporte-general', 'Local/maria@virtual-agent', 'María', 0),
  ('soporte-general', 'Local/ana@virtual-agent', 'Ana', 0),
  ('facturacion', 'Local/pedro@virtual-agent', 'Pedro', 0),
  ('facturacion', 'Local/lucia@virtual-agent', 'Lucía', 0),
  ('rrhh', 'Local/ana@virtual-agent', 'Ana', 0),
  ('rrhh', 'Local/carlos@virtual-agent', 'Carlos', 0);

-- PbxAdmin queues_config (for PbxAdmin UI management)
-- 6 new queues
INSERT INTO queues_config (server_id, name, strategy, timeout, retry, maxlen, wrapuptime, servicelevel, musiconhold, joinempty, leavewhenempty, ringinuse, enabled, notes)
VALUES
  ('pbx-realtime', 'ventas-nuevos', 'ringall', 15, 5, 10, 2, 30, 'default', 'yes', 'no', 'no', true, 'IVR Demo: Ventas nuevos clientes'),
  ('pbx-realtime', 'ventas-existentes', 'leastrecent', 15, 5, 10, 2, 30, 'default', 'yes', 'no', 'no', true, 'IVR Demo: Ventas clientes existentes'),
  ('pbx-realtime', 'soporte-urgente', 'ringall', 15, 5, 10, 2, 30, 'default', 'yes', 'no', 'no', true, 'IVR Demo: Soporte urgente'),
  ('pbx-realtime', 'soporte-general', 'leastrecent', 15, 5, 10, 2, 30, 'default', 'yes', 'no', 'no', true, 'IVR Demo: Soporte general'),
  ('pbx-realtime', 'facturacion', 'ringall', 15, 5, 10, 2, 30, 'default', 'yes', 'no', 'no', true, 'IVR Demo: Facturación'),
  ('pbx-realtime', 'rrhh', 'ringall', 15, 5, 10, 2, 30, 'default', 'yes', 'no', 'no', true, 'IVR Demo: Recursos Humanos');

-- Queue members (virtual agents)
INSERT INTO queue_members_config (queue_config_id, interface, membername, state_interface, penalty, paused)
SELECT id, 'Local/maria@virtual-agent', 'María', 'Local/maria@virtual-agent', 0, 0 FROM queues_config WHERE name='ventas-nuevos' AND server_id='pbx-realtime';
INSERT INTO queue_members_config (queue_config_id, interface, membername, state_interface, penalty, paused)
SELECT id, 'Local/carlos@virtual-agent', 'Carlos', 'Local/carlos@virtual-agent', 0, 0 FROM queues_config WHERE name='ventas-nuevos' AND server_id='pbx-realtime';

INSERT INTO queue_members_config (queue_config_id, interface, membername, state_interface, penalty, paused)
SELECT id, 'Local/ana@virtual-agent', 'Ana', 'Local/ana@virtual-agent', 0, 0 FROM queues_config WHERE name='ventas-existentes' AND server_id='pbx-realtime';
INSERT INTO queue_members_config (queue_config_id, interface, membername, state_interface, penalty, paused)
SELECT id, 'Local/pedro@virtual-agent', 'Pedro', 'Local/pedro@virtual-agent', 0, 0 FROM queues_config WHERE name='ventas-existentes' AND server_id='pbx-realtime';

INSERT INTO queue_members_config (queue_config_id, interface, membername, state_interface, penalty, paused)
SELECT id, 'Local/carlos@virtual-agent', 'Carlos', 'Local/carlos@virtual-agent', 0, 0 FROM queues_config WHERE name='soporte-urgente' AND server_id='pbx-realtime';
INSERT INTO queue_members_config (queue_config_id, interface, membername, state_interface, penalty, paused)
SELECT id, 'Local/lucia@virtual-agent', 'Lucía', 'Local/lucia@virtual-agent', 0, 0 FROM queues_config WHERE name='soporte-urgente' AND server_id='pbx-realtime';

INSERT INTO queue_members_config (queue_config_id, interface, membername, state_interface, penalty, paused)
SELECT id, 'Local/maria@virtual-agent', 'María', 'Local/maria@virtual-agent', 0, 0 FROM queues_config WHERE name='soporte-general' AND server_id='pbx-realtime';
INSERT INTO queue_members_config (queue_config_id, interface, membername, state_interface, penalty, paused)
SELECT id, 'Local/ana@virtual-agent', 'Ana', 'Local/ana@virtual-agent', 0, 0 FROM queues_config WHERE name='soporte-general' AND server_id='pbx-realtime';

INSERT INTO queue_members_config (queue_config_id, interface, membername, state_interface, penalty, paused)
SELECT id, 'Local/pedro@virtual-agent', 'Pedro', 'Local/pedro@virtual-agent', 0, 0 FROM queues_config WHERE name='facturacion' AND server_id='pbx-realtime';
INSERT INTO queue_members_config (queue_config_id, interface, membername, state_interface, penalty, paused)
SELECT id, 'Local/lucia@virtual-agent', 'Lucía', 'Local/lucia@virtual-agent', 0, 0 FROM queues_config WHERE name='facturacion' AND server_id='pbx-realtime';

INSERT INTO queue_members_config (queue_config_id, interface, membername, state_interface, penalty, paused)
SELECT id, 'Local/ana@virtual-agent', 'Ana', 'Local/ana@virtual-agent', 0, 0 FROM queues_config WHERE name='rrhh' AND server_id='pbx-realtime';
INSERT INTO queue_members_config (queue_config_id, interface, membername, state_interface, penalty, paused)
SELECT id, 'Local/carlos@virtual-agent', 'Carlos', 'Local/carlos@virtual-agent', 0, 0 FROM queues_config WHERE name='rrhh' AND server_id='pbx-realtime';

-- IVR menus
INSERT INTO ivr_menus (server_id, name, label, greeting, timeout, max_retries, timeout_dest_type, timeout_dest, invalid_dest_type, invalid_dest, enabled, notes)
VALUES
  ('pbx-realtime', 'empresa', 'Menú Principal', 'es-custom/ivr-main-greeting', 10, 3, 'hangup', '', 'hangup', '', true, 'IVR Demo: menú principal en español'),
  ('pbx-realtime', 'ventas', 'Sub-menú Ventas', 'es-custom/ivr-ventas', 10, 3, 'ivr', 'empresa', 'ivr', 'empresa', true, 'IVR Demo: sub-menú ventas'),
  ('pbx-realtime', 'soporte', 'Sub-menú Soporte', 'es-custom/ivr-soporte', 10, 3, 'ivr', 'empresa', 'ivr', 'empresa', true, 'IVR Demo: sub-menú soporte'),
  ('pbx-realtime', 'facturacion-menu', 'Sub-menú Facturación', 'es-custom/ivr-facturacion', 10, 3, 'ivr', 'empresa', 'ivr', 'empresa', true, 'IVR Demo: sub-menú facturación');

-- Main menu items (empresa)
INSERT INTO ivr_menu_items (menu_id, digit, label, dest_type, dest_target, trunk)
SELECT id, '1', 'Ventas', 'ivr', 'ventas', NULL FROM ivr_menus WHERE name='empresa' AND server_id='pbx-realtime';
INSERT INTO ivr_menu_items (menu_id, digit, label, dest_type, dest_target, trunk)
SELECT id, '2', 'Soporte Técnico', 'ivr', 'soporte', NULL FROM ivr_menus WHERE name='empresa' AND server_id='pbx-realtime';
INSERT INTO ivr_menu_items (menu_id, digit, label, dest_type, dest_target, trunk)
SELECT id, '3', 'Facturación', 'ivr', 'facturacion-menu', NULL FROM ivr_menus WHERE name='empresa' AND server_id='pbx-realtime';
INSERT INTO ivr_menu_items (menu_id, digit, label, dest_type, dest_target, trunk)
SELECT id, '4', 'Recursos Humanos', 'queue', 'rrhh', NULL FROM ivr_menus WHERE name='empresa' AND server_id='pbx-realtime';
INSERT INTO ivr_menu_items (menu_id, digit, label, dest_type, dest_target, trunk)
SELECT id, '9', 'Repetir menú', 'ivr', 'empresa', NULL FROM ivr_menus WHERE name='empresa' AND server_id='pbx-realtime';

-- Ventas sub-menu items
INSERT INTO ivr_menu_items (menu_id, digit, label, dest_type, dest_target, trunk)
SELECT id, '1', 'Nuevo cliente', 'queue', 'ventas-nuevos', NULL FROM ivr_menus WHERE name='ventas' AND server_id='pbx-realtime';
INSERT INTO ivr_menu_items (menu_id, digit, label, dest_type, dest_target, trunk)
SELECT id, '2', 'Cliente existente', 'queue', 'ventas-existentes', NULL FROM ivr_menus WHERE name='ventas' AND server_id='pbx-realtime';
INSERT INTO ivr_menu_items (menu_id, digit, label, dest_type, dest_target, trunk)
SELECT id, '9', 'Menú principal', 'ivr', 'empresa', NULL FROM ivr_menus WHERE name='ventas' AND server_id='pbx-realtime';

-- Soporte sub-menu items
INSERT INTO ivr_menu_items (menu_id, digit, label, dest_type, dest_target, trunk)
SELECT id, '1', 'Problemas servicio', 'queue', 'soporte-urgente', NULL FROM ivr_menus WHERE name='soporte' AND server_id='pbx-realtime';
INSERT INTO ivr_menu_items (menu_id, digit, label, dest_type, dest_target, trunk)
SELECT id, '2', 'Consultas generales', 'queue', 'soporte-general', NULL FROM ivr_menus WHERE name='soporte' AND server_id='pbx-realtime';
INSERT INTO ivr_menu_items (menu_id, digit, label, dest_type, dest_target, trunk)
SELECT id, '9', 'Menú principal', 'ivr', 'empresa', NULL FROM ivr_menus WHERE name='soporte' AND server_id='pbx-realtime';

-- Facturación sub-menu items
INSERT INTO ivr_menu_items (menu_id, digit, label, dest_type, dest_target, trunk)
SELECT id, '1', 'Consulta de saldo', 'queue', 'facturacion', NULL FROM ivr_menus WHERE name='facturacion-menu' AND server_id='pbx-realtime';
INSERT INTO ivr_menu_items (menu_id, digit, label, dest_type, dest_target, trunk)
SELECT id, '2', 'Pagos', 'queue', 'facturacion', NULL FROM ivr_menus WHERE name='facturacion-menu' AND server_id='pbx-realtime';
INSERT INTO ivr_menu_items (menu_id, digit, label, dest_type, dest_target, trunk)
SELECT id, '9', 'Menú principal', 'ivr', 'empresa', NULL FROM ivr_menus WHERE name='facturacion-menu' AND server_id='pbx-realtime';

-- Inbound route: extension 200 → IVR empresa
INSERT INTO routes_inbound (server_id, name, did_pattern, destination_type, destination, priority, enabled, notes)
VALUES ('pbx-realtime', 'IVR Empresa Español', '200', 'ivr', 'empresa', 5, true, 'Spanish IVR demo entry point');
