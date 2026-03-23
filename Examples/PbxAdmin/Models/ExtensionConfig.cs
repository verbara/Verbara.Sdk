using System.Globalization;

namespace PbxAdmin.Models;

public sealed class ExtensionConfig
{
    private const string DefaultContext = "default";

    // Identity
    public string Extension { get; set; } = "";
    public string? Name { get; set; }
    public ExtensionTechnology Technology { get; set; } = ExtensionTechnology.PjSip;

    // Auth
    public string? Password { get; set; }

    // Dialplan
    public string Context { get; set; } = DefaultContext;
    public string? CallGroup { get; set; }
    public string? PickupGroup { get; set; }

    // Media
    public string Codecs { get; set; } = "ulaw,alaw";
    public string DtmfMode { get; set; } = "rfc4733";

    // NAT/Transport
    public string Transport { get; set; } = "udp";
    public bool ForceRport { get; set; } = true;
    public bool DirectMedia { get; set; }

    // Voicemail
    public bool VoicemailEnabled { get; set; }
    public string? VoicemailPin { get; set; }
    public string? VoicemailEmail { get; set; }
    public int VoicemailMaxMessages { get; set; } = 50;

    // Features (AstDB)
    public bool DndEnabled { get; set; }
    public string? CallForwardUnconditional { get; set; }
    public string? CallForwardBusy { get; set; }
    public string? CallForwardNoAnswer { get; set; }
    public int CallForwardNoAnswerTimeout { get; set; } = 20;

    /// <summary>Generates PJSIP endpoint section variables.</summary>
    public Dictionary<string, string> ToPjsipEndpoint()
    {
        var vars = new Dictionary<string, string>
        {
            ["type"] = "endpoint",
            ["transport"] = $"transport-{Transport}",
            ["context"] = Context,
            ["disallow"] = "all",
            ["allow"] = Codecs,
            ["dtmf_mode"] = DtmfMode,
            ["aors"] = $"{Extension}-aor",
            ["auth"] = $"{Extension}-auth",
            ["direct_media"] = DirectMedia ? "yes" : "no",
        };

        if (!string.IsNullOrEmpty(Name))
            vars["callerid"] = $"\"{Name}\" <{Extension}>";

        if (ForceRport)
            vars["force_rport"] = "yes";

        if (!string.IsNullOrEmpty(CallGroup))
            vars["call_group"] = CallGroup;
        if (!string.IsNullOrEmpty(PickupGroup))
            vars["pickup_group"] = PickupGroup;

        return vars;
    }

    /// <summary>Generates PJSIP auth section variables.</summary>
    public Dictionary<string, string> ToPjsipAuth()
    {
        return new Dictionary<string, string>
        {
            ["type"] = "auth",
            ["auth_type"] = "userpass",
            ["username"] = Extension,
            ["password"] = Password ?? "",
        };
    }

    /// <summary>Generates PJSIP AOR section variables.</summary>
    public Dictionary<string, string> ToPjsipAor()
    {
        return new Dictionary<string, string>
        {
            ["type"] = "aor",
            ["max_contacts"] = "1",
            ["qualify_frequency"] = "60",
            ["remove_existing"] = "yes",
        };
    }

    /// <summary>Generates SIP peer section variables.</summary>
    public Dictionary<string, string> ToSipPeer()
    {
        var vars = new Dictionary<string, string>
        {
            ["type"] = "friend",
            ["host"] = "dynamic",
            ["secret"] = Password ?? "",
            ["context"] = Context,
            ["disallow"] = "all",
            ["allow"] = Codecs,
            ["dtmfmode"] = DtmfMode,
            ["qualify"] = "yes",
            ["nat"] = ForceRport ? "force_rport,comedia" : "no",
            ["directmedia"] = DirectMedia ? "yes" : "no",
        };

        if (!string.IsNullOrEmpty(Name))
            vars["callerid"] = $"\"{Name}\" <{Extension}>";

        if (!string.IsNullOrEmpty(CallGroup))
            vars["call-group"] = CallGroup;
        if (!string.IsNullOrEmpty(PickupGroup))
            vars["pickup-group"] = PickupGroup;

        return vars;
    }

