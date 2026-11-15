// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;
using CodexFlow.QueryRuntime.Engine.V2;
using CodexFlow.QueryRuntime.Protocol;

namespace CodexFlow.Core.Runtime;

/// <summary>Separates a turn's immutable executable catalog from per-step visibility.</summary>
internal sealed class FrozenRuntimeToolCatalog : IRuntimeToolCatalogSelector
{
    private readonly QueryRuntimeRequest _request;
    private readonly Dictionary<string, AIFunction> _frozen;

    public FrozenRuntimeToolCatalog(QueryRuntimeRequest request, AIFunction[] tools)
    {
        _request = request;
        _frozen = tools.ToDictionary(tool => tool.Name, StringComparer.OrdinalIgnoreCase);
        ExecutionTools = tools.Select(tool => (AIFunction)new BoundFunction(tool, this)).ToArray();
    }

    public AIFunction[] ExecutionTools { get; }

    private Dictionary<string, AIFunction> GetCurrentTools()
    {
        var visible = _request.AvailableToolsProvider?.Invoke() ?? _request.AvailableTools ?? [];
        var result = new Dictionary<string, AIFunction>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in visible)
        {
            // Tools outside the frozen snapshot are never executable this turn: omit them (deferred to next turn).
            if (!_frozen.TryGetValue(candidate.Name, out var frozen))
                continue;
            var adapted = new QreV2AdapterUtilities.TrustedRuntimeArgumentsAIFunction(candidate, _request.Session);
            // Descriptions are presentation text (e.g. start_next_task embeds the next task) and are frozen at turn
            // start; only a name or schema change is a contract violation.
            if (!string.Equals(frozen.Name, candidate.Name, StringComparison.Ordinal) ||
                !JsonElement.DeepEquals(frozen.JsonSchema, adapted.JsonSchema))
                throw new InvalidOperationException($"qre_v2_frozen_catalog_drift: '{candidate.Name}' is not the frozen tool contract.");
            if (!result.TryAdd(candidate.Name, adapted))
                throw new InvalidOperationException($"qre_v2_frozen_catalog_duplicate: '{candidate.Name}'.");
        }
        return result;
    }

    public IReadOnlyList<RuntimeToolDescriptor> SelectTools(
        PreparedRuntimeContext context, IReadOnlyList<RuntimeToolDescriptor> frozenCatalog, int stepIndex)
    {
        var visible = GetCurrentTools();
        return frozenCatalog.Where(tool => visible.ContainsKey(tool.CanonicalName)).ToArray();
    }

    public void Observe(RuntimeToolCall call, RuntimeToolResult result) { }

    private sealed class BoundFunction(AIFunction frozen, FrozenRuntimeToolCatalog owner) : AIFunction
    {
        public override string Name => frozen.Name;
        public override string Description => frozen.Description;
        public override JsonElement JsonSchema => frozen.JsonSchema;
        public override JsonElement? ReturnJsonSchema => frozen.ReturnJsonSchema;
        public override JsonSerializerOptions JsonSerializerOptions => frozen.JsonSerializerOptions;
        public override MethodInfo? UnderlyingMethod => frozen.UnderlyingMethod;
        public override IReadOnlyDictionary<string, object?> AdditionalProperties => frozen.AdditionalProperties;

        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            // Bind current trusted session context, but never substitute a new schema or expose a hidden tool.
            if (!owner.GetCurrentTools().TryGetValue(Name, out var current))
                throw new InvalidOperationException($"qre_v2_frozen_tool_not_visible: '{Name}'.");
            return await current.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
        }
    }
}
