// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CodexFlow.Core.BusinessSkills;

/// <summary>
/// 提供 Skill JSON v1 的规范 JSON Schema 与兼容严格结构化输出的模型 Schema。
/// 对齐 DefaultCodexPlanner 中已知的模型限制（如移除 maxItems/minItems、保持 object 根）。
/// </summary>
public static class SkillSchemaProvider
{
    private static readonly Lazy<JsonElement> CanonicalSchemaLazy = new(CreateCanonicalSchema);
    private static readonly Lazy<JsonElement> StructuredOutputSchemaLazy = new(CreateStructuredOutputSchema);

    public static JsonElement CanonicalValidationSchema => CanonicalSchemaLazy.Value;

    public static JsonElement StructuredOutputSchema => StructuredOutputSchemaLazy.Value;

    private static JsonElement CreateCanonicalSchema()
    {
        const string rawSchema = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "required": [
            "schemaVersion",
            "skillId",
            "version",
            "name",
            "description",
            "pluginId",
            "connectionBinding",
            "catalogSnapshotId",
            "selectedCapabilityIds",
            "usedCapabilityIds",
            "inputs",
            "outputs",
            "steps",
            "successCriteria",
            "dataPolicy",
            "budgets"
          ],
          "additionalProperties": false,
          "properties": {
            "schemaVersion": { "type": "integer", "enum": [1] },
            "skillId": { "type": "string" },
            "version": { "type": "integer" },
            "name": { "type": "string" },
            "description": { "type": "string" },
            "pluginId": { "type": "string" },
            "catalogSnapshotId": { "type": "string" },
            "connectionBinding": {
              "type": "object",
              "required": ["connectionId", "environment", "connectionRevision", "credentialRevision", "selectedServerUrl"],
              "additionalProperties": false,
              "properties": {
                "connectionId": { "type": "string" },
                "environment": { "type": "string" },
                "connectionRevision": { "type": "integer" },
                "credentialRevision": { "type": "string" },
                "selectedServerUrl": { "type": "string" }
              }
            },
            "selectedCapabilityIds": {
              "type": "array",
              "minItems": 1,
              "items": { "type": "string" }
            },
            "usedCapabilityIds": {
              "type": "array",
              "minItems": 1,
              "maxItems": 20,
              "items": { "type": "string" }
            },
            "inputs": {
              "type": "object",
              "required": ["type", "required"],
              "additionalProperties": true,
              "properties": {
                "type": { "type": "string", "enum": ["object"] },
                "required": { "type": "array", "items": { "type": "string" } }
              }
            },
            "outputs": {
              "type": "object",
              "required": ["type", "fields"],
              "additionalProperties": false,
              "properties": {
                "type": { "type": "string", "enum": ["object"] },
                "fields": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "required": ["name", "type"],
                    "additionalProperties": false,
                    "properties": {
                      "name": { "type": "string" },
                      "type": { "type": "string" },
                      "description": { "type": "string" }
                    }
                  }
                }
              }
            },
            "steps": {
              "type": "array",
              "minItems": 1,
              "maxItems": 30,
              "items": {
                "type": "object",
                "required": ["id", "type"],
                "additionalProperties": true,
                "properties": {
                  "id": { "type": "string" },
                  "title": { "type": "string" },
                  "type": {
                    "type": "string",
                    "enum": ["tool", "transform", "branch", "foreach", "generateText", "approval", "output"]
                  }
                }
              }
            },
            "successCriteria": {
              "type": "object",
              "required": ["requiredSteps", "requireCompletePagination", "failOnPartialData"],
              "additionalProperties": false,
              "properties": {
                "requiredSteps": { "type": "array", "items": { "type": "string" } },
                "requireCompletePagination": { "type": "boolean" },
                "failOnPartialData": { "type": "boolean" }
              }
            },
            "dataPolicy": {
              "type": "object",
              "required": ["allowedModelFields", "includeSanitizedSamplesForGeneration", "redactLogsAndExports"],
              "additionalProperties": false,
              "properties": {
                "allowedModelFields": { "type": "array", "items": { "type": "string" } },
                "includeSanitizedSamplesForGeneration": { "type": "boolean" },
                "redactLogsAndExports": { "type": "boolean" }
              }
            },
            "budgets": {
              "type": "object",
              "required": ["maxExternalRequests", "maxReadRecords", "maxJoinOutputRows", "maxWriteBatchItems", "maxReadOnlyRetries", "requestTimeoutSeconds"],
              "additionalProperties": false,
              "properties": {
                "maxExternalRequests": { "type": "integer" },
                "maxReadRecords": { "type": "integer" },
                "maxJoinOutputRows": { "type": "integer" },
                "maxWriteBatchItems": { "type": "integer" },
                "maxReadOnlyRetries": { "type": "integer" },
                "requestTimeoutSeconds": { "type": "integer" }
              }
            }
          }
        }
        """;

        using var document = JsonDocument.Parse(rawSchema);
        return document.RootElement.Clone();
    }

    private static JsonElement CreateStructuredOutputSchema()
    {
        var schema = JObject.Parse(CanonicalSchemaLazy.Value.GetRawText());
        schema.Remove("$schema");

        // 与 DefaultCodexPlanner.CreatePlanStructuredOutputJsonSchema 保持一致：
        // 严格结构化输出提供商拒绝数组 minItems/maxItems。
        foreach (var node in new[] { schema }.Concat(schema.Descendants().OfType<JObject>()).ToList())
        {
            node.Remove("minItems");
            node.Remove("maxItems");
        }

        using var document = JsonDocument.Parse(schema.ToString(Formatting.None));
        return document.RootElement.Clone();
    }
}
