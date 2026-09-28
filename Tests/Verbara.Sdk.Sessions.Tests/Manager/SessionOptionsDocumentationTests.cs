using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Sessions.Tests.Manager;

/// <summary>
/// Binds what the published documentation of <see cref="SessionOptions"/> says about the hold on
/// ended calls: that <see cref="SessionOptions.MaxCompletedSessions"/> bounds nothing, and that
/// <see cref="SessionOptions.CompletedRetention"/> is the bound — and that an idle process releases
/// nothing.
///
/// <para>The maximum is published, validated and read by nothing; it was not made a bound, because a
/// count that undercuts retention was measured to cost more than the memory it saves. What keeps a
/// consumer from relying on it is its documentation, so the documentation is what this test reads: the
/// XML documentation file the package ships beside the assembly, as the build emitted it — not the
/// source comment — with each <c>&lt;see cref&gt;</c> read as the name of the member it points to.</para>
/// </summary>
public sealed class SessionOptionsDocumentationTests
{
    private const string MemberPrefix = "P:Verbara.Sdk.Sessions.Manager.SessionOptions.";

    [Fact]
    public void PublishedDocumentation_ShouldSayTheMaximumBoundsNothingAndNameTheRetentionPeriodAsTheBound_WhenAConsumerReadsIt()
    {
        var file = Path.Join(AppContext.BaseDirectory, "Verbara.Sdk.Sessions.xml");
        File.Exists(file).Should().BeTrue($"premise: the build emits the package's documentation file beside the assembly ({file})");
        var documentation = XDocument.Load(file);

        var maximum = Summary(documentation, nameof(SessionOptions.MaxCompletedSessions));
        var retention = Summary(documentation, nameof(SessionOptions.CompletedRetention));

        new
        {
            MaximumIsNotABound = maximum.Contains("Not a bound", StringComparison.Ordinal),
            MaximumDoesNotLimitTheEndedCallsHeld = maximum.Contains("does not limit how many ended calls", StringComparison.Ordinal),
            MaximumNamesRetentionAsTheOnlyBound = maximum.Contains("CompletedRetention is the only bound", StringComparison.Ordinal),
            RetentionSaysHowLongAnEndedCallIsHeld = retention.Contains("How long an ended call stays held", StringComparison.Ordinal),
            RetentionIsTheOnlyBound = retention.Contains("the only bound on the ended calls held", StringComparison.Ordinal),
            RetentionSaysTheMaximumBoundsNothing = retention.Contains("MaxCompletedSessions bounds nothing", StringComparison.Ordinal),
            RetentionSaysWhatReleases = retention.Contains("release it on the next arrival or ending of a call", StringComparison.Ordinal),
            RetentionSaysAnIdleProcessReleasesNothing = retention.Contains("a process that receives no further calls releases nothing", StringComparison.Ordinal),
        }.Should().BeEquivalentTo(
            new
            {
                MaximumIsNotABound = true,
                MaximumDoesNotLimitTheEndedCallsHeld = true,
                MaximumNamesRetentionAsTheOnlyBound = true,
                RetentionSaysHowLongAnEndedCallIsHeld = true,
                RetentionIsTheOnlyBound = true,
                RetentionSaysTheMaximumBoundsNothing = true,
                RetentionSaysWhatReleases = true,
                RetentionSaysAnIdleProcessReleasesNothing = true,
            },
            "the published documentation says the maximum does not bound the number of ended calls held, "
            + "names the retention period as the bound and what releases a call past it, and says that an "
            + $"idle process releases nothing. Published MaxCompletedSessions: \"{maximum}\". Published "
            + $"CompletedRetention: \"{retention}\"");
    }

    /// <summary>
    /// The member's published summary as one line of text: every element flattened to its text, each
    /// <c>&lt;see cref&gt;</c> replaced by the last segment of the member it names, and whitespace
    /// collapsed. Empty when the member carries no summary.
    /// </summary>
    private static string Summary(XDocument documentation, string property)
    {
        var summary = documentation.Descendants("member")
            .Where(m => (string?)m.Attribute("name") == MemberPrefix + property)
            .Elements("summary")
            .FirstOrDefault();
        if (summary is null)
            return string.Empty;

        var text = new StringBuilder();
        Flatten(summary, text);
        return string.Join(' ', text.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static void Flatten(XElement element, StringBuilder text)
    {
        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XText run:
                    text.Append(run.Value);
                    break;
                case XElement { Name.LocalName: "see" } see when see.Attribute("cref") is { } cref:
                    text.Append(MemberName(cref.Value));
                    break;
                case XElement child:
                    text.Append(' ');
                    Flatten(child, text);
                    text.Append(' ');
                    break;
            }
        }
    }

    /// <summary>
    /// The name a <c>cref</c> points to, without its kind prefix, its namespace and type, or a method's
    /// parameter list: <c>P:…SessionOptions.CompletedRetention</c> reads <c>CompletedRetention</c>, and
    /// <c>M:…ICallSessionManager.GetById(System.String)</c> reads <c>GetById</c>.
    /// </summary>
    private static string MemberName(string cref)
    {
        var parameters = cref.IndexOf('(', StringComparison.Ordinal);
        var member = parameters < 0 ? cref : cref[..parameters];
        return member[(member.LastIndexOf('.') + 1)..];
    }
}
