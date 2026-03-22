-- 008-extension-templates.sql
-- Extension templates: built-in presets + user-defined templates

CREATE TABLE IF NOT EXISTS extension_templates (
    id          SERIAL PRIMARY KEY,
    name        VARCHAR(100) NOT NULL,
    description VARCHAR(500) DEFAULT '',
    is_built_in BOOLEAN DEFAULT FALSE,
    config_json JSONB NOT NULL,
    created_at  TIMESTAMP DEFAULT NOW()
);

-- Built-in: Standard Employee
INSERT INTO extension_templates (name, description, is_built_in, config_json)
VALUES (
    'Standard Employee',
    'Standard employee extension with voicemail enabled, PJSIP, ulaw+alaw codecs.',
    TRUE,
    '{
        "Extension": "",
        "Name": null,
        "Technology": 0,
        "Password": null,
        "Context": "default",
        "CallGroup": null,
        "PickupGroup": null,
        "Codecs": "ulaw,alaw",
        "DtmfMode": "rfc4733",
        "Transport": "udp",
        "ForceRport": true,
        "DirectMedia": false,
        "VoicemailEnabled": true,
        "VoicemailPin": null,
        "VoicemailEmail": null,
        "VoicemailMaxMessages": 50,
        "DndEnabled": false,
        "CallForwardUnconditional": null,
        "CallForwardBusy": null,
        "CallForwardNoAnswer": null,
        "CallForwardNoAnswerTimeout": 20
    }'::jsonb
);

-- Built-in: Lobby Phone
INSERT INTO extension_templates (name, description, is_built_in, config_json)
VALUES (
    'Lobby Phone',
    'Lobby / reception phone: ulaw only, no voicemail, no call forwarding.',
    TRUE,
    '{
        "Extension": "",
        "Name": null,
        "Technology": 0,
        "Password": null,
        "Context": "default",
        "CallGroup": null,
        "PickupGroup": null,
        "Codecs": "ulaw",
        "DtmfMode": "rfc4733",
        "Transport": "udp",
        "ForceRport": true,
        "DirectMedia": false,
        "VoicemailEnabled": false,
        "VoicemailPin": null,
        "VoicemailEmail": null,
        "VoicemailMaxMessages": 0,
        "DndEnabled": false,
        "CallForwardUnconditional": null,
        "CallForwardBusy": null,
        "CallForwardNoAnswer": null,
        "CallForwardNoAnswerTimeout": 20
    }'::jsonb
);

-- Built-in: Manager
INSERT INTO extension_templates (name, description, is_built_in, config_json)
VALUES (
    'Manager',
    'Manager extension: HD voice (ulaw+alaw+g722), voicemail enabled, all features active.',
    TRUE,
    '{
        "Extension": "",
        "Name": null,
        "Technology": 0,
        "Password": null,
        "Context": "default",
        "CallGroup": "1",
        "PickupGroup": "1",
        "Codecs": "ulaw,alaw,g722",
        "DtmfMode": "rfc4733",
        "Transport": "udp",
        "ForceRport": true,
        "DirectMedia": false,
        "VoicemailEnabled": true,
        "VoicemailPin": null,
        "VoicemailEmail": null,
        "VoicemailMaxMessages": 100,
        "DndEnabled": false,
        "CallForwardUnconditional": null,
        "CallForwardBusy": null,
        "CallForwardNoAnswer": null,
        "CallForwardNoAnswerTimeout": 30
    }'::jsonb
);
