using Verbara.Sdk.Sessions.Manager;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Sessions.Internal;

[OptionsValidator]
internal partial class SessionOptionsValidator : IValidateOptions<SessionOptions>
{
}
