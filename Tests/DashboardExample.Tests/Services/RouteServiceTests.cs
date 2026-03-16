using DashboardExample.Services;
using FluentAssertions;

namespace DashboardExample.Tests.Services;

public class RouteServiceTests
{
    [Theory]
    [InlineData("_NXXNXXXXXX", true)]
    [InlineData("_00X.", true)]
    [InlineData("911", true)]
    [InlineData("_1NXXNXXXXXX", true)]
    [InlineData("_[2-9]XXXXXXX", true)]
    [InlineData("", false)]
    [InlineData("abc", false)]
    [InlineData("_", false)]
    public void IsValidDialPattern_ShouldValidateCorrectly(string pattern, bool expected)
    {
        RouteService.IsValidDialPattern(pattern).Should().Be(expected);
    }
}
