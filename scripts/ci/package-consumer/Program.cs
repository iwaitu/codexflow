using CodexFlow.Contracts;
using CodexFlow.Controllers;
using CodexFlow.Storage.Sqlite;
using Microsoft.Extensions.DependencyInjection;

// Touches one public type from each package a consumer is expected to pull in, so a package that restores but is empty or broken fails the build.
var services = new ServiceCollection();
services.AddCodexFlowSqliteStorage(Path.Combine(Path.GetTempPath(), "codexflow-package-consumer.db"));
Console.WriteLine($"contracts={typeof(ArchiveFileDto).Assembly.GetName().Version} controllers={typeof(CodexGatewayController).Assembly.GetName().Name} services={services.Count}");
