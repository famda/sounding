using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Sounding.Dashboard.Api.Endpoints;

public static class EndpointMetadata
{
    // Every query can be rejected as invalid, names a host or container that may not exist, and reads the fleet
    // snapshot, which answers 503 until it is loaded. The name is the operation id generated clients use.
    public static RouteHandlerBuilder Returns<T>(this RouteHandlerBuilder builder, string name, string summary) => builder
        .WithName(name)
        .WithSummary(summary)
        .Produces<T>()
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

    // Text parameters with a fixed set of values ([Choices]) are listed as an enum.
    public static Task ListChoices(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        foreach (var description in context.Description.ParameterDescriptions)
        {
            if (description.ParameterDescriptor is not Microsoft.AspNetCore.Mvc.Infrastructure.IParameterInfoParameterDescriptor { ParameterInfo: { } info }
                || info.GetCustomAttributes(typeof(AllowedValuesAttribute), inherit: true) is not [AllowedValuesAttribute allowed, ..])
            {
                continue;
            }

            foreach (var parameter in operation.Parameters ?? [])
            {
                if (parameter.Name == description.Name && parameter is OpenApiParameter { Schema: OpenApiSchema schema })
                {
                    schema.Enum = [.. allowed.Values.Select(static value => (JsonNode)JsonValue.Create((string)value!))];
                }
            }
        }

        return Task.CompletedTask;
    }
}
