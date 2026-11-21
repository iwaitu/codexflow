// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using System.Text.Json.Serialization;
using CodexFlow.Contracts.Browser;
using CodexFlow.Contracts.BusinessSkills;
using CodexFlow.Controllers;

namespace CodexFlow.Gateway.Http;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(ActivateSkillVersionRequest))]
[JsonSerializable(typeof(BrowserApiErrorDto))]
[JsonSerializable(typeof(BrowserSessionCloseResponse))]
[JsonSerializable(typeof(BrowserSessionStateDto))]
[JsonSerializable(typeof(BrowserSettingsDto))]
[JsonSerializable(typeof(BrowserTakeoverCompleteRequest))]
[JsonSerializable(typeof(BrowserTakeoverResponse))]
[JsonSerializable(typeof(BrowserTakeoverStartRequest))]
[JsonSerializable(typeof(BusinessPluginDto))]
[JsonSerializable(typeof(BusinessSkillErrorDto))]
[JsonSerializable(typeof(BusinessSkillRetentionPruneRequest))]
[JsonSerializable(typeof(BusinessSkillRetentionPruneResultDto))]
[JsonSerializable(typeof(CapabilityDetailDto))]
[JsonSerializable(typeof(CapabilityPageResponseDto))]
[JsonSerializable(typeof(CapabilitySummaryDto))]
[JsonSerializable(typeof(CreateBusinessPluginRequest))]
[JsonSerializable(typeof(CreateSkillDraftRequest))]
[JsonSerializable(typeof(CredentialRefSummaryDto))]
[JsonSerializable(typeof(DeleteCredentialResponseDto))]
[JsonSerializable(typeof(DisablePluginResponseDto))]
[JsonSerializable(typeof(DiscoverPluginRequest))]
[JsonSerializable(typeof(DiscoverPluginResultDto))]
[JsonSerializable(typeof(GenerateSkillCandidateRequest))]
[JsonSerializable(typeof(GenerateSkillCandidateResultDto))]
[JsonSerializable(typeof(IReadOnlyList<BusinessPluginDto>))]
[JsonSerializable(typeof(IReadOnlyList<PublishedSkillDto>))]
[JsonSerializable(typeof(IReadOnlyList<SkillRunDto>))]
[JsonSerializable(typeof(PluginOperationStatusDto))]
[JsonSerializable(typeof(PublishSkillDraftRequest))]
[JsonSerializable(typeof(PublishedSkillDto))]
[JsonSerializable(typeof(PutDraftSelectionRequest))]
[JsonSerializable(typeof(SetPluginCredentialRequest))]
[JsonSerializable(typeof(SkillDraftDto))]
[JsonSerializable(typeof(SkillMarkdownPreviewDto))]
[JsonSerializable(typeof(SkillRunDto))]
[JsonSerializable(typeof(StartSkillRunRequest))]
[JsonSerializable(typeof(UpdateBrowserSettingsRequest))]
[JsonSerializable(typeof(UpdateCapabilityRiskRequest))]
[JsonSerializable(typeof(UpdatePluginConnectionRequest))]
[JsonSerializable(typeof(UpdateSkillDraftRequest))]
internal sealed partial class BusinessGatewayJsonContext : JsonSerializerContext;
