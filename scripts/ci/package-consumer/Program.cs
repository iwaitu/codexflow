using System.Text.Json;
using CodexFlow.Contracts;
using CodexFlow.Gateway.Contracts;
using CodexFlow.Gateway.Http;
using CodexFlow.Storage.EntityFramework;
using CodexFlow.Storage.Sqlite;
using Microsoft.Extensions.DependencyInjection;

// Touches one public type from each package a consumer is expected to pull in, so a package that restores but is empty or broken fails the build.
var services = new ServiceCollection();
services.AddCodexFlowSqliteStorage(Path.Combine(Path.GetTempPath(), "codexflow-package-consumer.db"));
services.AddCodexFlowGateway();
Console.WriteLine($"contracts={typeof(ArchiveFileDto).Assembly.GetName().Version} gateway={typeof(GatewayHttpExtensions).Assembly.GetName().Name} services={services.Count}");
Console.WriteLine($"managed-storage={typeof(EntityFrameworkStorage).Assembly.GetName().Name}");

var (openApiJson, sseSchemaJson) = await GatewayOpenApiExtensions.GenerateContractsAsync();
using var openApi = JsonDocument.Parse(openApiJson);
using var sseSchema = JsonDocument.Parse(sseSchemaJson);
var paths = openApi.RootElement.GetProperty("paths");
_ = paths.GetProperty("/api/gateway/chat").GetProperty("post");
_ = paths.GetProperty("/api/jobs/{jobId}/history").GetProperty("get");
_ = sseSchema.RootElement.GetProperty("oneOf");
Console.WriteLine("Installed gateway packages served the chat, job history and SSE contracts.");
