using Verbara.Sdk.Hosting;
using Verbara.Sdk.Sessions.Extensions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Sdk.Sessions.Redis.Tests;

/// <summary>
/// The range the <c>UseRedis</c> registrations accept for <see cref="RedisSessionStoreOptions.ActiveRecordTtl"/>:
/// unset, or from 30 seconds to 24 hours, both ends included. Read through the options alone, so no Redis is needed:
/// the store's resolution reads the same options and fails the same way (<c>UseRedisExtensionsTests</c>).
/// </summary>
public sealed class ActiveRecordTtlValidationTests
{
    private const long ThirtySecondsMs = 30_000;
    private const long TwentyFourHoursMs = 24 * 60 * 60 * 1000L;

    public enum Overload
    {
        Options,
        ConnectionString,
    }

    /// <summary>Reads the options as the store's resolution would; the connection is never opened.</summary>
    private static RedisSessionStoreOptions Resolve(Overload overload, TimeSpan? ttl)
    {
        var services = new ServiceCollection();
        var builder = services.AddVerbaraSessionsBuilder();
        _ = overload switch
        {
            Overload.Options => builder.UseRedis(opts =>
            {
                opts.ConfigurationString = "localhost:1";
                opts.ActiveRecordTtl = ttl;
            }),
            _ => builder.UseRedis("localhost:1", opts => opts.ActiveRecordTtl = ttl),
        };
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<RedisSessionStoreOptions>>().Value;
    }

    [Theory]
    [InlineData(Overload.Options, ThirtySecondsMs)]
    [InlineData(Overload.Options, ThirtySecondsMs + 1)]
    [InlineData(Overload.Options, TwentyFourHoursMs - 1)]
    [InlineData(Overload.Options, TwentyFourHoursMs)]
    [InlineData(Overload.ConnectionString, ThirtySecondsMs)]
    [InlineData(Overload.ConnectionString, TwentyFourHoursMs)]
    public void UseRedis_ShouldAcceptTheValue_WhenActiveRecordTtlIsWithinThirtySecondsAndTwentyFourHours(
        Overload overload, long milliseconds)
    {
        var ttl = TimeSpan.FromMilliseconds(milliseconds);

        Resolve(overload, ttl).ActiveRecordTtl.Should().Be(ttl);
    }

    [Theory]
    [InlineData(Overload.Options)]
    [InlineData(Overload.ConnectionString)]
    public void UseRedis_ShouldAcceptTheValue_WhenActiveRecordTtlIsUnset(Overload overload) =>
        Resolve(overload, null).ActiveRecordTtl.Should().BeNull();

    [Theory]
    [InlineData(Overload.Options, ThirtySecondsMs - 1)]
    [InlineData(Overload.Options, 0)]
    [InlineData(Overload.Options, -1)]
    [InlineData(Overload.Options, TwentyFourHoursMs + 1)]
    [InlineData(Overload.Options, 150L * TwentyFourHoursMs)]
    [InlineData(Overload.ConnectionString, ThirtySecondsMs - 1)]
    [InlineData(Overload.ConnectionString, TwentyFourHoursMs + 1)]
    public void UseRedis_ShouldRejectTheValue_WhenActiveRecordTtlIsOutsideThirtySecondsAndTwentyFourHours(
        Overload overload, long milliseconds)
    {
        var resolve = () => Resolve(overload, TimeSpan.FromMilliseconds(milliseconds));

        resolve.Should().Throw<OptionsValidationException>().WithMessage("*ActiveRecordTtl*30 seconds*24 hours*");
    }
}
