using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Push.Webhooks;

/// <summary>
/// Source-generated (AOT-safe) validator for <see cref="WebhookDeliveryOptions"/>, registered by
/// <see cref="WebhookServiceCollectionExtensions.AddVerbaraPushWebhooks"/>. It applies
/// <see cref="WebhookDeliveryRule"/> to every member marked <see cref="WebhookDeliveryRuleAttribute"/>.
/// </summary>
[OptionsValidator]
internal sealed partial class WebhookDeliveryOptionsValidator : IValidateOptions<WebhookDeliveryOptions>
{
}

/// <summary>Marks a <see cref="WebhookDeliveryOptions"/> member checked by <see cref="WebhookDeliveryRule"/>.</summary>
[AttributeUsage(AttributeTargets.Property)]
internal sealed class WebhookDeliveryRuleAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (validationContext.ObjectInstance is not WebhookDeliveryOptions options
            || validationContext.MemberName is not { } member)
            return ValidationResult.Success;

        return WebhookDeliveryRule.Violation(options, member) is { } message
            ? new ValidationResult(message, [member])
            : ValidationResult.Success;
    }
}
