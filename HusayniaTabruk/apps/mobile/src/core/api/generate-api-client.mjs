import { mkdir, readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const contractPath = path.resolve(__dirname, "../../../../../docs/api/openapi.json");
const outputPath = path.resolve(__dirname, "./generated/api-contract-client.ts");
const shouldCheck = process.argv.includes("--check");

const operationsToGenerate = [
  {
    path: "/auth/login",
    method: "post",
    operationId: "Login",
    functionName: "login",
    shape: "body",
  },
  {
    path: "/auth/refresh",
    method: "post",
    operationId: "RefreshSession",
    functionName: "refreshSession",
    shape: "body",
  },
  {
    path: "/auth/logout",
    method: "post",
    operationId: "LogoutSession",
    functionName: "logoutSession",
    shape: "body",
  },
  {
    path: "/me",
    method: "get",
    operationId: "GetCurrentActor",
    functionName: "getCurrentActor",
    shape: "options",
  },
  {
    path: "/dates",
    method: "get",
    operationId: "ListOpenServiceDates",
    functionName: "listOpenServiceDates",
    shape: "query",
  },
  {
    path: "/dates/{dateId}",
    method: "get",
    operationId: "GetServiceDate",
    functionName: "getServiceDate",
    shape: "datePath",
  },
  {
    path: "/dates/{dateId}",
    method: "patch",
    operationId: "EditServiceDate",
    functionName: "editServiceDate",
    shape: "datePatch",
  },
  {
    path: "/dates/{dateId}/open",
    method: "post",
    operationId: "OpenServiceDate",
    functionName: "openServiceDate",
    shape: "dateOpen",
  },
  {
    path: "/members/eligible-participants",
    method: "get",
    operationId: "ListEligibleSignupParticipants",
    functionName: "listEligibleSignupParticipants",
    shape: "query",
  },
  {
    path: "/needs/{needId}/signups",
    method: "post",
    operationId: "SubmitSignup",
    functionName: "submitSignup",
    shape: "submit",
  },
  {
    path: "/signups/mine",
    method: "get",
    operationId: "ListMySignups",
    functionName: "listMySignups",
    shape: "query",
  },
  {
    path: "/dates/{dateId}/roster",
    method: "get",
    operationId: "GetManagedRoster",
    functionName: "getManagedRoster",
    shape: "datePathQuery",
  },
  {
    path: "/dates/{dateId}/close",
    method: "post",
    operationId: "CloseServiceDate",
    functionName: "closeServiceDate",
    shape: "dateMutation",
  },
  {
    path: "/dates/{dateId}/cancel",
    method: "post",
    operationId: "CancelServiceDate",
    functionName: "cancelServiceDate",
    shape: "dateMutation",
  },
  {
    path: "/needs/{needId}",
    method: "patch",
    operationId: "EditHelpNeed",
    functionName: "editHelpNeed",
    shape: "needPatch",
  },
  {
    path: "/signups/{signupId}/withdraw",
    method: "post",
    operationId: "WithdrawSignup",
    functionName: "withdrawSignup",
    shape: "signupMutation",
  },
  {
    path: "/signups/{signupId}/approve",
    method: "post",
    operationId: "ApproveSignup",
    functionName: "approveSignup",
    shape: "signupMutation",
  },
  {
    path: "/signups/{signupId}/decline",
    method: "post",
    operationId: "DeclineSignup",
    functionName: "declineSignup",
    shape: "signupMutation",
  },
  {
    path: "/signups/{signupId}/waitlist",
    method: "post",
    operationId: "WaitlistSignup",
    functionName: "waitlistSignup",
    shape: "signupMutation",
  },
  {
    path: "/signups/{signupId}/override",
    method: "post",
    operationId: "OverrideSignupCancellation",
    functionName: "overrideSignupCancellation",
    shape: "signupMutation",
  },
  {
    path: "/needs/{needId}/reassign",
    method: "post",
    operationId: "ReassignWaitlistedSignup",
    functionName: "reassignWaitlistedSignup",
    shape: "needMutation",
  },
  {
    path: "/dates/{dateId}/thread/messages",
    method: "get",
    operationId: "ListThreadMessages",
    functionName: "listThreadMessages",
    shape: "threadQuery",
    successEnvelope: true,
  },
  {
    path: "/dates/{dateId}/thread/messages",
    method: "post",
    operationId: "PostThreadMessage",
    functionName: "postThreadMessage",
    shape: "threadPost",
    successEnvelope: true,
  },
  {
    path: "/dates/{dateId}/thread/messages/{messageId}/report",
    method: "post",
    operationId: "ReportThreadMessage",
    functionName: "reportThreadMessage",
    shape: "threadMessageMutation",
    successEnvelope: true,
  },
  {
    path: "/dates/{dateId}/thread/messages/{messageId}/hide",
    method: "post",
    operationId: "HideThreadMessage",
    functionName: "hideThreadMessage",
    shape: "threadMessageMutation",
    successEnvelope: true,
  },
  {
    path: "/dates/{dateId}/thread/lock",
    method: "post",
    operationId: "LockThread",
    functionName: "lockThread",
    shape: "threadMutation",
    successEnvelope: true,
  },
  {
    path: "/admin/moderation/thread-reads",
    method: "post",
    operationId: "ReadPrivilegedThreadMessages",
    functionName: "readPrivilegedThreadMessages",
    shape: "privilegedThreadRead",
  },
];

const openApiDocument = JSON.parse(await readFile(contractPath, "utf8"));
const generatedContent = renderDocument(openApiDocument);

if (shouldCheck) {
  const existing = await readFile(outputPath, "utf8").catch(() => null);

  if (existing !== generatedContent) {
    console.error(
      `Generated API client drift detected.\nRun: node ${path.relative(process.cwd(), fileURLToPath(import.meta.url))}`,
    );
    process.exitCode = 1;
  } else {
    console.log("Generated API client is up to date.");
  }
} else {
  await mkdir(path.dirname(outputPath), { recursive: true });
  await writeFile(outputPath, generatedContent, "utf8");
  console.log(`Wrote ${path.relative(process.cwd(), outputPath)}`);
}

function renderDocument(document) {
  const serverBasePath = document.servers?.[0]?.url ?? "";
  const schemas = document.components?.schemas ?? {};
  const referencedSchemaNames = new Set();

  const operations = operationsToGenerate.map((definition) =>
    buildOperationDefinition(
      definition,
      document.paths,
      document.components?.parameters ?? {},
      schemas,
      referencedSchemaNames,
      serverBasePath,
    ),
  );

  const sortedSchemaNames = Array.from(referencedSchemaNames).sort((left, right) =>
    toTypeName(left).localeCompare(toTypeName(right), "en"),
  );
  const schemaDeclarations = sortedSchemaNames.map((schemaName) =>
    renderSchemaDeclaration(schemaName, schemas[schemaName]),
  );
  const runtimeSchemas = Object.fromEntries(
    sortedSchemaNames.map((schemaName) => [schemaName, schemas[schemaName]]),
  );

  return `${renderHeader()}
${schemaDeclarations.join("\n\n")}

${renderSharedRuntime(runtimeSchemas)}

${renderClientClass(operations)}
`;
}

function buildOperationDefinition(
  definition,
  paths,
  componentParameters,
  schemas,
  referencedSchemaNames,
  serverBasePath,
) {
  const operation = paths?.[definition.path]?.[definition.method];

  if (!operation) {
    throw new Error(
      `OpenAPI operation missing: ${definition.method.toUpperCase()} ${definition.path}`,
    );
  }

  if (operation.operationId !== definition.operationId) {
    throw new Error(
      `OpenAPI operation ID mismatch for ${definition.method.toUpperCase()} ${definition.path}: expected ${definition.operationId}, received ${operation.operationId ?? "none"}.`,
    );
  }

  const parameters = (operation.parameters ?? []).map((parameter) =>
    resolveParameter(parameter, componentParameters),
  );
  validateOperationParameters(definition, parameters);

  const requestSchema = getJsonSchema(operation.requestBody?.content);
  if (requestSchema) {
    collectReferencedSchemas(requestSchema, schemas, referencedSchemaNames);
  }

  const successResponse = getSuccessResponse(operation.responses ?? {});
  const successSchema = successResponse ? getJsonSchema(successResponse.content) : null;

  if (definition.successEnvelope && !successResponse?.headers?.ETag) {
    throw new Error(
      `OpenAPI success response header missing: ETag for ${definition.method.toUpperCase()} ${definition.path}`,
    );
  }

  if (successSchema) {
    collectReferencedSchemas(successSchema, schemas, referencedSchemaNames);
  }

  for (const response of Object.values(operation.responses ?? {})) {
    const problemSchema = response?.content?.["application/problem+json"]?.schema;
    if (problemSchema) {
      collectReferencedSchemas(problemSchema, schemas, referencedSchemaNames);
    }
  }

  return {
    ...definition,
    method: definition.method.toUpperCase(),
    path: `${serverBasePath}${definition.path}`,
    requestType: requestSchema ? renderSchemaType(requestSchema) : null,
    responseSchemaName: getReferencedSchemaName(successSchema),
    responseType: successSchema ? renderSchemaType(successSchema) : "void",
    returnsVoid: !successSchema || successResponse?.status === "204",
    successEnvelope: Boolean(definition.successEnvelope),
  };
}

function resolveParameter(parameter, componentParameters) {
  if (!parameter?.$ref) {
    return parameter;
  }

  const name = parameter.$ref.split("/").at(-1);
  const resolved = name ? componentParameters[name] : null;
  if (!resolved) {
    throw new Error(`OpenAPI parameter missing: ${parameter.$ref}`);
  }

  return resolved;
}

function validateOperationParameters(definition, parameters) {
  const byName = new Map(parameters.map((parameter) => [parameter.name, parameter]));

  if (
    definition.shape === "datePath" ||
    definition.shape === "datePathQuery" ||
    definition.shape === "datePatch" ||
    definition.shape === "dateMutation" ||
    definition.shape === "dateOpen" ||
    definition.shape === "threadQuery" ||
    definition.shape === "threadPost" ||
    definition.shape === "threadMessageMutation" ||
    definition.shape === "threadMutation"
  ) {
    assertParameter(byName, "dateId", "path", true);
  }

  if (definition.shape === "datePatch") {
    assertParameter(byName, "If-Match", "header", true);
  }

  if (definition.shape === "dateMutation") {
    assertParameter(byName, "Idempotency-Key", "header", true);
    assertParameter(byName, "If-Match", "header", true);
  }

  if (definition.shape === "dateOpen") {
    assertParameter(byName, "Idempotency-Key", "header", true);
  }

  if (definition.shape === "submit") {
    assertParameter(byName, "needId", "path", true);
    assertParameter(byName, "Idempotency-Key", "header", true);
  }

  if (definition.shape === "signupMutation") {
    assertParameter(byName, "signupId", "path", true);
    assertParameter(byName, "Idempotency-Key", "header", true);
    assertParameter(byName, "If-Match", "header", true);
  }

  if (definition.shape === "needMutation") {
    assertParameter(byName, "needId", "path", true);
    assertParameter(byName, "Idempotency-Key", "header", true);
    assertParameter(byName, "If-Match", "header", true);
  }

  if (
    definition.shape === "query" ||
    definition.shape === "datePathQuery" ||
    definition.shape === "threadQuery"
  ) {
    assertParameter(byName, "cursor", "query", false);
    assertParameter(byName, "pageSize", "query", false);
  }

  if (definition.shape === "threadPost") {
    assertParameter(byName, "Idempotency-Key", "header", true);
    assertParameter(byName, "If-Match", "header", true);
  }

  if (
    definition.shape === "threadMessageMutation" ||
    definition.shape === "threadMutation"
  ) {
    assertParameter(byName, "If-Match", "header", true);
  }

  if (definition.shape === "threadMessageMutation") {
    assertParameter(byName, "messageId", "path", true);
  }

  if (definition.shape === "privilegedThreadRead") {
    assertParameter(byName, "X-Step-Up-Token", "header", true);
  }
}

function assertParameter(byName, name, location, required) {
  const parameter = byName.get(name);
  if (
    !parameter ||
    parameter.in !== location ||
    Boolean(parameter.required) !== required
  ) {
    throw new Error(`OpenAPI parameter mismatch: ${name}`);
  }
}

function getSuccessResponse(responses) {
  const entries = Object.entries(responses)
    .filter(([status]) => /^2\d\d$/.test(status))
    .sort(([left], [right]) => Number(left) - Number(right));

  if (entries.length === 0) {
    return null;
  }

  const [status, response] = entries[0];
  return {
    status,
    ...response,
  };
}

function getJsonSchema(content) {
  if (!content) {
    return null;
  }

  return content["application/json"]?.schema ?? content["application/problem+json"]?.schema ?? null;
}

function getReferencedSchemaName(schema) {
  return schema?.$ref ? schema.$ref.split("/").at(-1) ?? null : null;
}

function collectReferencedSchemas(schema, schemas, referencedSchemaNames) {
  if (!schema || typeof schema !== "object") {
    return;
  }

  if (schema.$ref) {
    const name = schema.$ref.split("/").at(-1);
    if (!name) {
      throw new Error(`Invalid $ref value: ${schema.$ref}`);
    }

    if (referencedSchemaNames.has(name)) {
      return;
    }

    referencedSchemaNames.add(name);
    collectReferencedSchemas(schemas[name], schemas, referencedSchemaNames);
    return;
  }

  if (Array.isArray(schema.type)) {
    for (const typeName of schema.type) {
      if (typeName !== "null") {
        collectReferencedSchemas({ ...schema, type: typeName }, schemas, referencedSchemaNames);
      }
    }
    return;
  }

  if (schema.items) {
    collectReferencedSchemas(schema.items, schemas, referencedSchemaNames);
  }

  if (schema.additionalProperties && typeof schema.additionalProperties === "object") {
    collectReferencedSchemas(schema.additionalProperties, schemas, referencedSchemaNames);
  }

  for (const propertySchema of Object.values(schema.properties ?? {})) {
    collectReferencedSchemas(propertySchema, schemas, referencedSchemaNames);
  }
}

function renderSchemaDeclaration(schemaName, schema) {
  const typeName = toTypeName(schemaName);

  if (!schema || typeof schema !== "object") {
    throw new Error(`Unsupported schema declaration for ${schemaName}`);
  }

  if (schema.type === "object" && schema.properties) {
    if (Object.keys(schema.properties).length === 0) {
      return `export type ${typeName} = Record<string, never>;`;
    }

    return `export interface ${typeName} {\n${renderProperties(schema)}\n}`;
  }

  return `export type ${typeName} = ${renderSchemaType(schema)};`;
}

function renderProperties(schema, indent = "  ") {
  const properties = Object.entries(schema.properties ?? {});
  const required = new Set(schema.required ?? []);

  return properties
    .map(([propertyName, propertySchema]) => {
      const optionalMarker = required.has(propertyName) ? "" : "?";
      return `${indent}${renderPropertyName(propertyName)}${optionalMarker}: ${renderSchemaType(propertySchema)};`;
    })
    .join("\n");
}

function renderSchemaType(schema) {
  if (!schema || typeof schema !== "object") {
    return "unknown";
  }

  if (schema.$ref) {
    return toTypeName(schema.$ref.split("/").at(-1));
  }

  if (schema.enum) {
    return schema.enum.map((value) => JSON.stringify(value)).join(" | ");
  }

  if (Array.isArray(schema.type)) {
    return schema.type
      .map((typeName) => renderSchemaType({ ...schema, type: typeName, enum: schema.enum }))
      .filter((value, index, values) => values.indexOf(value) === index)
      .join(" | ");
  }

  switch (schema.type) {
    case "array":
      return `${wrapArrayItem(renderSchemaType(schema.items))}[]`;
    case "boolean":
      return "boolean";
    case "integer":
    case "number":
      return "number";
    case "null":
      return "null";
    case "object":
      if (schema.properties) {
        return `{\n${renderProperties(schema, "    ")}\n  }`;
      }
      if (schema.additionalProperties) {
        return `Record<string, ${renderSchemaType(schema.additionalProperties)}>`;
      }
      return "Record<string, never>";
    case "string":
      return "string";
    default:
      throw new Error(`Unsupported schema type: ${schema.type ?? "undefined"}`);
  }
}

function wrapArrayItem(typeName) {
  return typeName.includes(" | ") ? `(${typeName})` : typeName;
}

function renderPropertyName(propertyName) {
  return isTypeScriptIdentifier(propertyName) ? propertyName : JSON.stringify(propertyName);
}

function isTypeScriptIdentifier(value) {
  return /^[A-Za-z_$][A-Za-z0-9_$]*$/.test(value);
}

function toTypeName(schemaName) {
  const normalized = schemaName
    .replace(/^[^A-Za-z0-9]+/, "")
    .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
    .replace(/[^A-Za-z0-9]+/g, " ")
    .trim();

  return normalized
    .split(/\s+/)
    .filter(Boolean)
    .map((segment) => segment.charAt(0).toUpperCase() + segment.slice(1))
    .join("");
}

function renderHeader() {
  return `/**
 * Generated by src/core/api/generate-api-client.mjs from docs/api/openapi.json.
 * Do not edit this file directly.
 */

export type HttpHeaders = Record<string, string | undefined>;

export interface RequestOptions {
  readonly headers?: HttpHeaders;
  readonly signal?: AbortSignal;
}

export interface CursorQuery {
  readonly cursor?: string;
  readonly pageSize?: number;
}

export interface ApiSuccessEnvelope<T> {
  readonly data: T;
  readonly etag: string;
  readonly version: number;
}`;
}

function renderSharedRuntime(runtimeSchemas) {
  return `export class ApiClientError extends Error {
  public readonly status: number;
  public readonly problem?: ProblemDetails;
  public readonly body?: unknown;
  public readonly retryAfterSeconds?: number;

  public constructor(init: {
    status: number;
    problem?: ProblemDetails;
    body?: unknown;
    message?: string;
    retryAfterSeconds?: number;
  }) {
    super(init.message ?? init.problem?.detail ?? \`The request failed with status \${init.status}.\`);
    this.name = "ApiClientError";
    this.status = init.status;
    this.problem = init.problem;
    this.body = init.body;
    this.retryAfterSeconds = init.retryAfterSeconds;
  }
}

export interface GeneratedApiClientConfig {
  readonly baseUrl: string;
  readonly defaultHeaders?: HttpHeaders | (() => HttpHeaders | Promise<HttpHeaders>);
  readonly fetch?: typeof fetch;
}

type ResolvedHttpHeaders = Record<string, string>;

type RequestDefinition = {
  readonly method: "GET" | "POST" | "PATCH";
  readonly path: string;
  readonly body?: unknown;
  readonly successEnvelope?: boolean;
  readonly headers?: HttpHeaders;
  readonly responseSchemaName?: keyof typeof schemaDefinitions;
  readonly signal?: AbortSignal;
};

type JsonSchema = {
  readonly $ref?: string;
  readonly additionalProperties?: boolean | JsonSchema;
  readonly enum?: readonly unknown[];
  readonly items?: JsonSchema;
  readonly properties?: Readonly<Record<string, JsonSchema>>;
  readonly required?: readonly string[];
  readonly type?: string | readonly string[];
};

const schemaDefinitions = ${JSON.stringify(runtimeSchemas, null, 2)} as const;

async function request<T>(
  config: GeneratedApiClientConfig,
  definition: RequestDefinition,
): Promise<T> {
  const fetcher = config.fetch ?? fetch;
  const response = await fetcher(buildUrl(config.baseUrl, definition.path), {
    method: definition.method,
    headers: (await buildHeaders(
      config.defaultHeaders,
      definition.headers,
      definition.body !== undefined,
    )) as HeadersInit,
    body: definition.body === undefined ? undefined : JSON.stringify(definition.body),
    signal: definition.signal,
  });

  const rawBody = response.status === 204 ? "" : await response.text();
  let parsedBody: unknown;

  try {
    parsedBody = parseResponseBody(rawBody, response.headers.get("content-type"));
  } catch {
    throw new ApiClientError({
      status: response.status,
      body: rawBody,
      message: response.ok
        ? "The server returned a malformed successful response."
        : \`The request failed with status \${response.status}.\`,
      retryAfterSeconds: parseRetryAfter(response.headers.get("retry-after")),
    });
  }

  if (!response.ok) {
    const problem = isProblemDetails(parsedBody) ? parsedBody : undefined;
    throw new ApiClientError({
      status: response.status,
      problem,
      body: parsedBody,
      retryAfterSeconds: parseRetryAfter(response.headers.get("retry-after")),
    });
  }

  if (
    definition.responseSchemaName &&
    !validateSchema(
      parsedBody,
      schemaDefinitions[definition.responseSchemaName] as JsonSchema,
    )
  ) {
    throw new ApiClientError({
      status: response.status,
      body: parsedBody,
      message: "The server returned a malformed successful response.",
    });
  }

  if (definition.successEnvelope) {
    const etag = response.headers.get("etag");
    const version = parseEtagVersion(etag);
    if (!etag || version === undefined) {
      throw new ApiClientError({
        status: response.status,
        body: parsedBody,
        message: "The server returned a malformed successful response.",
      });
    }

    return {
      data: parsedBody,
      etag,
      version,
    } as T;
  }

  return parsedBody as T;
}

async function buildHeaders(
  defaultHeaders: GeneratedApiClientConfig["defaultHeaders"],
  requestHeaders: HttpHeaders | undefined,
  hasJsonBody: boolean,
): Promise<ResolvedHttpHeaders> {
  const resolvedDefaultHeaders =
    typeof defaultHeaders === "function" ? await defaultHeaders() : defaultHeaders ?? {};

  const merged: HttpHeaders = {
    accept: "application/json",
    ...(hasJsonBody ? { "content-type": "application/json" } : {}),
    ...resolvedDefaultHeaders,
    ...requestHeaders,
  };

  return Object.fromEntries(
    Object.entries(merged).filter(([, value]) => typeof value === "string" && value.length > 0),
  ) as ResolvedHttpHeaders;
}

function buildUrl(baseUrl: string, path: string): string {
  const normalizedBaseUrl = baseUrl.replace(/\\/+$/, "");
  return \`\${normalizedBaseUrl}\${path}\`;
}

function appendQuery(path: string, query: CursorQuery): string {
  const values: string[] = [];

  if (query.cursor !== undefined) {
    values.push(\`cursor=\${encodeURIComponent(query.cursor)}\`);
  }

  if (query.pageSize !== undefined) {
    values.push(\`pageSize=\${encodeURIComponent(String(query.pageSize))}\`);
  }

  return values.length === 0 ? path : \`\${path}?\${values.join("&")}\`;
}

function parseResponseBody(body: string, contentType: string | null): unknown {
  if (body.length === 0) {
    return undefined;
  }

  if (contentType?.includes("json")) {
    return JSON.parse(body);
  }

  return body;
}

function parseRetryAfter(value: string | null): number | undefined {
  if (!value) {
    return undefined;
  }

  const seconds = Number(value);
  return Number.isInteger(seconds) && seconds > 0 ? seconds : undefined;
}

function parseEtagVersion(value: string | null): number | undefined {
  const match = value?.match(/^"([0-9]+)"$/);
  if (!match) {
    return undefined;
  }

  const version = Number(match[1]);
  return Number.isSafeInteger(version) ? version : undefined;
}

function isProblemDetails(value: unknown): value is ProblemDetails {
  return (
    typeof value === "object" &&
    value !== null &&
    typeof Reflect.get(value, "type") === "string" &&
    typeof Reflect.get(value, "title") === "string" &&
    typeof Reflect.get(value, "status") === "number" &&
    typeof Reflect.get(value, "code") === "string" &&
    typeof Reflect.get(value, "detail") === "string" &&
    typeof Reflect.get(value, "traceId") === "string"
  );
}

function validateSchema(value: unknown, schema: JsonSchema): boolean {
  if (schema.$ref) {
    const name = schema.$ref.split("/").at(-1) as keyof typeof schemaDefinitions | undefined;
    if (!name) {
      return false;
    }

    return validateSchema(value, schemaDefinitions[name] as JsonSchema);
  }

  if (schema.enum && !schema.enum.includes(value)) {
    return false;
  }

  if (Array.isArray(schema.type)) {
    return schema.type.some((typeName) =>
      validateSchema(value, { ...schema, type: typeName }),
    );
  }

  switch (schema.type) {
    case "null":
      return value === null;
    case "string":
      return typeof value === "string";
    case "integer":
      return typeof value === "number" && Number.isInteger(value);
    case "number":
      return typeof value === "number" && Number.isFinite(value);
    case "boolean":
      return typeof value === "boolean";
    case "array":
      return Array.isArray(value) && value.every((item) => validateSchema(item, schema.items ?? {}));
    case "object": {
      if (typeof value !== "object" || value === null || Array.isArray(value)) {
        return false;
      }

      const record = value as Record<string, unknown>;
      if ((schema.required ?? []).some((propertyName) => !(propertyName in record))) {
        return false;
      }

      for (const [propertyName, propertySchema] of Object.entries(schema.properties ?? {})) {
        if (propertyName in record && !validateSchema(record[propertyName], propertySchema)) {
          return false;
        }
      }

      if (typeof schema.additionalProperties === "object") {
        const known = new Set(Object.keys(schema.properties ?? {}));
        for (const [propertyName, propertyValue] of Object.entries(record)) {
          if (!known.has(propertyName) && !validateSchema(propertyValue, schema.additionalProperties)) {
            return false;
          }
        }
      }

      return true;
    }
    default:
      return true;
  }
}`;
}

function renderClientClass(operations) {
  return `export class GeneratedApiClient {
  private readonly config: GeneratedApiClientConfig;

  public constructor(config: GeneratedApiClientConfig) {
    this.config = config;
  }

${operations.map((operation) => renderOperation(operation)).join("\n\n")}
}`;
}

function renderOperation(operation) {
  const dataType = operation.returnsVoid ? "void" : operation.responseType;
  const returnType = operation.successEnvelope
    ? `ApiSuccessEnvelope<${dataType}>`
    : dataType;
  const responseSchema = operation.responseSchemaName
    ? `,\n      responseSchemaName: "${operation.responseSchemaName}"`
    : "";
  const successEnvelope = operation.successEnvelope
    ? ",\n      successEnvelope: true"
    : "";

  switch (operation.shape) {
    case "body":
      return `  public async ${operation.functionName}(body: ${operation.requestType}, options: RequestOptions = {}): Promise<${returnType}> {
    return request<${returnType}>(this.config, {
      method: "${operation.method}",
      path: "${operation.path}",
      body,
      headers: options.headers${responseSchema},
      signal: options.signal,
    });
  }`;
    case "options":
      return `  public async ${operation.functionName}(options: RequestOptions = {}): Promise<${returnType}> {
    return request<${returnType}>(this.config, {
      method: "${operation.method}",
      path: "${operation.path}",
      headers: options.headers${responseSchema},
      signal: options.signal,
    });
  }`;
    case "query":
      return `  public async ${operation.functionName}(query: CursorQuery = {}, options: RequestOptions = {}): Promise<${returnType}> {
    return request<${returnType}>(this.config, {
      method: "${operation.method}",
      path: appendQuery("${operation.path}", query),
      headers: options.headers${responseSchema},
      signal: options.signal,
    });
  }`;
    case "datePath":
      return `  public async ${operation.functionName}(dateId: string, options: RequestOptions = {}): Promise<${returnType}> {
    return request<${returnType}>(this.config, {
      method: "${operation.method}",
      path: "${operation.path}".replace("{dateId}", encodeURIComponent(dateId)),
      headers: options.headers${responseSchema},
      signal: options.signal,
    });
  }`;
    case "datePathQuery":
      return `  public async ${operation.functionName}(dateId: string, query: CursorQuery = {}, options: RequestOptions = {}): Promise<${returnType}> {
    return request<${returnType}>(this.config, {
      method: "${operation.method}",
      path: appendQuery("${operation.path}".replace("{dateId}", encodeURIComponent(dateId)), query),
      headers: options.headers${responseSchema},
      signal: options.signal,
    });
  }`;
    case "datePatch":
      return `  public async ${operation.functionName}(dateId: string, expectedVersion: number, body: ${operation.requestType}, options: RequestOptions = {}): Promise<${returnType}> {
    return request<${returnType}>(this.config, {
      method: "${operation.method}",
      path: "${operation.path}".replace("{dateId}", encodeURIComponent(dateId)),
      headers: {
        ...options.headers,
        "If-Match": \`"\${expectedVersion}"\`,
      }${responseSchema},
      body,
      signal: options.signal,
    });
  }`;
    case "dateMutation":
      return `  public async ${operation.functionName}(dateId: string, idempotencyKey: string, expectedVersion: number, body: ${operation.requestType}, options: RequestOptions = {}): Promise<${returnType}> {
    return request<${returnType}>(this.config, {
      method: "${operation.method}",
      path: "${operation.path}".replace("{dateId}", encodeURIComponent(dateId)),
      headers: {
        ...options.headers,
        "Idempotency-Key": idempotencyKey,
        "If-Match": \`"\${expectedVersion}"\`,
      }${responseSchema},
      body,
      signal: options.signal,
    });
  }`;
    case "dateOpen":
      return `  public async ${operation.functionName}(dateId: string, idempotencyKey: string, options: RequestOptions = {}): Promise<${returnType}> {
    return request<${returnType}>(this.config, {
      method: "${operation.method}",
      path: "${operation.path}".replace("{dateId}", encodeURIComponent(dateId)),
      headers: {
        ...options.headers,
        "Idempotency-Key": idempotencyKey,
      }${responseSchema},
      signal: options.signal,
    });
  }`;
    case "submit":
      return `  public async ${operation.functionName}(needId: string, idempotencyKey: string, body: ${operation.requestType}, options: RequestOptions = {}): Promise<${returnType}> {
    return request<${returnType}>(this.config, {
      method: "${operation.method}",
      path: "${operation.path}".replace("{needId}", encodeURIComponent(needId)),
      body,
      headers: {
        ...options.headers,
        "Idempotency-Key": idempotencyKey,
      }${responseSchema},
      signal: options.signal,
    });
  }`;
    case "signupMutation":
      return `  public async ${operation.functionName}(signupId: string, idempotencyKey: string, expectedVersion: number, body: ${operation.requestType}, options: RequestOptions = {}): Promise<${returnType}> {
    return request<${returnType}>(this.config, {
      method: "${operation.method}",
      path: "${operation.path}".replace("{signupId}", encodeURIComponent(signupId)),
      headers: {
        ...options.headers,
        "Idempotency-Key": idempotencyKey,
        "If-Match": \`"\${expectedVersion}"\`,
      }${responseSchema},
      body,
      signal: options.signal,
    });
  }`;
    case "needMutation":
      return `  public async ${operation.functionName}(needId: string, idempotencyKey: string, expectedVersion: number, body: ${operation.requestType}, options: RequestOptions = {}): Promise<${returnType}> {
    return request<${returnType}>(this.config, {
      method: "${operation.method}",
      path: "${operation.path}".replace("{needId}", encodeURIComponent(needId)),
      headers: {
        ...options.headers,
        "Idempotency-Key": idempotencyKey,
        "If-Match": \`"\${expectedVersion}"\`,
      }${responseSchema},
      body,
      signal: options.signal,
    });
  }`;
    case "threadQuery":
      return `  public async ${operation.functionName}(dateId: string, query: CursorQuery = {}, options: RequestOptions = {}): Promise<${returnType}> {
    return request<${returnType}>(this.config, {
      method: "${operation.method}",
      path: appendQuery("${operation.path}".replace("{dateId}", encodeURIComponent(dateId)), query),
      headers: options.headers${responseSchema}${successEnvelope},
      signal: options.signal,
    });
  }`;
    case "threadPost":
      return `  public async ${operation.functionName}(dateId: string, idempotencyKey: string, expectedVersion: number, body: ${operation.requestType}, options: RequestOptions = {}): Promise<${returnType}> {
    return request<${returnType}>(this.config, {
      method: "${operation.method}",
      path: "${operation.path}".replace("{dateId}", encodeURIComponent(dateId)),
      headers: {
        ...options.headers,
        "Idempotency-Key": idempotencyKey,
        "If-Match": \`"\${expectedVersion}"\`,
      }${responseSchema}${successEnvelope},
      body,
      signal: options.signal,
    });
  }`;
    case "threadMessageMutation":
      return `  public async ${operation.functionName}(dateId: string, messageId: string, expectedVersion: number, body: ${operation.requestType}, options: RequestOptions = {}): Promise<${returnType}> {
    return request<${returnType}>(this.config, {
      method: "${operation.method}",
      path: "${operation.path}"
        .replace("{dateId}", encodeURIComponent(dateId))
        .replace("{messageId}", encodeURIComponent(messageId)),
      headers: {
        ...options.headers,
        "If-Match": \`"\${expectedVersion}"\`,
      }${responseSchema}${successEnvelope},
      body,
      signal: options.signal,
    });
  }`;
    case "threadMutation":
      return `  public async ${operation.functionName}(dateId: string, expectedVersion: number, body: ${operation.requestType}, options: RequestOptions = {}): Promise<${returnType}> {
    return request<${returnType}>(this.config, {
      method: "${operation.method}",
      path: "${operation.path}".replace("{dateId}", encodeURIComponent(dateId)),
      headers: {
        ...options.headers,
        "If-Match": \`"\${expectedVersion}"\`,
      }${responseSchema}${successEnvelope},
      body,
      signal: options.signal,
    });
  }`;
    case "privilegedThreadRead":
      return `  public async ${operation.functionName}(stepUpToken: string, body: ${operation.requestType}, options: RequestOptions = {}): Promise<${returnType}> {
    return request<${returnType}>(this.config, {
      method: "${operation.method}",
      path: "${operation.path}",
      headers: {
        ...options.headers,
        "X-Step-Up-Token": stepUpToken,
      }${responseSchema},
      body,
      signal: options.signal,
    });
  }`;
    case "needPatch":
      return `  public async ${operation.functionName}(needId: string, expectedVersion: number, body: ${operation.requestType}, options: RequestOptions = {}): Promise<${returnType}> {
    return request<${returnType}>(this.config, {
      method: "${operation.method}",
      path: "${operation.path}".replace("{needId}", encodeURIComponent(needId)),
      headers: {
        ...options.headers,
        "If-Match": \`"\${expectedVersion}"\`,
      }${responseSchema},
      body,
      signal: options.signal,
    });
  }`;
    default:
      throw new Error(`Unsupported operation shape: ${operation.shape}`);
  }
}
