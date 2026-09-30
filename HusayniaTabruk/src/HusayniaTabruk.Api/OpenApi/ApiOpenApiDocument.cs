using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using HusayniaTabruk.Api.Configuration;
using HusayniaTabruk.Api.Endpoints.V1.Signups.Submit;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Errors;
using HusayniaTabruk.Domain.Signups;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.WebUtilities;

namespace HusayniaTabruk.Api.OpenApi;

public static class ApiOpenApiDocument
{
    private static readonly string[] OrderedHttpMethods =
    [
        HttpMethods.Get,
        HttpMethods.Post,
        HttpMethods.Put,
        HttpMethods.Patch,
        HttpMethods.Delete,
        HttpMethods.Options,
        HttpMethods.Head,
    ];

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };
    public static string CreateJson(IEnumerable<EndpointDataSource> dataSources)
    {
        ArgumentNullException.ThrowIfNull(dataSources);

        OpenApiSchemaRegistry schemaRegistry = new();

        JsonObject document = new()
        {
            ["openapi"] = "3.1.0",
            ["info"] = new JsonObject
            {
                ["title"] = "Husaynia Tabruk API",
                ["version"] = "v1",
            },
            ["servers"] = new JsonArray
            {
                new JsonObject
                {
                    ["url"] = ApiDefaults.BasePath,
                },
            },
            ["security"] = new JsonArray
            {
                new JsonObject
                {
                    ["bearerAuth"] = new JsonArray(),
                },
            },
            ["paths"] = CreatePaths(dataSources, schemaRegistry),
            ["components"] = CreateComponents(schemaRegistry),
            ["x-conventions"] = CreateConventions(),
        };

        return document.ToJsonString(SerializerOptions).ReplaceLineEndings("\n") + "\n";
    }

    private static JsonObject CreatePaths(
        IEnumerable<EndpointDataSource> dataSources,
        OpenApiSchemaRegistry schemaRegistry)
    {
        List<OpenApiOperation> operations = [];

        foreach (RouteEndpoint endpoint in dataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>())
        {
            if (endpoint.Metadata.GetMetadata<IExcludeFromDescriptionMetadata>()?.ExcludeFromDescription == true)
            {
                continue;
            }

            IHttpMethodMetadata? methodMetadata = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>();
            if (methodMetadata is null)
            {
                continue;
            }

            string? normalizedPath = NormalizeApiPath(endpoint.RoutePattern);
            if (normalizedPath is null)
            {
                continue;
            }

            foreach (string method in methodMetadata.HttpMethods)
            {
                operations.Add(new OpenApiOperation(normalizedPath, method.ToUpperInvariant(), endpoint));
            }
        }

        HashSet<string> operationKeys = new(StringComparer.Ordinal);
        foreach (OpenApiOperation operation in operations)
        {
            string key = operation.Path + "\n" + operation.Method;
            if (!operationKeys.Add(key))
            {
                throw new InvalidOperationException(
                    $"Duplicate OpenAPI operation '{operation.Method} {operation.Path}'.");
            }
        }

        JsonObject paths = new();
        foreach (IGrouping<string, OpenApiOperation> pathOperations in operations
                     .OrderBy(operation => operation.Path, StringComparer.Ordinal)
                     .ThenBy(operation => GetHttpMethodOrder(operation.Method))
                     .ThenBy(operation => operation.Method, StringComparer.Ordinal)
                     .GroupBy(operation => operation.Path, StringComparer.Ordinal))
        {
            JsonObject pathItem = new();
            foreach (OpenApiOperation operation in pathOperations)
            {
                pathItem[operation.Method.ToLowerInvariant()] = CreateOperation(
                    operation.Endpoint,
                    schemaRegistry);
            }

            paths[pathOperations.Key] = pathItem;
        }

        return paths;
    }

    private static JsonObject CreateOperation(
        RouteEndpoint endpoint,
        OpenApiSchemaRegistry schemaRegistry)
    {
        JsonObject operation = new();
        string? endpointName = endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName;
        if (!string.IsNullOrWhiteSpace(endpointName))
        {
            operation["operationId"] = endpointName;
        }

        JsonArray parameters = CreateRouteParameters(endpoint.RoutePattern);
        AddMetadataParameters(endpoint, parameters);
        if (parameters.Count > 0)
        {
            operation["parameters"] = parameters;
        }

        JsonObject? requestBody = CreateRequestBody(endpoint, schemaRegistry);
        if (requestBody is not null)
        {
            operation["requestBody"] = requestBody;
        }

        operation["responses"] = CreateResponses(endpoint, schemaRegistry);

        if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            operation["security"] = new JsonArray();
        }

        return operation;
    }

    private static JsonArray CreateRouteParameters(RoutePattern routePattern)
    {
        JsonArray parameters = new();
        foreach (RoutePatternParameterPart parameter in routePattern.Parameters)
        {
            parameters.Add(new JsonObject
            {
                ["name"] = parameter.Name,
                ["in"] = "path",
                ["required"] = true,
                ["schema"] = new JsonObject
                {
                    ["type"] = "string",
                },
            });
        }

        return parameters;
    }

    private static void AddMetadataParameters(RouteEndpoint endpoint, JsonArray parameters)
    {
        foreach (OpenApiParameterReferenceMetadata parameterReference in
                 endpoint.Metadata.GetOrderedMetadata<OpenApiParameterReferenceMetadata>())
        {
            parameters.Add(new JsonObject
            {
                ["$ref"] = $"#/components/parameters/{parameterReference.ComponentName}",
            });
        }
    }

    private static JsonObject? CreateRequestBody(
        RouteEndpoint endpoint,
        OpenApiSchemaRegistry schemaRegistry)
    {
        IReadOnlyList<IAcceptsMetadata> acceptsMetadata = endpoint.Metadata
            .GetOrderedMetadata<IAcceptsMetadata>();
        IAcceptsMetadata? requestMetadata = acceptsMetadata.Count > 0
            ? acceptsMetadata[acceptsMetadata.Count - 1]
            : null;
        Type? requestType = requestMetadata?.RequestType;
        if (requestType is null || requestType == typeof(void))
        {
            return null;
        }

        IAcceptsMetadata nonNullRequestMetadata = requestMetadata!;
        JsonObject content = new();
        foreach (string contentType in nonNullRequestMetadata.ContentTypes
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(contentType => contentType, StringComparer.Ordinal))
        {
            content[contentType] = new JsonObject
            {
                ["schema"] = schemaRegistry.CreateDocumentSchema(requestType),
            };
        }

        return new JsonObject
        {
            ["required"] = true,
            ["content"] = content,
        };
    }

    private static JsonObject CreateResponses(
        RouteEndpoint endpoint,
        OpenApiSchemaRegistry schemaRegistry)
    {
        IProducesResponseTypeMetadata[] responseMetadata = endpoint.Metadata
            .GetOrderedMetadata<IProducesResponseTypeMetadata>()
            .OrderBy(metadata => metadata.StatusCode)
            .ToArray();
        if (responseMetadata.Length == 0)
        {
            return new JsonObject
            {
                ["default"] = new JsonObject
                {
                    ["description"] = "Default response.",
                },
            };
        }

        JsonObject responses = new();
        foreach (IGrouping<int, IProducesResponseTypeMetadata> responseGroup in
                 responseMetadata.GroupBy(metadata => metadata.StatusCode).OrderBy(group => group.Key))
        {
            string statusCode = responseGroup.Key.ToString(CultureInfo.InvariantCulture);
            string reasonPhrase = ReasonPhrases.GetReasonPhrase(responseGroup.Key);
            JsonObject response = new()
            {
                ["description"] = string.IsNullOrEmpty(reasonPhrase)
                    ? $"HTTP {statusCode} response."
                    : reasonPhrase,
            };

            Type? responseType = responseGroup
                .Select(metadata => metadata.Type)
                .FirstOrDefault(type => type is not null && type != typeof(void));
            string[] contentTypes = responseGroup
                .SelectMany(metadata => metadata.ContentTypes)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(contentType => contentType, StringComparer.Ordinal)
                .ToArray();
            if (contentTypes.Length > 0)
            {
                JsonObject content = new();
                foreach (string contentType in contentTypes)
                {
                    content[contentType] = new JsonObject
                    {
                        ["schema"] = CreateResponseSchema(
                            responseType,
                            contentType,
                            schemaRegistry),
                    };
                }

                response["content"] = content;
            }

            OpenApiResponseHeaderMetadata[] responseHeaders = endpoint.Metadata
                .GetOrderedMetadata<OpenApiResponseHeaderMetadata>()
                .Where(metadata => metadata.StatusCode == responseGroup.Key)
                .OrderBy(metadata => metadata.HeaderName, StringComparer.Ordinal)
                .ToArray();
            if (responseHeaders.Length > 0)
            {
                JsonObject headers = new();
                foreach (OpenApiResponseHeaderMetadata responseHeader in responseHeaders)
                {
                    headers[responseHeader.HeaderName] = new JsonObject
                    {
                        ["required"] = true,
                        ["schema"] = CreateResponseHeaderSchema(responseHeader.HeaderName),
                    };
                }

                response["headers"] = headers;
            }

            responses[statusCode] = response;
        }

        return responses;
    }

    private static JsonObject CreateResponseHeaderSchema(string headerName) =>
        string.Equals(
            headerName,
            ApiDefaults.RetryAfterHeaderName,
            StringComparison.OrdinalIgnoreCase)
            ? new JsonObject
            {
                ["type"] = "integer",
                ["minimum"] = 1,
            }
            : new JsonObject
            {
                ["type"] = "string",
            };

    private static JsonObject CreateResponseSchema(
        Type? responseType,
        string contentType,
        OpenApiSchemaRegistry schemaRegistry)
    {
        if (string.Equals(contentType, "application/problem+json", StringComparison.OrdinalIgnoreCase))
        {
            return new JsonObject
            {
                ["$ref"] = "#/components/schemas/problemDetails",
            };
        }

        return responseType is null || responseType == typeof(void)
            ? new JsonObject()
            : schemaRegistry.CreateDocumentSchema(responseType);
    }

    private static string? NormalizeApiPath(RoutePattern routePattern)
    {
        string route = BuildRoutePath(routePattern);
        if (!route.Equals(ApiDefaults.BasePath, StringComparison.Ordinal) &&
            !route.StartsWith(ApiDefaults.BasePath + "/", StringComparison.Ordinal))
        {
            return null;
        }

        string relativePath = route[ApiDefaults.BasePath.Length..];
        return string.IsNullOrEmpty(relativePath) ? "/" : relativePath;
    }

    private static string BuildRoutePath(RoutePattern routePattern)
    {
        StringBuilder builder = new();
        foreach (RoutePatternPathSegment segment in routePattern.PathSegments)
        {
            builder.Append('/');
            foreach (RoutePatternPart part in segment.Parts)
            {
                switch (part)
                {
                    case RoutePatternLiteralPart literal:
                        builder.Append(literal.Content);
                        break;
                    case RoutePatternSeparatorPart separator:
                        builder.Append(separator.Content);
                        break;
                    case RoutePatternParameterPart parameter:
                        builder.Append('{').Append(parameter.Name).Append('}');
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Unsupported route pattern part '{part.GetType().Name}'.");
                }
            }
        }

        return builder.Length == 0 ? "/" : builder.ToString();
    }

    private static int GetHttpMethodOrder(string method)
    {
        int index = Array.IndexOf(OrderedHttpMethods, method);
        return index >= 0 ? index : OrderedHttpMethods.Length;
    }

    private static JsonObject CreateComponents(OpenApiSchemaRegistry schemaRegistry)
    {
        JsonObject schemas = new()
        {
            ["cursorPage"] = new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray("items", "nextCursor"),
                ["properties"] = new JsonObject
                {
                    ["items"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["maxItems"] = ApplicationLimits.MaximumPageSize,
                        ["items"] = new JsonObject(),
                    },
                    ["nextCursor"] = new JsonObject
                    {
                        ["type"] = new JsonArray("string", "null"),
                        ["description"] = "Opaque continuation cursor.",
                    },
                },
            },
            ["problemDetails"] = CreateProblemDetailsSchema(),
        };

        foreach ((string schemaName, JsonObject schema) in schemaRegistry.GetSchemas())
        {
            schemas[schemaName] = schema;
        }

        return new JsonObject
        {
            ["securitySchemes"] = new JsonObject
            {
                ["bearerAuth"] = new JsonObject
                {
                    ["type"] = "http",
                    ["scheme"] = "bearer",
                    ["bearerFormat"] = "JWT",
                },
            },
            ["parameters"] = new JsonObject
            {
                ["cursor"] = HeaderOrQueryParameter(
                    "cursor",
                    "query",
                    required: false,
                    "Opaque continuation cursor."),
                ["pageSize"] = new JsonObject
                {
                    ["name"] = "pageSize",
                    ["in"] = "query",
                    ["required"] = false,
                    ["schema"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["default"] = ApplicationLimits.DefaultPageSize,
                        ["minimum"] = 1,
                        ["maximum"] = ApplicationLimits.MaximumPageSize,
                    },
                },
                ["dateScope"] = new JsonObject
                {
                    ["name"] = "scope",
                    ["in"] = "query",
                    ["required"] = false,
                    ["description"] = "Date collection scope implemented by T11.",
                    ["schema"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["default"] = "open",
                        ["enum"] = new JsonArray("open"),
                    },
                },
                ["idempotencyKey"] = HeaderOrQueryParameter(
                    ApiDefaults.IdempotencyHeaderName,
                    "header",
                    required: true,
                    "UUID idempotency key for retry-safe writes.",
                    format: "uuid"),
                ["ifMatch"] = HeaderOrQueryParameter(
                    ApiDefaults.IfMatchHeaderName,
                    "header",
                    required: true,
                    "Entity tag required for optimistic writes."),
                ["stepUpToken"] = HeaderOrQueryParameter(
                    "X-Step-Up-Token",
                    "header",
                    required: true,
                    "Single-use purpose-bound step-up token."),
            },
            ["schemas"] = schemas,
            ["responses"] = new JsonObject
            {
                ["payloadTooLarge"] = ProblemResponse(
                    StatusCodes.Status413PayloadTooLarge,
                    ErrorCodes.PayloadTooLarge),
                ["rateLimited"] = RateLimitedResponse(),
            },
        };
    }

    private static JsonObject CreateConventions() => new()
    {
        ["jsonPropertyNaming"] = "camelCase",
        ["jsonEnums"] = "camelCaseStrings",
        ["numericEnumsAccepted"] = false,
        ["problemDetailsMediaType"] = "application/problem+json",
        ["traceHeader"] = ApiDefaults.TraceHeaderName,
        ["endpointBodyLimits"] = new JsonObject
        {
            ["message"] = ApplicationLimits.MaximumMessageRequestBytes,
            ["report"] = ApplicationLimits.MaximumReportRequestBytes,
            ["signup"] = ApplicationLimits.MaximumSignupRequestBytes,
            ["administrative"] = ApplicationLimits.MaximumAdministrativeRequestBytes,
        },
        ["rateLimits"] = new JsonObject
        {
            ["threadPostsPerTenSecondsPerAccount"] = ApplicationLimits.ThreadPostsPerTenSecondsPerAccount,
            ["threadPostsPerMinutePerAccount"] = ApplicationLimits.ThreadPostsPerMinutePerAccount,
            ["threadPostsPerHourPerAccount"] = ApplicationLimits.ThreadPostsPerHourPerAccount,
            ["threadPostsPerHourPerOrganization"] = ApplicationLimits.ThreadPostsPerHourPerOrganization,
            ["reportsPerHourPerAccount"] = ApplicationLimits.ReportsPerHourPerAccount,
            ["reportsPerDayPerAccount"] = ApplicationLimits.ReportsPerDayPerAccount,
            ["reportsPerDayPerOrganization"] = ApplicationLimits.ReportsPerDayPerOrganization,
            ["signupSubmissionsPerMinutePerAccount"] = ApplicationLimits.SignupSubmissionsPerMinutePerAccount,
            ["signupSubmissionsPerHourPerOrganization"] = ApplicationLimits.SignupSubmissionsPerHourPerOrganization,
            ["administrativeMutationsPerMinutePerAccount"] =
                ApplicationLimits.AdministrativeMutationsPerMinutePerAccount,
            ["administrativeMutationsPerHourPerOrganization"] =
                ApplicationLimits.AdministrativeMutationsPerHourPerOrganization,
        },
    };

    private static JsonObject CreateProblemDetailsSchema() => new()
    {
        ["type"] = "object",
        ["required"] = new JsonArray("type", "title", "status", "code", "detail", "traceId"),
        ["properties"] = new JsonObject
        {
            ["type"] = new JsonObject { ["type"] = "string", ["format"] = "uri" },
            ["title"] = new JsonObject { ["type"] = "string" },
            ["status"] = new JsonObject { ["type"] = "integer" },
            ["code"] = new JsonObject { ["type"] = "string" },
            ["detail"] = new JsonObject { ["type"] = "string" },
            ["traceId"] = new JsonObject { ["type"] = "string" },
            ["fieldErrors"] = new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["type"] = "string" },
                },
            },
        },
    };

    private static JsonObject HeaderOrQueryParameter(
        string name,
        string location,
        bool required,
        string description,
        string? format = null)
    {
        JsonObject schema = new()
        {
            ["type"] = "string",
        };
        if (format is not null)
        {
            schema["format"] = format;
        }

        return new JsonObject
        {
            ["name"] = name,
            ["in"] = location,
            ["required"] = required,
            ["description"] = description,
            ["schema"] = schema,
        };
    }

    private static JsonObject ProblemResponse(int status, string code) => new()
    {
        ["description"] = code,
        ["content"] = new JsonObject
        {
            ["application/problem+json"] = new JsonObject
            {
                ["schema"] = new JsonObject
                {
                    ["$ref"] = "#/components/schemas/problemDetails",
                },
                ["example"] = new JsonObject
                {
                    ["type"] = $"https://httpstatuses.com/{status}",
                    ["title"] = status == StatusCodes.Status413PayloadTooLarge
                        ? "Payload too large"
                        : "Request failed",
                    ["status"] = status,
                    ["code"] = code,
                    ["detail"] = "The request was rejected.",
                    ["traceId"] = "00000000000000000000000000000000",
                },
            },
        },
    };

    private static JsonObject RateLimitedResponse()
    {
        JsonObject response = ProblemResponse(StatusCodes.Status429TooManyRequests, ErrorCodes.RateLimited);
        response["headers"] = new JsonObject
        {
            [ApiDefaults.RetryAfterHeaderName] = new JsonObject
            {
                ["required"] = true,
                ["schema"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["minimum"] = 1,
                },
            },
        };
        return response;
    }

    private sealed class OpenApiSchemaRegistry
    {
        private readonly Dictionary<string, JsonObject> schemas = new(StringComparer.Ordinal);
        private readonly NullabilityInfoContext nullabilityContext = new();

        public JsonObject CreateDocumentSchema(Type type)
        {
            ArgumentNullException.ThrowIfNull(type);

            return IsInlineSchema(type)
                ? CreatePropertySchema(type, nullable: false)
                : CreateSchemaReference(type);
        }

        public IEnumerable<KeyValuePair<string, JsonObject>> GetSchemas() =>
            schemas.OrderBy(entry => entry.Key, StringComparer.Ordinal);

        private JsonObject CreateSchemaReference(Type type)
        {
            string schemaId = GetSchemaId(type);
            EnsureSchema(type);
            return new JsonObject
            {
                ["$ref"] = $"#/components/schemas/{schemaId}",
            };
        }

        private void EnsureSchema(Type type)
        {
            string schemaId = GetSchemaId(type);
            if (schemas.ContainsKey(schemaId))
            {
                return;
            }

            schemas[schemaId] = new JsonObject();
            schemas[schemaId] = BuildObjectOrCollectionSchema(type);
        }

        private JsonObject BuildObjectOrCollectionSchema(Type type)
        {
            if (TryGetSimpleSchema(type, nullable: false, out JsonObject? simpleSchema))
            {
                return simpleSchema!;
            }

            if (TryGetEnumerableElementType(type, out Type? elementType))
            {
                return new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = CreatePropertySchema(elementType!, nullable: false),
                };
            }

            JsonObject properties = new();
            JsonArray required = [];
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                         .Where(candidate => candidate.GetMethod is not null && candidate.GetIndexParameters().Length == 0))
            {
                string propertyName = GetJsonPropertyName(property);
                NullabilityInfo nullability = nullabilityContext.Create(property);
                bool nullable = Nullable.GetUnderlyingType(property.PropertyType) is not null
                    || (!property.PropertyType.IsValueType && nullability.ReadState != NullabilityState.NotNull);

                properties[propertyName] = CreatePropertySchema(
                    Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType,
                    nullable);
                if (!nullable
                    || property.IsDefined(typeof(JsonRequiredAttribute), inherit: true))
                {
                    required.Add(propertyName);
                }
            }

            JsonObject schema = new()
            {
                ["type"] = "object",
                ["properties"] = properties,
            };
            if (required.Count > 0)
            {
                schema["required"] = required;
            }

            ApplyKnownContractConstraints(type, schema);
            return schema;
        }

        private static void ApplyKnownContractConstraints(
            Type type,
            JsonObject schema)
        {
            if (type != typeof(SubmitSignupRequest))
            {
                return;
            }

            JsonObject properties = schema["properties"]!.AsObject();
            JsonObject label = properties["label"]!.AsObject();
            label["maxLength"] = ApplicationLimits.MaximumSignupLabelUnicodeScalars;
            label["pattern"] = SignupLabelPolicy.OpenApiPattern;
            label["description"] = SignupLabelPolicy.OpenApiDescription;
            label["x-maxUtf8Bytes"] = ApplicationLimits.MaximumSignupLabelUtf8Bytes;

            JsonObject memberParticipantIds =
                properties["memberParticipantIds"]!.AsObject();
            memberParticipantIds["maxItems"] = ApplicationLimits.MaximumNamedParticipants;
            memberParticipantIds["uniqueItems"] = true;
            memberParticipantIds["items"]!.AsObject()["format"] = "uuid";

            JsonObject unnamedParticipantCount =
                properties["unnamedParticipantCount"]!.AsObject();
            unnamedParticipantCount["minimum"] = 0;
            unnamedParticipantCount["maximum"] =
                ApplicationLimits.MaximumUnnamedParticipants;

            schema["description"] =
                "The named primary contact is implicit. Total participants equal 1 plus "
                + "memberParticipantIds count plus unnamedParticipantCount and must be from 1 "
                + $"through {ApplicationLimits.MaximumTotalParticipants}. Individual signups "
                + "cannot add participants; household and team signups require at least one.";
            schema["x-minimumTotalParticipants"] = 1;
            schema["x-maximumTotalParticipants"] =
                ApplicationLimits.MaximumTotalParticipants;
        }

        private JsonObject CreatePropertySchema(Type type, bool nullable)
        {
            if (TryGetSimpleSchema(type, nullable, out JsonObject? simpleSchema))
            {
                return simpleSchema!;
            }

            if (TryGetEnumerableElementType(type, out Type? elementType))
            {
                JsonObject arraySchema = new()
                {
                    ["type"] = "array",
                    ["items"] = CreatePropertySchema(elementType!, nullable: false),
                };

                return nullable ? AllowNull(arraySchema) : arraySchema;
            }

            return nullable
                ? new JsonObject
                {
                    ["oneOf"] = new JsonArray(
                        CreateSchemaReference(type),
                        new JsonObject
                        {
                            ["type"] = "null",
                        }),
                }
                : CreateSchemaReference(type);
        }

        private static bool IsInlineSchema(Type type) =>
            TryGetSimpleSchema(type, nullable: false, out _);

        private static bool TryGetSimpleSchema(
            Type type,
            bool nullable,
            out JsonObject? schema)
        {
            ArgumentNullException.ThrowIfNull(type);

            Type actualType = Nullable.GetUnderlyingType(type) ?? type;
            schema = actualType switch
            {
                _ when actualType == typeof(string) => CreateTypedSchema("string"),
                _ when actualType == typeof(bool) => CreateTypedSchema("boolean"),
                _ when actualType == typeof(int)
                    || actualType == typeof(short)
                    || actualType == typeof(byte)
                    || actualType == typeof(uint)
                    || actualType == typeof(ushort)
                    || actualType == typeof(sbyte)
                    => CreateTypedSchema("integer", "int32"),
                _ when actualType == typeof(long)
                    || actualType == typeof(ulong)
                    => CreateTypedSchema("integer", "int64"),
                _ when actualType == typeof(float) => CreateTypedSchema("number", "float"),
                _ when actualType == typeof(double) => CreateTypedSchema("number", "double"),
                _ when actualType == typeof(decimal) => CreateTypedSchema("number"),
                _ when actualType == typeof(Guid) => CreateTypedSchema("string", "uuid"),
                _ when actualType == typeof(DateTimeOffset) || actualType == typeof(DateTime)
                    => CreateTypedSchema("string", "date-time"),
                _ when actualType.IsEnum => CreateEnumSchema(actualType),
                _ => null,
            };

            if (schema is null)
            {
                return false;
            }

            if (nullable)
            {
                schema = AllowNull(schema);
            }

            return true;
        }

        private static JsonObject CreateTypedSchema(string type, string? format = null)
        {
            JsonObject schema = new()
            {
                ["type"] = type,
            };
            if (format is not null)
            {
                schema["format"] = format;
            }

            return schema;
        }

        private static JsonObject CreateEnumSchema(Type enumType)
        {
            JsonObject schema = CreateTypedSchema("string");
            JsonArray values = [];
            foreach (string name in Enum.GetNames(enumType))
            {
                values.Add(JsonNamingPolicy.CamelCase.ConvertName(name));
            }

            schema["enum"] = values;
            return schema;
        }

        private static JsonObject AllowNull(JsonObject schema)
        {
            JsonNode? typeNode = schema["type"];
            if (typeNode is JsonValue value
                && value.TryGetValue<string>(out string? typeName)
                && typeName is not null)
            {
                schema["type"] = new JsonArray(typeName, "null");
            }

            return schema;
        }

        private static bool TryGetEnumerableElementType(Type type, out Type? elementType)
        {
            if (type == typeof(string))
            {
                elementType = null;
                return false;
            }

            if (type.IsArray)
            {
                elementType = type.GetElementType();
                return elementType is not null;
            }

            Type? enumerableType = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                ? type
                : type.GetInterfaces()
                    .FirstOrDefault(
                        candidate => candidate.IsGenericType
                            && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            if (enumerableType is null)
            {
                elementType = null;
                return false;
            }

            elementType = enumerableType.GetGenericArguments()[0];
            return true;
        }

        private static string GetSchemaId(Type type)
        {
            if (!type.IsGenericType)
            {
                return type.Name;
            }

            string genericName = type.Name[..type.Name.IndexOf('`')];
            return genericName + "Of" + string.Join(
                "And",
                type.GetGenericArguments().Select(GetSchemaId));
        }

        private static string GetJsonPropertyName(PropertyInfo property)
        {
            JsonPropertyNameAttribute? attribute = property.GetCustomAttribute<JsonPropertyNameAttribute>();
            return attribute?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(property.Name);
        }
    }

    private sealed record OpenApiOperation(string Path, string Method, RouteEndpoint Endpoint);
}
