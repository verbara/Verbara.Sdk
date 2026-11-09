namespace Verbara.Sdk.FunctionalTests.Layer5_Integration.Ari;

using Verbara.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Verbara.Sdk.FunctionalTests.Infrastructure.Helpers;
using FluentAssertions;

/// <summary>
/// <c>CreateWithoutDialAsync</c> against a real Asterisk: every variable passed to the create is set on the channel
/// Asterisk made, readable through ARI right after the create, before any dial.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it needs Asterisk.</b> Asterisk answers a create 200 whatever it did with the variables, so only reading
/// them back from a real Asterisk shows whether they arrived. A create that carried them in the query was accepted and
/// left every variable unset.
/// </para>
/// <para>
/// <b>Off the PR path</b>: this lane runs in the merge queue, on the scheduled matrix, and on a pull request only
/// when it carries the <c>ci:functional</c> label.
/// </para>
/// </remarks>
[Collection("Functional")]
[Trait("Category", "Functional")]
public sealed class AriCreateWithoutDialVariablesTests : FunctionalTestBase
{
    /// <summary>The Stasis application <see cref="AriClientFactory"/> subscribes and the create names.</summary>
    private const string StasisApp = "test-app";

    public AriCreateWithoutDialVariablesTests() : base("Verbara.Sdk.Ari")
    {
    }

    /// <summary>
    /// Two variables passed to the create, one holding a space, <c>&amp;</c>, <c>=</c>, <c>+</c>, a double quote and a
    /// non-ASCII letter, are each read back from the created channel with the value passed.
    /// </summary>
    /// <remarks>
    /// The channel id and the plain value are unique per run, so a channel left behind by another test cannot answer
    /// for this one. The created channel is never dialed; it is hung up in <c>finally</c>.
    /// </remarks>
    [Fact]
    public async Task CreateWithoutDialAsync_ShouldSetEachVariable_WhenCreatedWithVariables()
    {
        const string plainName = "VSDK_FT_CWD";
        const string reservedName = "VSDK_FT_CWD_RESERVED";
        const string reservedValue = "a b&c=d+\"é";
        var plainValue = $"ft-{Guid.NewGuid():N}";
        var channelId = Guid.NewGuid().ToString();

        await using var ariClient = AriClientFactory.Create(LoggerFactory, StasisApp);
        var connect = async () => await ariClient.ConnectAsync();
        await connect.Should().NotThrowAsync(
            "the functional fixture runs Asterisk with ARI enabled for user testari, so the event WebSocket for test-app must open");

        var created = await ariClient.Channels.CreateWithoutDialAsync(
            "Local/100@test-functional", StasisApp, channelId,
            variables: new Dictionary<string, string> { [plainName] = plainValue, [reservedName] = reservedValue });
        try
        {
            created.Id.Should().Be(channelId, "the create keeps the channel id named in its query");

            (await ReadVariableAsync(ariClient, created.Id, plainName)).Should().Be(
                plainValue, "the create was given this variable, and Asterisk must have set it on the channel");
            (await ReadVariableAsync(ariClient, created.Id, reservedName)).Should().Be(
                reservedValue, "a value with reserved characters must arrive unchanged");
        }
        finally
        {
            await BestEffort.AriAsync(() => ariClient.Channels.HangupAsync(created.Id));
        }
    }

    /// <summary>
    /// The value of <paramref name="variable"/> on the channel, or <see langword="null"/> when Asterisk answers that it
    /// is not set, so a variable the create dropped fails on the value asserted rather than on an exception.
    /// </summary>
    private static async Task<string?> ReadVariableAsync(Verbara.Sdk.Ari.Client.AriClient ariClient, string channelId, string variable)
    {
        try
        {
            return (await ariClient.Channels.GetVariableAsync(channelId, variable)).Value;
        }
        catch (Verbara.Sdk.Ari.AriNotFoundException)
        {
            return null;
        }
    }
}
