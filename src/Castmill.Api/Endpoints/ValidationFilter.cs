using System.ComponentModel.DataAnnotations;

namespace Castmill.Api.Endpoints;

/// <summary>
/// Validates the first argument of type T against its DataAnnotations before
/// the endpoint runs — malformed input never reaches business code.
/// </summary>
public sealed class ValidationFilter<T> : IEndpointFilter where T : class
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var argument = context.Arguments.OfType<T>().FirstOrDefault();
        if (argument is null)
        {
            return Results.BadRequest();
        }

        var results = new List<ValidationResult>();
        if (!Validator.TryValidateObject(argument, new ValidationContext(argument), results, validateAllProperties: true))
        {
            return Results.ValidationProblem(results
                .SelectMany(r => r.MemberNames.DefaultIfEmpty(string.Empty), (r, m) => (Member: m, r.ErrorMessage))
                .GroupBy(x => x.Member, x => x.ErrorMessage ?? "Invalid value.")
                .ToDictionary(g => g.Key, g => g.ToArray()));
        }

        return await next(context);
    }
}

public static class ValidationFilterExtensions
{
    public static RouteHandlerBuilder Validate<T>(this RouteHandlerBuilder builder) where T : class =>
        builder.AddEndpointFilter(new ValidationFilter<T>());
}
