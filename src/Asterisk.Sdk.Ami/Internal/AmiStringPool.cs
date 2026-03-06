using System.Text;

namespace Asterisk.Sdk.Ami.Internal;

/// <summary>
/// Static pool of interned strings for AMI protocol keys and common values.
/// Eliminates repeated string allocations for all 941 known AMI field keys and ~35 common values.
///
/// Key lookup uses FNV-1a hash → bucket array (2048 buckets, avg 1.2 entries/bucket, max 5).
/// Value lookup uses length-indexed array with linear scan (35 entries, max 10 per bucket).
/// </summary>
internal static class AmiStringPool
{
    private const int KeyBucketCount = 2048;
    private const int KeyBucketMask = KeyBucketCount - 1;

    private static readonly (byte[] Utf8, string Str)[][] s_keyBuckets = new (byte[], string)[KeyBucketCount][];
    private static readonly (byte[] Utf8, string Str)[]?[] s_values = new (byte[], string)[]?[24];

    static AmiStringPool()
    {
        BuildHashPool(s_keyBuckets,
        [
            // ManagerEvent base
            "Event", "Privilege", "Uniqueid", "Timestamp",
            // Response / Action
            "Response", "ActionID", "Message", "Cause-txt",
            // All 941 unique keys from AMI events (sorted)
            "Abandoned", "Account", "AccountCode", "AccountID", "AccountId", "Accountcode",
            "Accountid", "Acl", "AclName", "ActionId", "Active", "ActiveChannels",
            "Address", "Admin", "Agent", "AgentCalled", "AgentName", "Alarm",
            "Alerting", "Allow", "AmaFlags", "AnswerTime", "AnswerTimeAsDate", "Answeredtime",
            "Aor", "Aors", "AppData", "Application", "Applicationdata", "AsycOperations",
            "AttachMessage", "AttachmentFormat", "AttemptedTransport", "AudioSetting", "AudioState", "Auth",
            "AuthMethod", "AuthType", "Authenticatequalify", "Auths", "AutoComedia", "AutoDeleteSMS",
            "AutoForcerport", "Available", "AverageLag", "AverageLagInMilliSeconds", "AverageRxDataRate", "AverageRxDataRateInBps",
            "AverageTxDataRate", "AverageTxDataRateInBps", "BadLineCount", "BillableSeconds", "BillingID", "Bind",
            "BridgeCreator", "BridgeId", "BridgeName", "BridgeNumChannels", "BridgePreviousVideoSource", "BridgeState",
            "BridgeTechnology", "BridgeType", "BridgeUniqueid", "BridgeVideoSourceMode", "BridgedChannel", "Bridgeduniqueid",
            "BridgedUniqueId", "Bridgeid", "Bridgevideosourcemode", "Buddy", "BuddySkypename", "BuddyStatus",
            "CaListFile", "CaListPath", "Callback", "CallDuration", "CallGroup", "CallID",
            "CallWaitingSetting", "CallWaitingState", "CallerId", "CallerId1", "CallerId2", "CallerIDName",
            "CallerIDNum", "CallerIDani", "CallerIDdnid", "CallerIDrdnis", "CallerIdName", "CallerIdNum",
            "Callerid", "CalleridPrivacy", "CalleridTag", "Callers", "Callid", "Callidx",
            "CallOperator", "Calls", "CallsChannels", "CallsTaken", "Callstaken", "CanReview",
            "Cause", "CauseTxt", "Cccause", "CellID", "CertFile", "CfrCount",
            "Challange", "Challenge", "Channel", "Channel1", "Channel2", "ChannelCalling",
            "ChannelDriver", "ChannelLanguage", "ChannelState", "ChannelStateDesc", "ChannelType", "Channels",
            "ChanObjectType", "Charge", "CidCallingPres", "CidCallingPresTxt", "Cipher", "ClassName",
            "ClientUri", "Clone", "CloneState", "CloneStateDesc", "Comedia", "Command",
            "CommandId", "CommandsInQueue", "Completed", "CompletedFAXes", "Conference", "ConnectedLineMethod",
            "ConnectedLineName", "ConnectedLineNum", "ContactAcl", "ContactStatus", "Contacts", "ContactsRegistered",
            "Context", "Cos", "CosAudio", "CosVideo", "Counter", "CumulativeLoss",
            "Currency", "CurrencyAmount", "CurrencyName", "CurrentDeviceState", "CurrentSessions", "DChannel",
            "Dahdichannel", "Dahdigroup", "Dahdispan", "Data", "DataRate", "DataSetting",
            "DataState", "DcnCount", "DecodedMessage", "DefaultCallingPres", "DefaultExpiration", "DeleteMessage",
            "Description", "DesiredDeviceState", "DestAccountCode", "DestApp", "DestBridgeUniqueid", "DestCallerIdName",
            "DestCallerIdNum", "DestChannel", "DestChannelState", "DestChannelStateDesc", "DestConnectedLineName", "DestConnectedLineNum",
            "DestContext", "DestExten", "DestLanguage", "DestLinkedId", "DestLinkedid", "DestPriority",
            "DestTransfererChannel", "DestType", "DestUniqueId", "DestUniqueid", "Destination", "DestinationChannel",
            "DestinationContext", "Device", "DeviceState", "DeviceStateBusyAt", "Devicestate", "DialStatus",
            "DialString", "Dialing", "Dialout", "Digit", "Direction", "DirectMediaGlareMitigation",
            "DirectMediaMethod", "DisableSMS", "DisDcsDtcCtcCount", "Disposition", "Dnd", "Dnid",
            "DocumentNumber", "DocumentTime", "Domain", "DtlsAutoGenerateCert", "DtlsCaFile", "DtlsCaPath",
            "DtlsCertFile", "DtlsCipher", "DtlsFingerprint", "DtlsPrivateKey", "DtlsRekey", "DtlsSetup",
            "DtlsVerify", "DTMF", "DtmfMode", "Duration", "DurationMs", "Dynamic",
            "EcmMode", "EffectiveConnectedLineName", "EffectiveConnectedLineNum", "Email", "Enabled", "Encryption",
            "Endmarked", "Endpoint", "EndpointName", "Endstatus", "EndTime", "EndTimeAsDate",
            "Env", "Error", "Eventlist", "EventList", "EventName", "EventTV",
            "EventTime", "EventVersion", "Eventtv", "Eventversion", "ExitContext", "ExpectedAddress",
            "ExpectedResponse", "Expirationtime", "Expires", "Exten", "Extension", "ExtensionLabel",
            "ExternalMediaAddress", "ExternalSignalingAddress", "ExternalSignalingPort", "Extra", "FailedFAXes", "Family",
            "FaxDetectTimeout", "File", "FileName", "Filename", "Files", "Firmware",
            "Flag", "Folder", "ForceRport", "Forward", "From", "FromBridgeCreator",
            "FromBridgeName", "FromBridgeNumChannels", "FromBridgeTechnology", "FromBridgeType", "FromBridgeUniqueId", "FromDomain",
            "FromPort", "FromUser", "FttCount", "Fullname", "Group", "GSMRegistrationStatus",
            "GtalkSid", "Handler", "Header", "Held", "HighestSequence", "Hint",
            "HoldTime", "Host", "HostId", "ID", "IMEISetting", "IMEIState",
            "IMSISetting", "Iax2CallNoLocal", "Iax2CallNoRemote", "Iax2Peer", "Id", "IdentifyBy",
            "Ignore183withoutsdp", "IgnorePattern", "ImageEncoding", "ImageResolution", "ImapUser", "InCall",
            "Incall", "IncludeContext", "Incoming", "IncomingMwiMailbox", "Initializing", "Interface",
            "IpAddress", "IpPort", "Isexternal", "Items", "JitterBufferOverflows", "Key",
            "Language", "LastApplication", "LastCall", "LastData", "LastError", "LastPageProcessed",
            "LastPause", "LastSr", "Lastcall", "Lastpause", "Lastreload", "Linecount",
            "Link", "LinkedID", "LinkedId", "Linkedid", "ListContexts", "ListExtensions",
            "ListItems", "ListName", "ListPriorities", "Listitems", "LocalAddress", "LocalDis",
            "LocalDropped", "LocalJbDelay", "LocalJitter", "LocalLossPercent", "LocalNet", "LocalOneAccountCode",
            "LocalOneCallerIDName", "LocalOneCallerIDNum", "LocalOneCalleridName", "LocalOneCalleridNum", "LocalOneChannel", "LocalOneChannelState",
            "LocalOneChannelStateDesc", "LocalOneConnectedLineName", "LocalOneConnectedLineNum", "LocalOneContext", "LocalOneExten", "LocalOneLanguage",
            "LocalOneLinkedid", "LocalOnePriority", "LocalOneUniqueid", "LocalOneUniqueId", "LocalReceived", "LocalSid",
            "LocalStationID", "LocalStationId", "LocalTotalLost", "LocalTwoAccountCode", "LocalTwoCallerIDName", "LocalTwoCallerIDNum",
            "LocalTwoCalleridName", "LocalTwoCalleridNum", "LocalTwoChannel", "LocalTwoChannelState", "LocalTwoChannelStateDesc", "LocalTwoConnectedLineName",
            "LocalTwoConnectedLineNum", "LocalTwoContext", "LocalTwoExten", "LocalTwoLanguage", "LocalTwoLinkedid", "LocalTwoPriority",
            "LocalTwoUniqueid", "LocalTwochannelState", "Localaddress", "Localooo", "LocalOptimization", "Location",
            "LocationAreaCode", "Locked", "LoggedIn", "LoggedInChan", "LoggedInTime", "LoginChan",
            "LoginTime", "Logintime", "LongestHoldTime", "Mailbox", "MailCommand", "Mailboxes",
            "Manufacturer", "Marked", "MarkedUser", "Match", "MatchHeader", "Max",
            "MaxAudioStreams", "MaxContacts", "MaxLag", "MaxLagInMilliSeconds", "MaxMessageCount", "MaxMessageLength",
            "MaxVideoStreams", "MaximumExpiration", "MCallerIDNum", "MCallerIDNumPlan", "MCallerIDNumPres", "MCallerIDNumValid",
            "MCallerIDton", "McfCount", "Md5Cred", "MediaAddress", "MeetMe", "Meetme",
            "MemberName", "Membername", "Membership", "Mes", "MessageContext", "Messageline0",
            "Method", "MinimalDTMFDuration", "MinimalDTMFGap", "MinimalDTMFInterval", "MinimumExpiration", "MinimumJitterSpace",
            "Mode", "Model", "Module", "ModuleCount", "ModuleLoadStatus", "ModuleSelection",
            "MohSuggest", "MusicClass", "Muted", "Mutex", "MwiFromUser", "MwiSubscribeReplacesUnsolicited",
            "Name", "NamedCallGroup", "NamedPickupGroup", "NativeFormats", "NatSupport", "New",
            "NewMessageCount", "NewMessages", "NewPassword", "Newstate", "NonceLifetime", "ObjectName",
            "ObjectType", "ObjectUserName", "Objectname", "Objecttype", "Old", "OldAccountCode",
            "OldMessageCount", "OldMessages", "OperatingMode", "Operation", "OrigBridgeCreator", "OrigBridgeName",
            "OrigBridgeNumChannels", "OrigBridgeTechnology", "OrigBridgeType", "OrigBridgeUniqueid", "OrigBridgeVideoSourceMode", "Original",
            "OriginalPosition", "OriginalState", "OriginalStateDesc", "Origtime",
            "OrigTransfererAccountCode", "OrigTransfererCallerIDName", "OrigTransfererCallerIDNum", "OrigTransfererChannel",
            "OrigTransfererChannelState", "OrigTransfererChannelStateDesc", "OrigTransfererConnectedLineName", "OrigTransfererConnectedLineNum",
            "OrigTransfererContext", "OrigTransfererExten", "OrigTransfererLanguage", "OrigTransfererLinkedId",
            "OrigTransfererPriority", "OrigTransfererUniqueid",
            "OurSsrc", "OutboundAuth", "OutboundAuths", "OutboundProxy", "Outboundproxy", "Owner",
            "Packet", "PacketsLost", "PageCount", "PageNumber", "PageSize", "Pager",
            "PagesTransferred", "ParkeeChannel", "ParkeeChannelState", "Parkeelinkedid", "ParkerChannel", "ParkingLot",
            "ParkingSpace", "ParkingTimeout", "Parties", "Password", "Path", "Paused",
            "PausedReason", "Pausedreason", "PbjectName", "Peer", "PeerAccount", "PeerCount",
            "PeerName", "PeerStatus", "Penalty", "PickupGroup", "Ping", "Port",
            "Ports", "Position", "PprCount", "Presentity", "PriEvent", "PriEventCode",
            "Priority", "PrivKeyFile", "ProcessedStatus", "Product", "Protocol", "ProtocolIdentifier",
            "ProviderName", "Pruneonboot", "Pt", "QualifyFrequency", "QualifyTimeout", "Qualifyfrequency",
            "Queue", "RSSI", "ReadFormat", "Readtrans", "Realm", "RealtimeDevice",
            "Reason", "ReasonTxt", "ReceiveAttempts", "ReceivedChallenge", "ReceivedHash", "ReceivedPackets",
            "ReceptionReports", "Recordfile", "RecordOffFeature", "RecordOnFeature", "Refresh", "RegExpire",
            "Registrar", "RegistrationTime", "RegistryCount", "Regserver", "Rel100", "Releasing",
            "ReloadReason", "ReloadReasonCode", "ReloadReasonDescription", "RemoteAddress", "RemoteDis", "RemoteDropped",
            "RemoteJbDelay", "RemoteJitter", "RemoteLossPercent", "RemoteReceived", "RemoteSid", "RemoteStationID",
            "RemoteStationId", "RemoteTotalLost", "Remoteaddress", "Remoteooo",
            "Report0CumulativeLost", "Report0FractionLost", "Report0HighestSequence", "Report0SequenceNumberCycles",
            "Report0Sourcessrc", "Report0dlsr", "Report0iaJitter", "Report0lsr", "ReportCount",
            "RequestType", "Requestparams", "Requesttype", "RequireClientCert", "ReservedSessions", "ResetDongle",
            "Resolution", "Restart", "Result", "ResultCode", "RetransmitCount",
            "RetrieverAccountCode", "RetrieverCallerIDName", "RetrieverCallerIDNum", "RetrieverChannel",
            "RetrieverChannelState", "RetrieverChannelStateDesc", "RetrieverConnectedLineName", "RetrieverConnectedLineNum",
            "RetrieverContext", "RetrieverExten", "RetrieverPriority", "RetrieverUniqueid",
            "Ringinuse", "Ringtime", "RoundtripUsec", "Roundtripusec", "RrCount", "RtnCount",
            "RtpEngine", "RtpKeepalive", "RtpTimeout", "RtpTimeoutHold", "Rtt", "RttAsMillseconds",
            "RxBytes", "RXGain", "RxPages", "SayCid", "SayDurationMinimum", "SayEnvelope",
            "SdpOwner", "SdpSession",
            "SecondBridgeCreator", "SecondBridgeName", "SecondBridgeNumChannels", "SecondBridgeTechnology",
            "SecondBridgeType", "SecondBridgeUniqueid", "SecondBridgeVideoSourceMode", "Seconds",
            "SecondTransfererAccountCode", "SecondTransfererCallerIDName", "SecondTransfererCallerIDNum", "SecondTransfererChannel",
            "SecondTransfererChannelState", "SecondTransfererChannelStateDesc", "SecondTransfererConnectedLineName", "SecondTransfererConnectedLineNum",
            "SecondTransfererContext", "SecondTransfererExten", "SecondTransfererLanguage", "SecondTransfererLinkedId",
            "SecondTransfererPriority", "SecondTransfererUniqueid",
            "SenderSsrc", "SentNtp", "SentOctets", "SentPackets", "SentRtp", "Sentntp", "Sentrtp",
            "SequenceNumberCycles", "ServerEmail", "ServerUri", "Service", "ServiceLevel", "ServiceLevelPerf",
            "ServiceLevelPerf2", "SessionID", "SessionId", "SessionNumber", "SessionTV", "Sessionid",
            "Sessiontv", "SetVar", "Severity", "Shutdown", "Signal", "Signalling",
            "Signallingcode", "SipCallId", "SipFullContact", "SMS", "SMSPDU", "SMSServiceCenter",
            "SourceAccountCode", "SourceCallerIDName", "SourceCallerIDNum", "SourceChannel", "SourceChannelState", "SourceChannelStateDesc",
            "SourceConnectedLineName", "SourceConnectedLineNum", "SourceContext", "SourceExten", "SourceLanguage", "SourceLinkedid",
            "SourcePriority", "SourceUniqueid", "Span",
            "SpyeeAccountCode", "SpyeeCallerIdName", "SpyeeCallerIdNum", "SpyeeChannel", "SpyeeChannelState", "SpyeeChannelStateDesc",
            "SpyeeConnectedLineName", "SpyeeConnectedLineNum", "SpyeeContext", "SpyeeExten", "SpyeeLanguage", "SpyeeLinkedId",
            "SpyeePriority", "SpyeeUniqueId",
            "SpyerAccountCode", "SpyerCallerIdName", "SpyerCallerIdNum", "SpyerChannel", "SpyerChannelState", "SpyerChannelStateDesc",
            "SpyerConnectedLineName", "SpyerConnectedLineNum", "SpyerContext", "SpyerExten", "SpyerLanguage", "SpyerLinkedId",
            "SpyerPriority", "SpyerUniqueId",
            "Src", "SrCount", "SrcUniqueId", "SrtpTag32", "SrvLookups", "Ssrc",
            "StartPage", "StartTime", "StartTimeAsDate", "State", "StateInterface", "Stateinterface",
            "Status", "Statustext", "Strategy", "SubEvent", "SubMinExpiry", "Submode",
            "SubscribeContext", "SubscriberNumber", "Subtype", "Success", "Swapuniqueid", "Switch",
            "T38OctetsReceived", "T38OctetsSent", "T38PacketsReceived", "T38PacketsSent", "T38SessionDuration",
            "T38SessionDurationInSeconds", "T38UdptlEc", "T38UdptlMaxdatagram",
            "Talking", "TalkingStatus", "TalkingTo", "TalkingToChan", "TalkTime",
            "TargetChannel", "Targetaccountcode", "Targetcalleridname", "Targetcalleridnum", "Targetchannel",
            "Targetchannelstate", "Targetchannelstatedesc", "Targetconnectedlinename", "Targetconnectedlinenum",
            "Targetcontext", "Targetexten", "Targetlanguage", "Targetlinkedid", "Targetpriority",
            "TargetUniqueId", "Targetuniqueid",
            "TasksInQueue", "TechCause", "Technology", "TextSupport", "TheirLastSr", "Time",
            "Timeout", "Timers", "TimersSessExpires", "TimersMinSe", "TimeToHangup", "Timezone",
            "To", "ToBridgeCreator", "ToBridgeName", "ToBridgeNumChannels", "ToBridgeTechnology",
            "ToBridgeType", "ToBridgeUniqueId", "ToneZone", "ToPort", "Tos", "TosAudio",
            "TosVideo", "TotalBadLines", "TotalContacts", "TotalEvents", "TotalLag", "TotalLagInMilliSeconds",
            "TotalRxLines", "TotalTxLines", "TotalType", "Transfer2Parking", "TransferContext", "TransferDuration",
            "TransferExten", "TransferMethod", "TransferPels", "TransferRate", "TransferType",
            "TransfereeAccountCode", "TransfereeCallerIDName", "TransfereeCallerIDNum", "TransfereeCallerIdName", "TransfereeCallerIdNum",
            "TransfereeChannel", "TransfereeChannelState", "TransfereeChannelStateDesc",
            "TransfereeConnectedLineName", "TransfereeConnectedLineNum", "TransfereeContext", "TransfereeExten",
            "TransfereeLanguage", "TransfereeLinkedId", "TransfereePriority", "TransfereeUniqueId", "TransfereeUniqueid",
            "Transfereeaccountcode",
            "TransfererAccountCode", "TransfererCallerIdName", "TransfererCallerIdNum", "TransfererChannel",
            "TransfererChannelState", "TransfererChannelStateDesc", "TransfererConnectedLineName", "TransfererConnectedLineNum",
            "TransfererContext", "TransfererExten", "TransfererLanguage", "TransfererLinkedId",
            "TransfererPriority", "TransfererUniqueId",
            "TransferTargetAccountCode", "TransferTargetCallerIDName", "TransferTargetCallerIDNum", "TransferTargetChannel",
            "TransferTargetChannelState", "TransferTargetChannelStateDesc", "TransferTargetConnectedLineName", "TransferTargetConnectedLineNum",
            "TransferTargetContext", "TransferTargetExten", "TransferTargetLanguage", "TransferTargetLinkedID",
            "TransferTargetPriority", "TransferTargetUniqueID",
            "Transit", "TransmitAttempts", "Transport", "Trunk", "Trunkname", "TxBytes",
            "TXGain", "TxPages", "Type", "U2DIAG", "UniqueID", "UniqueId1",
            "UniqueId2", "UnrecoverablePackets", "Uptime", "Uri", "UseCallingPres", "UseUCS2Encoding",
            "User", "UserAgent", "UserCount", "UserEvent", "UserField", "Useragent",
            "Username", "Usernum", "UsingPassword", "USSDUse7BitEncoding", "USSDUseUCS2Decoding",
            "Val", "Value", "Variable", "VerifyClient", "VerifyServer", "ViaAddress",
            "Viaaddr", "Viaport", "VideoSupport", "VmContext", "Voice", "Voicemailbox",
            "VoicemailExtension", "VolumeGain", "Wait", "Waiting", "Waitmarked", "WebsocketWriteTimeout",
            "Weight", "WrapupTime", "Wrapuptime", "WriteFormat", "Writetrans",
        ]);

        BuildLengthPool(s_values,
        [
            // Single digits (ChannelState, Priority, etc.)
            "0", "1", "2", "3", "4", "5", "6", "7", "8", "9",
            // Channel state descriptions
            "Down", "Rsrvd", "OffHook", "Dialing", "Ring", "Ringing", "Up", "Busy",
            // Response statuses
            "Success", "Error", "Follows", "Goodbye",
            // Boolean-like
            "true", "false", "yes", "no",
            // Common short values
            "en", "default",
            // Privilege values
            "call,all", "agent,all", "system,all", "command,all",
            "reporting,all", "security,all",
        ]);
    }

