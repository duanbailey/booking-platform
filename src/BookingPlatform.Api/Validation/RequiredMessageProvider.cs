using System.ComponentModel.DataAnnotations;
using BookingPlatform.Api.Errors;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;

namespace BookingPlatform.Api.Validation;

/// <summary>
/// T30: every [Required] says "Is required." instead of "The Price field is required.", which names the C#
/// property. Covers the attributes in the code and the ones MVC adds for non-nullable properties; it must run
/// after MVC's DataAnnotations provider, so it is added to the end of the list. An attribute with its own
/// message keeps it.
/// </summary>
public sealed class RequiredMessageProvider : IValidationMetadataProvider
{
    public void CreateValidationMetadata(ValidationMetadataProviderContext context)
    {
        foreach (var required in context.ValidationMetadata.ValidatorMetadata.OfType<RequiredAttribute>())
        {
            if (required.ErrorMessage is null && required.ErrorMessageResourceName is null)
            {
                required.ErrorMessage = ErrorResponses.RequiredMessage;
            }
        }
    }
}
