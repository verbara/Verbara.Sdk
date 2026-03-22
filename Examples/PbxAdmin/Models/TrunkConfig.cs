using System.Globalization;

namespace PbxAdmin.Models;

public sealed class TrunkConfig
{
    public string Name { get; set; } = "";
    public TrunkTechnology Technology { get; set; } = TrunkTechnology.PjSip;
    public string Host { get; set; } = "";
    public int Port { get; set; } = 5060;
    public string Transport { get; set; } = "udp";
    public string Username { get; set; } = "";
    public string Secret { get; set; } = "";
    public string AuthType { get; set; } = "userpass";
    public string Context { get; set; } = "from-trunk";
    public string Codecs { get; set; } = "ulaw,alaw";
    public string DtmfMode { get; set; } = "rfc4733";
    public bool ForceRport { get; set; } = true;
    public bool Comedia { get; set; } = true;
    public string CallerIdNum { get; set; } = "";
    public string CallerIdName { get; set; } = "";
    public int MaxChannels { get; set; }
    public int QualifyFrequency { get; set; } = 60;
    public bool RegistrationEnabled { get; set; } = true;
    public string Notes { get; set; } = "";

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
            ["aors"] = $"{Name}-aor",
            ["auth"] = $"{Name}-auth",
            ["outbound_auth"] = $"{Name}-auth",
        };

        if (ForceRport)
            vars["force_rport"] = "yes";
        if (Comedia)
            vars["rewrite_contact"] = "yes";
        if (!string.IsNullOrEmpty(CallerIdNum))
            vars["from_user"] = CallerIdNum;
        if (!string.IsNullOrEmpty(CallerIdName))
            vars["callerid"] = $"\"{CallerIdName}\" <{CallerIdNum}>";
        if (MaxChannels > 0)
            vars["device_state_busy_at"] = MaxChannels.ToString(CultureInfo.InvariantCulture);

        return vars;
    }

    /// <summary>Generates PJSIP auth section variables.</summary>
    public Dictionary<string, string> ToPjsipAuth()
    {
        return new Dictionary<string, string>
        {
            ["type"] = "auth",
            ["auth_type"] = AuthType,
            ["username"] = Username,
            ["password"] = Secret,
        };
    }

    /// <summary>Generates PJSIP AOR section variables.</summary>
    public Dictionary<string, string> ToPjsipAor()
    {
        var vars = new Dictionary<string, string>
        {
            ["type"] = "aor",
            ["contact"] = $"sip:{Host}:{Port}",
            ["qualify_frequency"] = QualifyFrequency.ToString(CultureInfo.InvariantCulture),
        };
        return vars;
    }

    /// <summary>Generates PJSIP identify section variables for IP-based trunk matching.</summary>
    public Dictionary<string, string> ToPjsipIdentify()
    {
        var vars = new Dictionary<string, string>
        {
            ["type"] = "identify",
            ["endpoint"] = Name ?? "",
            ["match"] = Host ?? ""
        };
        return vars;
    }

    /// <summary>Generates PJSIP registration section variables, or null if disabled.</summary>
    public Dictionary<string, string>? ToPjsipRegistration()
    {
        if (!RegistrationEnabled)
            return null;

        return new Dictionary<string, string>
        {
            ["type"] = "registration",
            ["server_uri"] = $"sip:{Host}:{Port}",
            ["client_uri"] = $"sip:{Username}@{Host}:{Port}",
            ["outbound_auth"] = $"{Name}-auth",
            ["retry_interval"] = "60",
            ["expiration"] = "3600",
        };
    }

    /// <summary>Generates SIP peer section variables.</summary>
    public Dictionary<string, string> ToSipPeer()
    {
        var vars = new Dictionary<string, string>
        {
            ["type"] = "peer",
            ["host"] = Host,
            ["port"] = Port.ToString(CultureInfo.InvariantCulture),
            ["username"] = Username,
            ["secret"] = Secret,
            ["context"] = Context,
            ["disallow"] = "all",
            ["allow"] = Codecs,
            ["dtmfmode"] = DtmfMode,
            ["insecure"] = "port,invite",
            ["qualify"] = "yes",
            ["nat"] = ForceRport ? "force_rport,comedia" : "no",
        };

        if (!string.IsNullOrEmpty(CallerIdNum))
            vars["fromuser"] = CallerIdNum;
        if (MaxChannels > 0)
            vars["call-limit"] = MaxChannels.ToString(CultureInfo.InvariantCulture);
        if (RegistrationEnabled)
            vars["register"] = $"{Username}:{Secret}@{Host}:{Port}/{Username}";

        return vars;
    }

    /// <summary>Generates IAX2 peer section variables.</summary>
    public Dictionary<string, string> ToIaxPeer()
    {
        var vars = new Dictionary<string, string>
        {
            ["type"] = "peer",
            ["host"] = Host,
            ["port"] = Port > 0 ? Port.ToString(CultureInfo.InvariantCulture) : "4569",
            ["username"] = Username,
            ["secret"] = Secret,
            ["context"] = Context,
            ["disallow"] = "all",
            ["allow"] = Codecs,
            ["qualify"] = "yes",
            ["auth"] = "plaintext",
            ["trunk"] = "yes",
        };

        if (MaxChannels > 0)
            vars["maxchannels"] = MaxChannels.ToString(CultureInfo.InvariantCulture);
        if (RegistrationEnabled)
            vars["register"] = $"{Username}:{Secret}@{Host}";

        return vars;
    }

    /// <summary>Creates a TrunkConfig from PJSIP configuration sections.</summary>
    public static TrunkConfig FromPjsipSections(
        string name,
        Dictionary<string, string>? endpoint,
        Dictionary<string, string>? auth,
        Dictionary<string, string>? aor,
        Dictionary<string, string>? registration)
    {
        var config = new TrunkConfig
        {
            Name = name,
            Technology = TrunkTechnology.PjSip,
        };

        if (endpoint is not null)
        {
            config.Context = endpoint.GetValueOrDefault("context", "from-trunk");
            config.Codecs = endpoint.GetValueOrDefault("allow", "ulaw,alaw");
            config.DtmfMode = endpoint.GetValueOrDefault("dtmf_mode", "rfc4733");
            config.ForceRport = endpoint.GetValueOrDefault("force_rport", "no") == "yes";
            config.Comedia = endpoint.GetValueOrDefault("rewrite_contact", "no") == "yes";

            if (endpoint.TryGetValue("from_user", out var fromUser))
                config.CallerIdNum = fromUser;
            if (endpoint.TryGetValue("device_state_busy_at", out var busyAt) && int.TryParse(busyAt, out var max))
                config.MaxChannels = max;

            var transport = endpoint.GetValueOrDefault("transport", "transport-udp");
            config.Transport = transport.StartsWith("transport-", StringComparison.Ordinal)
                ? transport["transport-".Length..]
                : transport;
        }

        if (auth is not null)
        {
            config.Username = auth.GetValueOrDefault("username", "");
            config.Secret = auth.GetValueOrDefault("password", "");
            config.AuthType = auth.GetValueOrDefault("auth_type", "userpass");
        }

        if (aor is not null)
        {
            var contact = aor.GetValueOrDefault("contact", "");
            if (contact.StartsWith("sip:", StringComparison.Ordinal))
            {
                var hostPort = contact["sip:".Length..];
                var colonIdx = hostPort.LastIndexOf(':');
                if (colonIdx > 0)
                {
                    config.Host = hostPort[..colonIdx];
                    if (int.TryParse(hostPort[(colonIdx + 1)..], out var port))
                        config.Port = port;
                }
                else
                {
                    config.Host = hostPort;
                }
            }

            if (aor.TryGetValue("qualify_frequency", out var qf) && int.TryParse(qf, out var freq))
                config.QualifyFrequency = freq;
        }

        config.RegistrationEnabled = registration is not null;

        return config;
    }

    /// <summary>Creates a TrunkConfig from a SIP peer section.</summary>
    public static TrunkConfig FromSipPeer(string name, Dictionary<string, string> section)
    {
        var config = new TrunkConfig
        {
            Name = name,
            Technology = TrunkTechnology.Sip,
            Host = section.GetValueOrDefault("host", ""),
            Username = section.GetValueOrDefault("username", ""),
            Secret = section.GetValueOrDefault("secret", ""),
            Context = section.GetValueOrDefault("context", "from-trunk"),
            Codecs = section.GetValueOrDefault("allow", "ulaw,alaw"),
            DtmfMode = section.GetValueOrDefault("dtmfmode", "rfc2833"),
        };

        if (section.TryGetValue("port", out var port) && int.TryParse(port, out var p))
            config.Port = p;
        if (section.TryGetValue("call-limit", out var limit) && int.TryParse(limit, out var l))
            config.MaxChannels = l;

        var nat = section.GetValueOrDefault("nat", "no");
        config.ForceRport = nat.Contains("force_rport", StringComparison.Ordinal);
        config.Comedia = nat.Contains("comedia", StringComparison.Ordinal);

        if (section.TryGetValue("fromuser", out var fromUser))
            config.CallerIdNum = fromUser;

        config.RegistrationEnabled = section.ContainsKey("register");

        return config;
    }

    /// <summary>Creates a TrunkConfig from an IAX2 peer section.</summary>
    public static TrunkConfig FromIaxPeer(string name, Dictionary<string, string> section)
    {
        var config = new TrunkConfig
        {
            Name = name,
            Technology = TrunkTechnology.Iax2,
            Host = section.GetValueOrDefault("host", ""),
            Username = section.GetValueOrDefault("username", ""),
            Secret = section.GetValueOrDefault("secret", ""),
            Context = section.GetValueOrDefault("context", "from-trunk"),
            Codecs = section.GetValueOrDefault("allow", "ulaw,alaw"),
        };

        if (section.TryGetValue("port", out var port) && int.TryParse(port, out var p))
            config.Port = p;
        else
            config.Port = 4569;

        if (section.TryGetValue("maxchannels", out var maxCh) && int.TryParse(maxCh, out var m))
            config.MaxChannels = m;

        config.RegistrationEnabled = section.ContainsKey("register");

        return config;
    }
}