    /// <summary>
    /// Returns an interned string for known AMI keys, or allocates a new string for unknown keys.
    /// Uses FNV-1a hash for O(1) amortized lookup across 941 known keys.
    /// </summary>
    public static string GetKey(ReadOnlySpan<byte> utf8)
    {
        var hash = Fnv1aHash(utf8);
        var bucket = s_keyBuckets[hash & KeyBucketMask];
        if (bucket is not null)
        {
            foreach (var (poolUtf8, str) in bucket)
            {
                if (utf8.SequenceEqual(poolUtf8))
                    return str;
            }
        }

        return Encoding.UTF8.GetString(utf8);
    }

    /// <summary>
    /// Returns an interned string for common AMI values, or allocates a new string for uncommon values.
    /// </summary>
    public static string GetValue(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length == 0)
            return string.Empty;

        if ((uint)utf8.Length < (uint)s_values.Length)
        {
            var group = s_values[utf8.Length];
            if (group is not null)
            {
                foreach (var (poolUtf8, str) in group)
                {
                    if (utf8.SequenceEqual(poolUtf8))
                        return str;
                }
            }
        }

        return Encoding.UTF8.GetString(utf8);
    }

    /// <summary>
    /// FNV-1a hash for byte spans. Fast, good distribution, no allocations.
    /// </summary>
    private static uint Fnv1aHash(ReadOnlySpan<byte> bytes)
    {
        var hash = 2166136261u;
        foreach (var b in bytes)
        {
            hash ^= b;
            hash *= 16777619u;
        }

        return hash;
    }

    private static void BuildHashPool((byte[], string)[][] pool, string[] entries)
    {
        var buckets = new Dictionary<int, List<(byte[], string)>>();
        var seen = new HashSet<string>();

        foreach (var entry in entries)
        {
            if (!seen.Add(entry)) continue; // skip duplicates

            var utf8 = Encoding.UTF8.GetBytes(entry);
            var hash = Fnv1aHashArray(utf8);
            var idx = (int)(hash & KeyBucketMask);

            if (!buckets.TryGetValue(idx, out var list))
            {
                list = [];
                buckets[idx] = list;
            }

            list.Add((utf8, entry));
        }

        foreach (var (idx, list) in buckets)
        {
            pool[idx] = [.. list];
        }
    }

    private static void BuildLengthPool((byte[], string)[]?[] pool, string[] entries)
    {
        var groups = new Dictionary<int, List<(byte[], string)>>();
        foreach (var entry in entries)
        {
            if (entry.Length >= pool.Length) continue;
            if (!groups.TryGetValue(entry.Length, out var list))
            {
                list = [];
                groups[entry.Length] = list;
            }

            list.Add((Encoding.UTF8.GetBytes(entry), entry));
        }

        foreach (var (length, list) in groups)
        {
            pool[length] = [.. list];
        }
    }

    /// <summary>
    /// FNV-1a hash for byte arrays (used during pool construction only).
    /// </summary>
    private static uint Fnv1aHashArray(byte[] bytes)
    {
        var hash = 2166136261u;
        foreach (var b in bytes)
        {
            hash ^= b;
            hash *= 16777619u;
        }

        return hash;
    }
}
