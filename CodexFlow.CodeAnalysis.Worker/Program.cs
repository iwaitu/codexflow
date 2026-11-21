// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text;
using System.Text.Json;
using CodexFlow.Core.Agents;
using CodexFlow.Core.CodeAnalysis;

using var input = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
using var output = new StreamWriter(Console.OpenStandardOutput(), encoding) { AutoFlush = true };
var next = input.ReadLineAsync();
while (await next is { } line)
{
    var request = JsonSerializer.Deserialize(line, CodeAnalysisWorkerJson.Default.CodeAnalysisRequest);
    if (request is null || request.Version != CodeAnalysisWorkerJson.ProtocolVersion ||
        request.Id <= 0 || request.Code is null || request.ReferenceDirectory is null)
    {
        return 1;
    }

    // The host sends one request at a time. Read ahead to observe a closed pipe
    // even while Roslyn is computing diagnostics.
    next = input.ReadLineAsync();
    var analysis = Task.Run(() => Analyze(request));
    if (await Task.WhenAny(analysis, next) == next)
    {
        return await next is null ? 0 : 1;
    }

    var response = JsonSerializer.Serialize(await analysis, CodeAnalysisWorkerJson.Default.CodeAnalysisResponse);
    await output.WriteLineAsync(response);
}

return 0;

static CodeAnalysisResponse Analyze(CodeAnalysisRequest request)
{
    try
    {
        var diagnostics = RoslynCodeAnalysisService.AnalyzeInProcess(request.Code, request.ReferenceDirectory, CancellationToken.None);
        return new CodeAnalysisResponse(CodeAnalysisWorkerJson.ProtocolVersion, request.Id, diagnostics, null);
    }
    catch (CodeAnalysisUnavailableException ex)
    {
        return new CodeAnalysisResponse(CodeAnalysisWorkerJson.ProtocolVersion, request.Id, null, ex.Message);
    }
}