    /// <summary>Generates IAX2 peer section variables.</summary>
    public Dictionary<string, string> ToIaxPeer()
    {
        var vars = new Dictionary<string, string>
        {
            ["type"] = "friend",
            ["host"] = "dynamic",
            ["secret"] = Password ?? "",
            ["context"] = Context,
            ["disallow"] = "all",
            ["allow"] = Codecs,
            ["qualify"] = "yes",
            ["auth"] = "plaintext",
        };

        if (!string.IsNullOrEmpty(Name))
            vars["callerid"] = $"\"{Name}\" <{Extension}>";

        return vars;
    }

    /// <summary>Creates an ExtensionConfig from PJSIP configuration sections.</summary>
    public static ExtensionConfig FromPjsipSections(
        string name,
        Dictionary<string, string>? endpoint,
        Dictionary<string, string>? auth,
        Dictionary<string, string>? aor)
    {
        var config = new ExtensionConfig
        {
            Extension = name,
            Technology = ExtensionTechnology.PjSip,
        };

        if (endpoint is not null)
        {
            config.Context = endpoint.GetValueOrDefault("context", DefaultContext);
            config.Codecs = endpoint.GetValueOrDefault("allow", "ulaw,alaw");
            config.DtmfMode = endpoint.GetValueOrDefault("dtmf_mode", "rfc4733");
            config.ForceRport = endpoint.GetValueOrDefault("force_rport", "no") == "yes";
            config.DirectMedia = endpoint.GetValueOrDefault("direct_media", "no") == "yes";

            if (endpoint.TryGetValue("callerid", out var callerid))
                config.Name = ParseCallerIdName(callerid);

            if (endpoint.TryGetValue("call_group", out var callGroup))
                config.CallGroup = callGroup;
            if (endpoint.TryGetValue("pickup_group", out var pickupGroup))
                config.PickupGroup = pickupGroup;

            var transport = endpoint.GetValueOrDefault("transport", "transport-udp");
            config.Transport = transport.StartsWith("transport-", StringComparison.Ordinal)
                ? transport["transport-".Length..]
                : transport;
        }

        if (auth is not null)
        {
            config.Password = auth.GetValueOrDefault("password", "");
        }

        return config;
    }

    /// <summary>Creates an ExtensionConfig from a SIP peer section.</summary>
    public static ExtensionConfig FromSipPeer(string name, Dictionary<string, string> section)
    {
        var config = new ExtensionConfig
        {
            Extension = name,
            Technology = ExtensionTechnology.Sip,
            Password = section.GetValueOrDefault("secret", ""),
            Context = section.GetValueOrDefault("context", DefaultContext),
            Codecs = section.GetValueOrDefault("allow", "ulaw,alaw"),
            DtmfMode = section.GetValueOrDefault("dtmfmode", "rfc4733"),
        };

        var nat = section.GetValueOrDefault("nat", "no");
        config.ForceRport = nat.Contains("force_rport", StringComparison.Ordinal);
        config.DirectMedia = section.GetValueOrDefault("directmedia", "yes") == "yes";

        if (section.TryGetValue("callerid", out var callerid))
            config.Name = ParseCallerIdName(callerid);

        if (section.TryGetValue("call-group", out var callGroup))
            config.CallGroup = callGroup;
        if (section.TryGetValue("pickup-group", out var pickupGroup))
            config.PickupGroup = pickupGroup;

        return config;
    }

    /// <summary>Creates an ExtensionConfig from an IAX2 peer section.</summary>
    public static ExtensionConfig FromIaxPeer(string name, Dictionary<string, string> section)
    {
        var config = new ExtensionConfig
        {
            Extension = name,
            Technology = ExtensionTechnology.Iax2,
            Password = section.GetValueOrDefault("secret", ""),
            Context = section.GetValueOrDefault("context", DefaultContext),
            Codecs = section.GetValueOrDefault("allow", "ulaw,alaw"),
        };

        if (section.TryGetValue("callerid", out var callerid))
            config.Name = ParseCallerIdName(callerid);

        return config;
    }

    /// <summary>Extracts the display name from a callerid string like "Name" &lt;ext&gt;.</summary>
    private static string? ParseCallerIdName(string callerid)
    {
        var startQuote = callerid.IndexOf('"');
        if (startQuote < 0)
            return null;
        var endQuote = callerid.IndexOf('"', startQuote + 1);
        if (endQuote < 0)
            return null;
        return callerid[(startQuote + 1)..endQuote];
    }
}
