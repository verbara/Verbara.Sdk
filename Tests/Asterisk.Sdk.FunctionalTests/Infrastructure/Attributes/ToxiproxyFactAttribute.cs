namespace Asterisk.Sdk.FunctionalTests.Infrastructure.Attributes;

using Asterisk.Sdk.FunctionalTests.Infrastructure.Helpers;

public sealed class ToxiproxyFactAttribute : FactAttribute
{
    public ToxiproxyFactAttribute()
    {
        if (!ToxiproxyControl.IsAvailable())
            Skip = "Toxiproxy is not available";
    }
}
