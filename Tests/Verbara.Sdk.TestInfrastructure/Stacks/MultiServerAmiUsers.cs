using System.Security.Cryptography;
using System.Text;

namespace Verbara.Sdk.TestInfrastructure.Stacks;

/// <summary>An AMI user of a fixed class: its name, its read and write classes, and an optional event filter.</summary>
/// <param name="Name">The user's name in <c>manager.conf</c>.</param>
/// <param name="Read">The <c>read</c> classes.</param>
/// <param name="Write">The <c>write</c> classes.</param>
/// <param name="EventFilter">An <c>eventfilter</c> line, or <see langword="null"/> for none.</param>
public sealed record AmiUserClass(string Name, string Read, string Write, string? EventFilter = null);

/// <summary>
/// The AMI users each server of the two-server fixture has, and the per-run <c>manager.conf</c> that declares them.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Consumer"/> is the class set a production host registers: every class an SDK consumer reads calls,
/// queues and agents through, and no <c>dialplan</c>, <c>dtmf</c> or <c>cdr</c>. Measured on Asterisk 22 and 23 over
/// 10 queue calls answered on the far server: it receives every <c>Hangup</c> and <c>AgentConnect</c> and no
/// <c>Newexten</c> or <c>VarSet</c>; <see cref="NoAgent"/>, the same set without <c>agent</c>, receives every
/// <c>Hangup</c> and no <c>AgentConnect</c>, <c>AgentCalled</c> or <c>QueueCallerJoin</c>; <see cref="NoHangup"/>
/// receives everything but <c>Hangup</c>.
/// </para>
/// <para>
/// Secrets are never written to the tree: <see cref="WriteManagerConf"/> takes them from the run, which generates
/// them with <see cref="NewSecret"/>.
/// </para>
/// </remarks>
public static class MultiServerAmiUsers
{
    /// <summary>The AMI port, inside the fixture's network.</summary>
    public const int AmiPort = 5038;

    /// <summary>Every read and write class: the ground truth.</summary>
    public static readonly AmiUserClass Full = new("full", "all", "all");

    /// <summary>Every class, with an event filter that removes <c>Hangup</c>.</summary>
    public static readonly AmiUserClass NoHangup = new("nohangup", "all", "all", "!Event: Hangup");

    /// <summary>The <see cref="Consumer"/> class set without the <c>agent</c> read class.</summary>
    public static readonly AmiUserClass NoAgent = new(
        "noagent", "system,call,user,config,originate,reporting,command", "system,call,agent,user,config,originate,command");

    /// <summary>The class set a production host registers.</summary>
    public static readonly AmiUserClass Consumer = new(
        "consumer", "system,call,agent,user,config,originate,reporting,command", "system,call,agent,user,config,originate,command");

    /// <summary>Every user each server has, in the order <c>manager.conf</c> declares them.</summary>
    public static readonly IReadOnlyList<AmiUserClass> All = [Full, NoHangup, NoAgent, Consumer];

    /// <summary>A secret for one user of one run: 16 random bytes, hexadecimal.</summary>
    public static string NewSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>The <c>manager.conf</c> that declares every user of <see cref="All"/> with its secret.</summary>
    /// <param name="secrets">Each user's secret, by name; every user of <see cref="All"/> must have one.</param>
    public static string WriteManagerConf(IReadOnlyDictionary<string, string> secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);

        var text = new StringBuilder();
        text.Append("[general]\nenabled = yes\nbindaddr = 0.0.0.0\n")
            .Append("port = ").Append(AmiPort.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n')
            .Append("timestampevents = yes\n");
        foreach (var user in All)
        {
            text.Append('\n').Append('[').Append(user.Name).Append("]\n")
                .Append("secret = ").Append(secrets[user.Name]).Append('\n')
                .Append("read = ").Append(user.Read).Append('\n')
                .Append("write = ").Append(user.Write).Append('\n');
            if (user.EventFilter is not null)
                text.Append("eventfilter = ").Append(user.EventFilter).Append('\n');
        }

        return text.ToString();
    }
}
