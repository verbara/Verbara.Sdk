using Asterisk.Sdk.Sessions.Manager;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.Sessions.Internal;

[OptionsValidator]
internal partial class SessionOptionsValidator : IValidateOptions<SessionOptions>
{
}
