using FluentValidation.Results;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace ZenLead.Api;

public static class ValidationExtensions
{
    public static ModelStateDictionary ToModelState(this ValidationResult validation)
    {
        var modelState = new ModelStateDictionary();
        foreach (var error in validation.Errors)
            modelState.AddModelError(error.PropertyName, error.ErrorMessage);
        return modelState;
    }
}
