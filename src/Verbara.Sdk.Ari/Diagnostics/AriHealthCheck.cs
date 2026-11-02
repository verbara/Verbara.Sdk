using Verbara.Sdk.Ari.Internal;
using Verbara.Sdk.Enums;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Verbara.Sdk.Ari.Diagnostics;

/// <summary>
/// Health check for the ARI client's connection state. It reads the state with the same table as the AMI
/// connection's check, ARI's own <see cref="AriConnectionState.Faulted"/> mapped by what it means:
/// <list type="bullet">
///   <item><description><see cref="AriConnectionState.Connected"/>: <see cref="HealthStatus.Healthy"/>.</description></item>
///   <item><description><see cref="AriConnectionState.Connecting"/> and <see cref="AriConnectionState.Reconnecting"/>:
///   <see cref="HealthStatus.Degraded"/> — not connected, but a dial is in progress or the reconnect loop is still
///   running.</description></item>
///   <item><description><see cref="AriConnectionState.Initial"/>: <see cref="HealthStatus.Degraded"/> — nobody has
///   connected the client yet, and it does not connect on its own.</description></item>
///   <item><description><see cref="AriConnectionState.Disconnecting"/>, <see cref="AriConnectionState.Disconnected"/>
///   and <see cref="AriConnectionState.Faulted"/>: <see cref="HealthStatus.Unhealthy"/> — nothing reconnects the
///   client until a new connect. Every path that writes <c>Faulted</c> ends the client's dialling: a failed first
///   connect starts no reconnect loop, and a give-up, a refused reconnect or a backoff that cannot be computed ends
///   it.</description></item>
/// </list>
/// <see cref="AriConnectionState.RestOnly"/> says nothing about Asterisk, so for that state alone the check asks: it
/// pings Asterisk once, bounded at 5 s and linked to the caller's token. An answer is
/// <see cref="HealthStatus.Healthy"/>. Anything else, a refused credential, a <c>5xx</c>, a transport failure, the bound's
/// expiry or a body that does not parse, is <see cref="HealthStatus.Unhealthy"/>, with the exception on the result: no
/// loop is recovering anything, and the next run asks again. The caller's own cancellation propagates. No other state
/// sends a request.
/// <para>The result's data carries <c>ariState</c>, the state's name as a string, and its description names the state.</para>
/// </summary>
public sealed class AriHealthCheck(IAriClient client) : IHealthCheck
{
    // The clock the ping's bound runs on. Settable by tests (via InternalsVisibleTo) to drive it on a manual clock.
    internal TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var state = client.State;
        var stateName = state.ToString();
        var data = new Dictionary<string, object>
        {
            // A string, never the boxed enum: a reflection-free JSON writer cannot serialize an arbitrary enum.
            ["ariState"] = stateName,
        };

        if (state == AriConnectionState.RestOnly)
            return AskAsteriskAsync(stateName, data, cancellationToken);

        return Task.FromResult(state switch
        {
            AriConnectionState.Connected => HealthCheckResult.Healthy("ARI " + stateName, data),
            AriConnectionState.Disconnecting or AriConnectionState.Disconnected or AriConnectionState.Faulted =>
                HealthCheckResult.Unhealthy("ARI " + stateName + ": nothing reconnects it until a new connect", data: data),
            AriConnectionState.Initial =>
                HealthCheckResult.Degraded("ARI " + stateName + ": not connected yet", data: data),
            _ => HealthCheckResult.Degraded("ARI " + stateName + ": the connection may still recover on its own", data: data),
        });
    }

    private async Task<HealthCheckResult> AskAsteriskAsync(
        string stateName, Dictionary<string, object> data, CancellationToken cancellationToken)
    {
        using var bound = new CancellationTokenSource(AriConnectBound.Default, TimeProvider);
        using var ask = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, bound.Token);
        try
        {
            await client.Asterisk.PingAsync(ask.Token).ConfigureAwait(false);
            return HealthCheckResult.Healthy("ARI " + stateName + ": Asterisk answered", data);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller's own cancellation, read from its token: the bound's expiry arrives here too, and it is an
            // outcome of the check.
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return HealthCheckResult.Unhealthy("ARI " + stateName + ": Asterisk did not answer the ping", ex, data);
        }
    }
}
