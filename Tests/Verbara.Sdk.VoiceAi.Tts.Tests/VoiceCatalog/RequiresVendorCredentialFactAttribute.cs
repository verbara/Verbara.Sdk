using Xunit;

namespace Verbara.Sdk.VoiceAi.Tts.Tests.VoiceCatalog;

/// <summary>
/// A <see cref="FactAttribute"/> that skips when the named vendor credential is absent from the
/// environment, so a developer without keys sees a skip rather than a failure — and a run that
/// <em>has</em> the key gets a real verdict instead of a silent pass.
/// </summary>
/// <remarks>
/// Skipping is the honest outcome of "not measured". The alternative — a test that quietly returns
/// green when unconfigured — is the failure mode this whole test class exists to eliminate, and it
/// would be perverse to reproduce it in the instrument.
/// </remarks>
public sealed class RequiresVendorCredentialFactAttribute : FactAttribute
{
    /// <param name="environmentVariable">Name of the environment variable holding the key.</param>
    public RequiresVendorCredentialFactAttribute(string environmentVariable)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(environmentVariable)))
        {
            Skip = $"{environmentVariable} is not set — voice catalog left unverified, not asserted.";
        }
    }
}
