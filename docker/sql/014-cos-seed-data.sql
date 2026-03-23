-- COS Seed Data for pbx-realtime demo server

-- Pattern Groups (6 built-in)
INSERT INTO cos_pattern_groups (server_id, name, description, patterns, country_code, is_built_in) VALUES
    ('pbx-realtime', 'Emergency (Universal)', 'Emergency numbers worldwide', ARRAY['_911','_112','_999'], NULL, true),
    ('pbx-realtime', 'Local (Colombia)', 'Colombia local 7-digit', ARRAY['_NXXXXXX'], 'CO', true),
    ('pbx-realtime', 'National (Colombia)', 'Colombia national 0+10 digits', ARRAY['_0NXXXXXXXXX'], 'CO', true),
    ('pbx-realtime', 'International', 'International dialing universal', ARRAY['_00.','_+.'], NULL, true),
    ('pbx-realtime', 'Premium (Colombia)', 'Colombia premium 900/901', ARRAY['_900XXXXXXX','_901XXXXXXX'], 'CO', true),
    ('pbx-realtime', 'Mobile (Colombia)', 'Colombia mobile 3+9 digits', ARRAY['_3XXXXXXXXX'], 'CO', true);

-- COS Levels (7 built-in) — matching spec section 10.1
INSERT INTO cos_levels (server_id, name, description, priority, asterisk_context, is_built_in, allow_premium, allow_mobile, allow_forward_external, allow_conference_external, allow_recording_control, enabled) VALUES
    ('pbx-realtime', 'Emergency Only', 'Can only dial emergency numbers', 0, 'cos-emergency-only', true, false, false, false, false, false, true),
    ('pbx-realtime', 'Receive Only', 'Can receive calls, dial emergency only', 1, 'cos-receive-only', true, false, false, false, false, false, true),
    ('pbx-realtime', 'Internal Only', 'Internal extensions + feature codes + emergency', 2, 'cos-internal-only', true, false, false, false, false, false, true),
    ('pbx-realtime', 'Local', 'Internal + local calls', 3, 'cos-local', true, false, true, false, false, false, true),
    ('pbx-realtime', 'National', 'Local + national + mobile/premium (gated)', 4, 'cos-national', true, false, true, true, true, false, true),
    ('pbx-realtime', 'International', 'National + international dialing', 5, 'cos-international', true, false, true, true, true, false, true),
    ('pbx-realtime', 'Unrestricted', 'All permissions', 6, 'cos-unrestricted', true, true, true, true, true, true, true);

-- COS Level Rules (linking levels to pattern groups)
-- Emergency Only (level 1): only emergency
INSERT INTO cos_level_rules (cos_level_id, pattern_group_id, action, sequence)
SELECT l.id, g.id, 'ALLOW', 1
FROM cos_levels l, cos_pattern_groups g
WHERE l.name = 'Emergency Only' AND l.server_id = 'pbx-realtime'
  AND g.name = 'Emergency (Universal)' AND g.server_id = 'pbx-realtime';

-- Receive Only (level 2): same as emergency
INSERT INTO cos_level_rules (cos_level_id, pattern_group_id, action, sequence)
SELECT l.id, g.id, 'ALLOW', 1
FROM cos_levels l, cos_pattern_groups g
WHERE l.name = 'Receive Only' AND l.server_id = 'pbx-realtime'
  AND g.name = 'Emergency (Universal)' AND g.server_id = 'pbx-realtime';

-- Internal Only (level 3): emergency
INSERT INTO cos_level_rules (cos_level_id, pattern_group_id, action, sequence)
SELECT l.id, g.id, 'ALLOW', 1
FROM cos_levels l, cos_pattern_groups g
WHERE l.name = 'Internal Only' AND l.server_id = 'pbx-realtime'
  AND g.name = 'Emergency (Universal)' AND g.server_id = 'pbx-realtime';

-- Local (level 4): emergency + local
INSERT INTO cos_level_rules (cos_level_id, pattern_group_id, action, sequence)
SELECT l.id, g.id, 'ALLOW', s.seq
FROM cos_levels l
CROSS JOIN (VALUES
    ('Emergency (Universal)', 1),
    ('Local (Colombia)', 2)
) AS s(gname, seq)
JOIN cos_pattern_groups g ON g.name = s.gname AND g.server_id = 'pbx-realtime'
WHERE l.name = 'Local' AND l.server_id = 'pbx-realtime';

-- National (level 5): emergency + local + national + premium + mobile
INSERT INTO cos_level_rules (cos_level_id, pattern_group_id, action, sequence)
SELECT l.id, g.id, 'ALLOW', s.seq
FROM cos_levels l
CROSS JOIN (VALUES
    ('Emergency (Universal)', 1),
    ('Local (Colombia)', 2),
    ('National (Colombia)', 3),
    ('Premium (Colombia)', 4),
    ('Mobile (Colombia)', 5)
) AS s(gname, seq)
JOIN cos_pattern_groups g ON g.name = s.gname AND g.server_id = 'pbx-realtime'
WHERE l.name = 'National' AND l.server_id = 'pbx-realtime';

-- International (level 6): all
INSERT INTO cos_level_rules (cos_level_id, pattern_group_id, action, sequence)
SELECT l.id, g.id, 'ALLOW', s.seq
FROM cos_levels l
CROSS JOIN (VALUES
    ('Emergency (Universal)', 1),
    ('Local (Colombia)', 2),
    ('National (Colombia)', 3),
    ('Premium (Colombia)', 4),
    ('Mobile (Colombia)', 5),
    ('International', 6)
) AS s(gname, seq)
JOIN cos_pattern_groups g ON g.name = s.gname AND g.server_id = 'pbx-realtime'
WHERE l.name = 'International' AND l.server_id = 'pbx-realtime';

-- Unrestricted (level 7): all
INSERT INTO cos_level_rules (cos_level_id, pattern_group_id, action, sequence)
SELECT l.id, g.id, 'ALLOW', s.seq
FROM cos_levels l
CROSS JOIN (VALUES
    ('Emergency (Universal)', 1),
    ('Local (Colombia)', 2),
    ('National (Colombia)', 3),
    ('Premium (Colombia)', 4),
    ('Mobile (Colombia)', 5),
    ('International', 6)
) AS s(gname, seq)
JOIN cos_pattern_groups g ON g.name = s.gname AND g.server_id = 'pbx-realtime'
WHERE l.name = 'Unrestricted' AND l.server_id = 'pbx-realtime';

-- Extension overrides: Sales=National, Support=Local
INSERT INTO cos_extension_overrides (server_id, extension, cos_level_id)
SELECT 'pbx-realtime', ext, l.id
FROM cos_levels l
CROSS JOIN (VALUES ('2001'), ('2002'), ('2003')) AS e(ext)
WHERE l.name = 'National' AND l.server_id = 'pbx-realtime';

INSERT INTO cos_extension_overrides (server_id, extension, cos_level_id)
SELECT 'pbx-realtime', ext, l.id
FROM cos_levels l
CROSS JOIN (VALUES ('3001'), ('3002'), ('3003')) AS e(ext)
WHERE l.name = 'Local' AND l.server_id = 'pbx-realtime';
