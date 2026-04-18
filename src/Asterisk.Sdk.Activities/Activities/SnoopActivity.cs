using Asterisk.Sdk;

namespace Asterisk.Sdk.Activities.Activities;

/// <summary>Direction of ARI snoop — which audio legs of the target channel the snooper sees.</summary>
public enum SnoopDirection
{
    /// <summary>No audio on this leg.</summary>
    None = 0,
    /// <summary>Audio inbound to target (what target hears).</summary>
    In = 1,
    /// <summary>Audio outbound from target (what target says).</summary>
    Out = 2,
    /// <summary>Both directions.</summary>
    Both = 3,
}

/// <summary>
/// Create an ARI snoop channel against an active target channel for supervisor listening,
/// whispering, or both. Wraps <c>IAriClient.Channels.SnoopAsync</c> and surfaces the resulting
/// snoop channel via <see cref="SnoopChannel"/>. Requires a Stasis application name; the
/// snoop channel enters that Stasis app and the caller can then play audio into it
/// (whisper) or record from it (spy).
/// </summary>
/// <remarks>
/// For AGI-side equivalents see <see cref="ChanSpyActivity"/> (silent spy / whisper) and
/// <see cref="BargeActivity"/> (audible three-way).
/// </remarks>
public sealed class SnoopActivity(IAriClient ariClient) : AriActivityBase(ariClient)
{
    /// <summary>Target channel ID to snoop on.</summary>
    public required string TargetChannelId { get; init; }

    /// <summary>Stasis application the snoop channel enters.</summary>
    public required string App { get; init; }

    /// <summary>Direction of the spy leg (what the snooper hears from the target).</summary>
    public SnoopDirection Spy { get; init; } = SnoopDirection.Both;

    /// <summary>Direction of the whisper leg (what the snooper injects into the target).</summary>
    public SnoopDirection Whisper { get; init; } = SnoopDirection.None;

    /// <summary>Optional explicit snoop channel ID. If null, Asterisk assigns one.</summary>
    public string? SnoopId { get; init; }

    /// <summary>The snoop channel created by Asterisk; available after <c>StartAsync</c> completes.</summary>
    public AriChannel? SnoopChannel { get; private set; }

    protected override async ValueTask ExecuteAsync(CancellationToken cancellationToken)
    {
        SnoopChannel = await AriClient.Channels.SnoopAsync(
            TargetChannelId,
            App,
            spy: ToDirectionString(Spy),
            whisper: ToDirectionString(Whisper),
            snoopId: SnoopId,
            cancellationToken: cancellationToken);
    }

    private static string? ToDirectionString(SnoopDirection direction) => direction switch
    {
        SnoopDirection.None => null,
        SnoopDirection.In => "in",
        SnoopDirection.Out => "out",
        SnoopDirection.Both => "both",
        _ => null,
    };
}
