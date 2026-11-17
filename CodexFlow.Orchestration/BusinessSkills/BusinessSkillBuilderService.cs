// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexFlow.Contracts.BusinessSkills;
using CodexFlow.Core.BusinessSkills;
using CodexFlow.Domain.BusinessSkills;
using CodexFlow.Orchestration.Jobs;
using Microsoft.EntityFrameworkCore;

namespace CodexFlow.Orchestration.BusinessSkills;

public interface IBusinessSkillBuilderService
{
    Task<BusinessServiceResult<SkillDraftDto>> CreateDraftAsync(CreateSkillDraftRequest request, CancellationToken ct = default);
    Task<BusinessServiceResult<SkillDraftDto>> GetDraftAsync(string draftId, CancellationToken ct = default);
    Task<BusinessServiceResult<SkillDraftDto>> UpdateDraftAsync(string draftId, UpdateSkillDraftRequest request, CancellationToken ct = default);
    Task<BusinessServiceResult<SkillDraftDto>> UpdateSelectionAsync(string draftId, PutDraftSelectionRequest request, CancellationToken ct = default);
    Task<BusinessServiceResult<GenerateSkillCandidateResultDto>> GenerateCandidateAsync(string draftId, GenerateSkillCandidateRequest request, CancellationToken ct = default);
    Task<BusinessServiceResult<SkillDraftDto>> ValidateDraftAsync(string draftId, CancellationToken ct = default);
    Task<BusinessServiceResult<PublishedSkillDto>> PublishDraftAsync(string draftId, PublishSkillDraftRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<PublishedSkillDto>> ListPublishedSkillsAsync(CancellationToken ct = default);
    Task<BusinessServiceResult<PublishedSkillDto>> GetPublishedSkillAsync(string skillId, CancellationToken ct = default);
    Task<BusinessServiceResult<SkillMarkdownPreviewDto>> GetPublishedSkillMarkdownAsync(string skillId, int? version = null, CancellationToken ct = default);
    Task<BusinessServiceResult<PublishedSkillDto>> ActivateVersionAsync(string skillId, ActivateSkillVersionRequest request, CancellationToken ct = default);
}

/// <summary>
/// P3 业务 Skill 构建器服务：管理草稿乐观并发编辑、选择集冻结、受控生成、静态/上下文校验与不可变版本事务发布。
/// </summary>
public sealed class BusinessSkillBuilderService : IBusinessSkillBuilderService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IJobDbContext _db;
    private readonly IBusinessSkillGenerator _generator;

    public BusinessSkillBuilderService(
        IJobDbContext db,
        IBusinessSkillGenerator? generator = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
        _generator = generator ?? new StructuredBusinessSkillGenerator();
    }

    public async Task<BusinessServiceResult<SkillDraftDto>> CreateDraftAsync(CreateSkillDraftRequest request, CancellationToken ct = default)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.PluginId) || string.IsNullOrWhiteSpace(request.Name))
        {
            return BusinessServiceResult<SkillDraftDto>.Fail(422, BusinessSkillErrorCodes.ValidationFailed, "pluginId and name are required.");
        }

        var plugin = await _db.BusinessPlugins.AsNoTracking().FirstOrDefaultAsync(p => p.Id == request.PluginId, ct).ConfigureAwait(false);
        if (plugin is null)
        {
            return BusinessServiceResult<SkillDraftDto>.Fail(404, BusinessSkillErrorCodes.ConnectionUnavailable, $"Plugin '{request.PluginId}' not found.");
        }

        var snapshotId = string.IsNullOrWhiteSpace(request.CatalogSnapshotId)
            ? plugin.ActiveSnapshotId
            : request.CatalogSnapshotId.Trim();
        if (string.IsNullOrWhiteSpace(snapshotId))
        {
            return BusinessServiceResult<SkillDraftDto>.Fail(422, BusinessSkillErrorCodes.CatalogIncomplete, "Plugin has no discovered catalog snapshot yet.");
        }

        var snapshot = await _db.CatalogSnapshots.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == snapshotId && s.PluginId == plugin.Id, ct)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            return BusinessServiceResult<SkillDraftDto>.Fail(404, BusinessSkillErrorCodes.CatalogIncomplete, $"Snapshot '{snapshotId}' not found for plugin '{plugin.Id}'.");
        }

        var selectedIds = request.SelectedCapabilityIds is { Count: > 0 }
            ? request.SelectedCapabilityIds.Distinct(StringComparer.Ordinal).ToArray()
            : [];

        if (selectedIds.Length > 0)
        {
            var err = await ValidateSelectedIdsExistInSnapshotAsync(snapshotId, selectedIds, ct).ConfigureAwait(false);
            if (err is not null)
            {
                return BusinessServiceResult<SkillDraftDto>.Fail(422, BusinessSkillErrorCodes.SelectionViolation, err);
            }
        }

        var now = DateTime.UtcNow;
        var entity = new SkillDraftEntity
        {
            Id = $"skd-{Guid.NewGuid():N}"[..16],
            PluginId = plugin.Id,
            CatalogSnapshotId = snapshotId,
            Revision = 1,
            Name = request.Name.Trim(),
            BusinessGoal = request.BusinessGoal?.Trim() ?? string.Empty,
            SelectedCapabilityIdsJson = JsonSerializer.Serialize(selectedIds, JsonOptions),
            ClarificationQuestionsJson = "[]",
            ValidationErrorsJson = "[]",
            NeedsRevalidation = false,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        _db.SkillDrafts.Add(entity);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return BusinessServiceResult<SkillDraftDto>.Ok(MapDraftDto(entity), 201);
    }

    public async Task<BusinessServiceResult<SkillDraftDto>> GetDraftAsync(string draftId, CancellationToken ct = default)
    {
        var entity = await _db.SkillDrafts.AsNoTracking().FirstOrDefaultAsync(d => d.Id == draftId, ct).ConfigureAwait(false);
        return entity is null
            ? BusinessServiceResult<SkillDraftDto>.Fail(404, BusinessSkillErrorCodes.ValidationFailed, $"Skill draft '{draftId}' not found.")
            : BusinessServiceResult<SkillDraftDto>.Ok(MapDraftDto(entity));
    }

    public async Task<BusinessServiceResult<SkillDraftDto>> UpdateDraftAsync(
        string draftId,
        UpdateSkillDraftRequest request,
        CancellationToken ct = default)
    {
        if (request is null || request.ExpectedRevision <= 0)
        {
            return BusinessServiceResult<SkillDraftDto>.Fail(422, BusinessSkillErrorCodes.ValidationFailed, "expectedRevision > 0 is required.");
        }

        var entity = await _db.SkillDrafts.FirstOrDefaultAsync(d => d.Id == draftId, ct).ConfigureAwait(false);
        if (entity is null)
        {
            return BusinessServiceResult<SkillDraftDto>.Fail(404, BusinessSkillErrorCodes.ValidationFailed, $"Skill draft '{draftId}' not found.");
        }

        if (entity.Revision != request.ExpectedRevision)
        {
            return BusinessServiceResult<SkillDraftDto>.Fail(409, BusinessSkillErrorCodes.CatalogChanged,
                $"Draft revision conflict: expected {request.ExpectedRevision}, current revision is {entity.Revision}.");
        }

        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            entity.Name = request.Name.Trim();
        }

        if (request.BusinessGoal is not null)
        {
            entity.BusinessGoal = request.BusinessGoal.Trim();
        }

        if (request.CandidateDefinition is not null)
        {
            // 手工或向导修订候选定义时，旧验证与试运行证据必须立即作废，防止跳过重校验直接发布
            var cleared = request.CandidateDefinition with
            {
                ValidationEvidence = new SkillValidationEvidenceDto()
            };
            var hash = SkillDefinitionV1ValidatorAndCanonicalizer.ComputeContentHash(cleared);
            entity.CandidateDefinitionJson = JsonSerializer.Serialize(cleared with { ContentHash = hash }, JsonOptions);
            entity.ValidationErrorsJson = "[]";
            entity.NeedsRevalidation = true;
        }

        entity.Revision += 1;
        entity.UpdatedAtUtc = DateTime.UtcNow;

        try
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return BusinessServiceResult<SkillDraftDto>.Fail(409, BusinessSkillErrorCodes.CatalogChanged,
                "Concurrent draft modification detected; reload latest draft state.");
        }

        return BusinessServiceResult<SkillDraftDto>.Ok(MapDraftDto(entity));
    }

    public async Task<BusinessServiceResult<SkillDraftDto>> UpdateSelectionAsync(
        string draftId,
        PutDraftSelectionRequest request,
        CancellationToken ct = default)
    {
        if (request is null || request.ExpectedRevision <= 0 || string.IsNullOrWhiteSpace(request.CatalogSnapshotId))
        {
            return BusinessServiceResult<SkillDraftDto>.Fail(422, BusinessSkillErrorCodes.ValidationFailed, "expectedRevision and catalogSnapshotId are required.");
        }

        var entity = await _db.SkillDrafts.FirstOrDefaultAsync(d => d.Id == draftId, ct).ConfigureAwait(false);
        if (entity is null)
        {
            return BusinessServiceResult<SkillDraftDto>.Fail(404, BusinessSkillErrorCodes.ValidationFailed, $"Skill draft '{draftId}' not found.");
        }

        if (entity.Revision != request.ExpectedRevision)
        {
            return BusinessServiceResult<SkillDraftDto>.Fail(409, BusinessSkillErrorCodes.CatalogChanged,
                $"Draft revision conflict: expected {request.ExpectedRevision}, current revision is {entity.Revision}.");
        }

        var snap = await _db.CatalogSnapshots.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == request.CatalogSnapshotId && s.PluginId == entity.PluginId, ct)
            .ConfigureAwait(false);
        if (snap is null)
        {
            return BusinessServiceResult<SkillDraftDto>.Fail(404, BusinessSkillErrorCodes.CatalogIncomplete, $"Snapshot '{request.CatalogSnapshotId}' not found.");
        }

        var distinctIds = request.SelectedCapabilityIds.Distinct(StringComparer.Ordinal).ToArray();
        var missingErr = await ValidateSelectedIdsExistInSnapshotAsync(snap.Id, distinctIds, ct).ConfigureAwait(false);
        if (missingErr is not null)
        {
            return BusinessServiceResult<SkillDraftDto>.Fail(422, BusinessSkillErrorCodes.SelectionViolation, missingErr);
        }

        entity.CatalogSnapshotId = snap.Id;
        entity.SelectedCapabilityIdsJson = JsonSerializer.Serialize(distinctIds, JsonOptions);
        entity.NeedsRevalidation = true;
        entity.ValidationErrorsJson = "[]";
        if (!string.IsNullOrWhiteSpace(entity.CandidateDefinitionJson))
        {
            var existingCandidate = JsonSerializer.Deserialize<SkillDefinitionV1>(entity.CandidateDefinitionJson, JsonOptions);
            if (existingCandidate is not null)
            {
                var updatedCand = existingCandidate with
                {
                    CatalogSnapshotId = snap.Id,
                    SelectedCapabilityIds = distinctIds,
                    ValidationEvidence = new SkillValidationEvidenceDto()
                };
                entity.CandidateDefinitionJson = JsonSerializer.Serialize(
                    updatedCand with { ContentHash = SkillDefinitionV1ValidatorAndCanonicalizer.ComputeContentHash(updatedCand) },
                    JsonOptions);
            }
        }

        entity.Revision += 1;
        entity.UpdatedAtUtc = DateTime.UtcNow;

        try
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return BusinessServiceResult<SkillDraftDto>.Fail(409, BusinessSkillErrorCodes.CatalogChanged,
                "Concurrent draft update detected.");
        }

        return BusinessServiceResult<SkillDraftDto>.Ok(MapDraftDto(entity));
    }

    public async Task<BusinessServiceResult<GenerateSkillCandidateResultDto>> GenerateCandidateAsync(
        string draftId,
        GenerateSkillCandidateRequest request,
        CancellationToken ct = default)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ClientRequestId))
        {
            return BusinessServiceResult<GenerateSkillCandidateResultDto>.Fail(422, BusinessSkillErrorCodes.ValidationFailed, "clientRequestId is required.");
        }

        if (!request.ConfirmModelDataScope)
        {
            return BusinessServiceResult<GenerateSkillCandidateResultDto>.Fail(
                422,
                BusinessSkillErrorCodes.SelectionViolation,
                "confirmModelDataScope must be true before disclosing capability metadata/schemas to the model service.");
        }

        var normalizedClientReqId = request.ClientRequestId.Trim();
        var existingOp = await _db.PluginOperations.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Kind == "generate" && o.TargetId == draftId && o.ClientRequestId == normalizedClientReqId, ct)
            .ConfigureAwait(false);
        if (existingOp is not null)
        {
            var latestDraft = await _db.SkillDrafts.AsNoTracking().FirstOrDefaultAsync(d => d.Id == draftId, ct).ConfigureAwait(false);
            if (latestDraft is null)
            {
                return BusinessServiceResult<GenerateSkillCandidateResultDto>.Fail(404, BusinessSkillErrorCodes.ValidationFailed, $"Draft '{draftId}' not found.");
            }

            if (string.Equals(existingOp.Status, nameof(PluginOperationStatus.Failed), StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(existingOp.ErrorJson))
            {
                var errDto = JsonSerializer.Deserialize<BusinessSkillErrorDto>(existingOp.ErrorJson, JsonOptions);
                return new BusinessServiceResult<GenerateSkillCandidateResultDto>(false, 422, null, errDto);
            }

            var reusedOp = new PluginOperationAcceptedDto(
                existingOp.OperationId,
                existingOp.Kind,
                Enum.TryParse<PluginOperationStatus>(existingOp.Status, out var st) ? st : PluginOperationStatus.Succeeded,
                existingOp.Sequence,
                existingOp.Version,
                ReusedFromClientRequestId: true);
            return BusinessServiceResult<GenerateSkillCandidateResultDto>.Ok(
                new GenerateSkillCandidateResultDto(reusedOp, MapDraftDto(latestDraft), ["capability_names_and_titles", "capability_input_output_schemas"]),
                202);
        }

        var draft = await _db.SkillDrafts.FirstOrDefaultAsync(d => d.Id == draftId, ct).ConfigureAwait(false);
        if (draft is null)
        {
            return BusinessServiceResult<GenerateSkillCandidateResultDto>.Fail(404, BusinessSkillErrorCodes.ValidationFailed, $"Draft '{draftId}' not found.");
        }

        var plugin = await _db.BusinessPlugins.AsNoTracking().FirstOrDefaultAsync(p => p.Id == draft.PluginId, ct).ConfigureAwait(false);
        var conn = plugin is null ? null : await _db.PluginConnections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == plugin.ConnectionId, ct).ConfigureAwait(false);
        if (plugin is null || conn is null)
        {
            return BusinessServiceResult<GenerateSkillCandidateResultDto>.Fail(404, BusinessSkillErrorCodes.ConnectionUnavailable, $"Plugin '{draft.PluginId}' or connection not found.");
        }

        var selectedIds = DeserializeStringArray(draft.SelectedCapabilityIdsJson);
        var selectedCaps = await LoadCapabilityDetailsAsync(draft.CatalogSnapshotId, selectedIds, ct).ConfigureAwait(false);

        var operationId = $"op-{Guid.NewGuid():N}"[..16];
        var opEntity = new PluginOperationEntity
        {
            OperationId = operationId,
            Kind = "generate",
            TargetId = draft.Id,
            ClientRequestId = normalizedClientReqId,
            Status = PluginOperationStatus.Running.ToString(),
            Sequence = 1,
            Version = 1,
            SnapshotId = draft.CatalogSnapshotId,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
        _db.PluginOperations.Add(opEntity);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var genOutcome = await _generator.GenerateAsync(new SkillGenerationContext(
            SkillId: $"skl-{draft.Id}",
            Version: 1,
            Name: draft.Name,
            BusinessGoal: draft.BusinessGoal,
            PluginId: plugin.Id,
            Environment: conn.Environment,
            ConnectionId: conn.Id,
            ConnectionRevision: conn.Revision,
            CredentialRevision: conn.CredentialRevision,
            CatalogSnapshotId: draft.CatalogSnapshotId,
            SelectedCapabilities: selectedCaps,
            IncludeSanitizedSamples: request.IncludeSanitizedSamples), ct).ConfigureAwait(false);

        if (!genOutcome.Success || genOutcome.CandidateDefinition is null)
        {
            var errDto = new BusinessSkillErrorDto(
                genOutcome.ErrorCode ?? BusinessSkillErrorCodes.ValidationFailed,
                genOutcome.ErrorMessage ?? "Skill generation failed.",
                genOutcome.Errors,
                false,
                operationId);
            opEntity.Status = PluginOperationStatus.Failed.ToString();
            opEntity.Sequence = 2;
            opEntity.ErrorJson = JsonSerializer.Serialize(errDto, JsonOptions);
            opEntity.ResultMessage = errDto.Message;
            opEntity.UpdatedAtUtc = DateTime.UtcNow;

            draft.ClarificationQuestionsJson = JsonSerializer.Serialize(genOutcome.ClarificationQuestions, JsonOptions);
            draft.ValidationErrorsJson = JsonSerializer.Serialize(genOutcome.Errors, JsonOptions);
            draft.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            var statusCode = errDto.Code == BusinessSkillErrorCodes.ConnectionUnavailable ? 503 : 422;
            return new BusinessServiceResult<GenerateSkillCandidateResultDto>(false, statusCode, null, errDto);
        }

        draft.CandidateDefinitionJson = JsonSerializer.Serialize(genOutcome.CandidateDefinition, JsonOptions);
        draft.ClarificationQuestionsJson = JsonSerializer.Serialize(genOutcome.ClarificationQuestions, JsonOptions);
        draft.ValidationErrorsJson = JsonSerializer.Serialize(genOutcome.Errors, JsonOptions);
        draft.NeedsRevalidation = genOutcome.Errors.Count > 0;
        draft.Revision += 1;
        draft.UpdatedAtUtc = DateTime.UtcNow;

        opEntity.Status = PluginOperationStatus.Succeeded.ToString();
        opEntity.Sequence = 2;
        opEntity.ResultMessage = $"Generated candidate definition with {genOutcome.Errors.Count} validation errors.";
        opEntity.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var acceptedDto = new PluginOperationAcceptedDto(operationId, "generate", PluginOperationStatus.Succeeded, 2, 1, false);
        return BusinessServiceResult<GenerateSkillCandidateResultDto>.Ok(
            new GenerateSkillCandidateResultDto(acceptedDto, MapDraftDto(draft), genOutcome.DisclosedDataCategories),
            202);
    }

    public async Task<BusinessServiceResult<SkillDraftDto>> ValidateDraftAsync(string draftId, CancellationToken ct = default)
    {
        var draft = await _db.SkillDrafts.FirstOrDefaultAsync(d => d.Id == draftId, ct).ConfigureAwait(false);
        if (draft is null)
        {
            return BusinessServiceResult<SkillDraftDto>.Fail(404, BusinessSkillErrorCodes.ValidationFailed, $"Skill draft '{draftId}' not found.");
        }

        if (string.IsNullOrWhiteSpace(draft.CandidateDefinitionJson))
        {
            return BusinessServiceResult<SkillDraftDto>.Fail(422, BusinessSkillErrorCodes.ValidationFailed, "Draft has no candidateDefinition to validate.");
        }

        var candidate = JsonSerializer.Deserialize<SkillDefinitionV1>(draft.CandidateDefinitionJson, JsonOptions);
        if (candidate is null)
        {
            return BusinessServiceResult<SkillDraftDto>.Fail(422, BusinessSkillErrorCodes.ValidationFailed, "CandidateDefinition JSON is malformed.");
        }

        var plugin = await _db.BusinessPlugins.AsNoTracking().FirstOrDefaultAsync(p => p.Id == draft.PluginId, ct).ConfigureAwait(false);
        var conn = plugin is null ? null : await _db.PluginConnections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == plugin.ConnectionId, ct).ConfigureAwait(false);
        var snap = await _db.CatalogSnapshots.AsNoTracking().FirstOrDefaultAsync(s => s.Id == draft.CatalogSnapshotId && s.PluginId == draft.PluginId, ct).ConfigureAwait(false);
        if (plugin is null || conn is null || snap is null)
        {
            return BusinessServiceResult<SkillDraftDto>.Fail(404, BusinessSkillErrorCodes.CatalogIncomplete, "Bound plugin, connection, or snapshot not found.");
        }

        var selectedIds = DeserializeStringArray(draft.SelectedCapabilityIdsJson);
        var normalized = candidate with
        {
            PluginId = plugin.Id,
            CatalogSnapshotId = snap.Id,
            SelectedCapabilityIds = selectedIds,
            ConnectionBinding = candidate.ConnectionBinding with
            {
                ConnectionId = conn.Id,
                Environment = conn.Environment,
                ConnectionRevision = conn.Revision,
                CredentialRevision = conn.CredentialRevision
            }
        };

        var caps = await LoadCapabilityMapAsync(snap.Id, ct).ConfigureAwait(false);
        var validation = SkillDefinitionV1ValidatorAndCanonicalizer.ValidateAgainstCatalog(
            normalized,
            plugin.Id,
            snap.Id,
            conn.Environment,
            caps);

        var updatedEvidence = validation.IsValid
            ? normalized.ValidationEvidence with
            {
                DraftRevision = draft.Revision,
                CatalogHash = snap.SchemaHash,
                StaticValidationHash = validation.ContentHash,
                VerifiedAtUtc = DateTimeOffset.UtcNow
            }
            : new SkillValidationEvidenceDto();

        var updatedDef = normalized with
        {
            ContentHash = validation.ContentHash,
            ValidationEvidence = updatedEvidence
        };

        draft.CandidateDefinitionJson = JsonSerializer.Serialize(updatedDef, JsonOptions);
        draft.ValidationErrorsJson = JsonSerializer.Serialize(validation.Errors, JsonOptions);
        draft.NeedsRevalidation = !validation.IsValid;
        draft.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return BusinessServiceResult<SkillDraftDto>.Ok(MapDraftDto(draft));
    }

    public async Task<BusinessServiceResult<PublishedSkillDto>> PublishDraftAsync(
        string draftId,
        PublishSkillDraftRequest request,
        CancellationToken ct = default)
    {
        if (request is null || request.ExpectedRevision <= 0)
        {
            return BusinessServiceResult<PublishedSkillDto>.Fail(422, BusinessSkillErrorCodes.ValidationFailed, "expectedRevision > 0 is required.");
        }

        await using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        var draft = await _db.SkillDrafts.FirstOrDefaultAsync(d => d.Id == draftId, ct).ConfigureAwait(false);
        if (draft is null)
        {
            return BusinessServiceResult<PublishedSkillDto>.Fail(404, BusinessSkillErrorCodes.ValidationFailed, $"Draft '{draftId}' not found.");
        }

        if (draft.Revision != request.ExpectedRevision)
        {
            return BusinessServiceResult<PublishedSkillDto>.Fail(409, BusinessSkillErrorCodes.CatalogChanged,
                $"Draft revision conflict: expected {request.ExpectedRevision}, current is {draft.Revision}.");
        }

        if (draft.NeedsRevalidation)
        {
            return BusinessServiceResult<PublishedSkillDto>.Fail(422, BusinessSkillErrorCodes.ValidationFailed,
                "Draft requires revalidation after catalog/connection/definition change before publishing.");
        }

        var plugin = await _db.BusinessPlugins.AsNoTracking().FirstOrDefaultAsync(p => p.Id == draft.PluginId, ct).ConfigureAwait(false);
        var conn = plugin is null ? null : await _db.PluginConnections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == plugin.ConnectionId, ct).ConfigureAwait(false);
        var snap = await _db.CatalogSnapshots.AsNoTracking().FirstOrDefaultAsync(s => s.Id == draft.CatalogSnapshotId && s.PluginId == draft.PluginId, ct).ConfigureAwait(false);
        if (plugin is null || conn is null || snap is null || !plugin.Enabled)
        {
            return BusinessServiceResult<PublishedSkillDto>.Fail(422, BusinessSkillErrorCodes.ConnectionUnavailable,
                "Plugin must remain enabled with valid connection and snapshot to publish.");
        }

        var candidate = string.IsNullOrWhiteSpace(draft.CandidateDefinitionJson)
            ? null
            : JsonSerializer.Deserialize<SkillDefinitionV1>(draft.CandidateDefinitionJson, JsonOptions);
        if (candidate is null)
        {
            return BusinessServiceResult<PublishedSkillDto>.Fail(422, BusinessSkillErrorCodes.ValidationFailed, "Draft has no candidateDefinition.");
        }

        var caps = await LoadCapabilityMapAsync(snap.Id, ct).ConfigureAwait(false);
        var validation = SkillDefinitionV1ValidatorAndCanonicalizer.ValidateAgainstCatalog(
            candidate, plugin.Id, snap.Id, conn.Environment, caps);
        if (!validation.IsValid)
        {
            return BusinessServiceResult<PublishedSkillDto>.Fail(422, BusinessSkillErrorCodes.ValidationFailed,
                "Static/catalog validation failed before publish.", fieldErrors: validation.Errors);
        }

        var ev = candidate.ValidationEvidence;
        if (!string.Equals(ev.StaticValidationHash, validation.ContentHash, StringComparison.Ordinal) ||
            !string.Equals(ev.CatalogHash, snap.SchemaHash, StringComparison.Ordinal))
        {
            return BusinessServiceResult<PublishedSkillDto>.Fail(422, BusinessSkillErrorCodes.ValidationFailed,
                "Validation evidence hashes do not match current draft definition and catalog snapshot; re-run validate and test-run.");
        }

        if (string.IsNullOrWhiteSpace(ev.ReadOnlyTestRunId) ||
            !string.Equals(ev.ReadOnlyTestRunEvidenceHash, ComputeTestRunEvidenceHash(draft.Revision, snap.SchemaHash, validation.ContentHash), StringComparison.Ordinal))
        {
            return BusinessServiceResult<PublishedSkillDto>.Fail(422, BusinessSkillErrorCodes.ValidationFailed,
                "A passing read-only test run on the current validated draft revision is required before publishing.");
        }

        var testRun = await _db.SkillRuns.AsNoTracking()
            .FirstOrDefaultAsync(r => r.RunId == ev.ReadOnlyTestRunId && r.Status == nameof(SkillRunStatus.Succeeded), ct)
            .ConfigureAwait(false);
        if (testRun is null)
        {
            return BusinessServiceResult<PublishedSkillDto>.Fail(422, BusinessSkillErrorCodes.ValidationFailed,
                $"Referenced test run '{ev.ReadOnlyTestRunId}' did not complete with Succeeded status.");
        }

        var existingSkill = await _db.PublishedSkills.FirstOrDefaultAsync(s => s.Id == candidate.SkillId, ct).ConfigureAwait(false);
        var existingVersions = existingSkill is null
            ? []
            : await _db.PublishedSkillVersions.Where(v => v.SkillId == existingSkill.Id).ToListAsync(ct).ConfigureAwait(false);

        var nextVersion = existingVersions.Count == 0 ? 1 : existingVersions.Max(static v => v.Version) + 1;
        var now = DateTime.UtcNow;

        var finalDef = candidate with
        {
            Version = nextVersion,
            Name = draft.Name,
            Description = string.IsNullOrWhiteSpace(candidate.Description) ? draft.BusinessGoal : candidate.Description
        };
        var finalHash = SkillDefinitionV1ValidatorAndCanonicalizer.ComputeContentHash(finalDef);
        finalDef = finalDef with { ContentHash = finalHash };

        if (existingSkill is null)
        {
            existingSkill = new PublishedSkillEntity
            {
                Id = finalDef.SkillId,
                Name = finalDef.Name,
                Description = finalDef.Description,
                PluginId = plugin.Id,
                Environment = conn.Environment,
                ActiveVersion = nextVersion,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            _db.PublishedSkills.Add(existingSkill);
        }
        else
        {
            existingSkill.Name = finalDef.Name;
            existingSkill.Description = finalDef.Description;
            existingSkill.Environment = conn.Environment;
            existingSkill.ActiveVersion = nextVersion;
            existingSkill.UpdatedAtUtc = now;
        }

        var versionEntity = new PublishedSkillVersionEntity
        {
            SkillId = finalDef.SkillId,
            Version = nextVersion,
            ContentHash = finalHash,
            CatalogSnapshotId = snap.Id,
            DefinitionJson = JsonSerializer.Serialize(finalDef, JsonOptions),
            NeedsRevalidation = false,
            WriteStepVerified = ev.WriteStepVerified,
            PublishedAtUtc = now
        };
        _db.PublishedSkillVersions.Add(versionEntity);

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);

        var allVersions = existingVersions.Append(versionEntity).OrderBy(static v => v.Version).ToArray();
        return BusinessServiceResult<PublishedSkillDto>.Ok(MapPublishedSkillDto(existingSkill, allVersions), 201);
    }

    public async Task<IReadOnlyList<PublishedSkillDto>> ListPublishedSkillsAsync(CancellationToken ct = default)
    {
        var skills = await _db.PublishedSkills.AsNoTracking().OrderBy(s => s.CreatedAtUtc).ToListAsync(ct).ConfigureAwait(false);
        var versions = await _db.PublishedSkillVersions.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var bySkill = versions.GroupBy(static v => v.SkillId, StringComparer.Ordinal).ToDictionary(static g => g.Key, static g => g.OrderBy(v => v.Version).ToArray(), StringComparer.Ordinal);

        return skills.Select(s => MapPublishedSkillDto(s, bySkill.TryGetValue(s.Id, out var list) ? list : [])).ToArray();
    }

    public async Task<BusinessServiceResult<PublishedSkillDto>> GetPublishedSkillAsync(string skillId, CancellationToken ct = default)
    {
        var skill = await _db.PublishedSkills.AsNoTracking().FirstOrDefaultAsync(s => s.Id == skillId, ct).ConfigureAwait(false);
        if (skill is null)
        {
            return BusinessServiceResult<PublishedSkillDto>.Fail(404, BusinessSkillErrorCodes.ValidationFailed, $"Published skill '{skillId}' not found.");
        }

        var versions = await _db.PublishedSkillVersions.AsNoTracking()
            .Where(v => v.SkillId == skillId)
            .OrderBy(v => v.Version)
            .ToArrayAsync(ct)
            .ConfigureAwait(false);

        return BusinessServiceResult<PublishedSkillDto>.Ok(MapPublishedSkillDto(skill, versions));
    }

    public async Task<BusinessServiceResult<SkillMarkdownPreviewDto>> GetPublishedSkillMarkdownAsync(
        string skillId,
        int? version = null,
        CancellationToken ct = default)
    {
        var skill = await _db.PublishedSkills.AsNoTracking().FirstOrDefaultAsync(s => s.Id == skillId, ct).ConfigureAwait(false);
        if (skill is null)
        {
            return BusinessServiceResult<SkillMarkdownPreviewDto>.Fail(404, BusinessSkillErrorCodes.ValidationFailed, $"Published skill '{skillId}' not found.");
        }

        var targetVer = version ?? skill.ActiveVersion;
        var verEntity = await _db.PublishedSkillVersions.AsNoTracking()
            .FirstOrDefaultAsync(v => v.SkillId == skillId && v.Version == targetVer, ct)
            .ConfigureAwait(false);
        if (verEntity is null)
        {
            return BusinessServiceResult<SkillMarkdownPreviewDto>.Fail(404, BusinessSkillErrorCodes.ValidationFailed, $"Version {targetVer} not found for skill '{skillId}'.");
        }

        var def = JsonSerializer.Deserialize<SkillDefinitionV1>(verEntity.DefinitionJson, JsonOptions);
        if (def is null)
        {
            return BusinessServiceResult<SkillMarkdownPreviewDto>.Fail(422, BusinessSkillErrorCodes.ValidationFailed, "Stored definition JSON is corrupt.");
        }

        var md = SkillMarkdownRenderer.RenderReadOnlyMarkdown(def);
        return BusinessServiceResult<SkillMarkdownPreviewDto>.Ok(new SkillMarkdownPreviewDto(skillId, targetVer, verEntity.ContentHash, md));
    }

    public async Task<BusinessServiceResult<PublishedSkillDto>> ActivateVersionAsync(
        string skillId,
        ActivateSkillVersionRequest request,
        CancellationToken ct = default)
    {
        if (request is null || request.Version <= 0)
        {
            return BusinessServiceResult<PublishedSkillDto>.Fail(422, BusinessSkillErrorCodes.ValidationFailed, "version >= 1 is required.");
        }

        var skill = await _db.PublishedSkills.FirstOrDefaultAsync(s => s.Id == skillId, ct).ConfigureAwait(false);
        if (skill is null)
        {
            return BusinessServiceResult<PublishedSkillDto>.Fail(404, BusinessSkillErrorCodes.ValidationFailed, $"Published skill '{skillId}' not found.");
        }

        var verEntity = await _db.PublishedSkillVersions.AsNoTracking()
            .FirstOrDefaultAsync(v => v.SkillId == skillId && v.Version == request.Version, ct)
            .ConfigureAwait(false);
        if (verEntity is null)
        {
            return BusinessServiceResult<PublishedSkillDto>.Fail(404, BusinessSkillErrorCodes.ValidationFailed, $"Version {request.Version} not found.");
        }

        if (verEntity.NeedsRevalidation)
        {
            return BusinessServiceResult<PublishedSkillDto>.Fail(409, BusinessSkillErrorCodes.CatalogChanged,
                $"Version {request.Version} requires revalidation after plugin/catalog changes and cannot be activated directly.");
        }

        var plugin = await _db.BusinessPlugins.AsNoTracking().FirstOrDefaultAsync(p => p.Id == skill.PluginId, ct).ConfigureAwait(false);
        var conn = plugin is null ? null : await _db.PluginConnections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == plugin.ConnectionId, ct).ConfigureAwait(false);
        if (plugin is null || conn is null || !plugin.Enabled)
        {
            return BusinessServiceResult<PublishedSkillDto>.Fail(422, BusinessSkillErrorCodes.ConnectionUnavailable,
                $"Plugin '{skill.PluginId}' is disabled or unavailable.");
        }

        var def = JsonSerializer.Deserialize<SkillDefinitionV1>(verEntity.DefinitionJson, JsonOptions);
        if (def is null)
        {
            return BusinessServiceResult<PublishedSkillDto>.Fail(422, BusinessSkillErrorCodes.ValidationFailed, "Stored version definition is invalid.");
        }

        var caps = await LoadCapabilityMapAsync(verEntity.CatalogSnapshotId, ct).ConfigureAwait(false);
        var validation = SkillDefinitionV1ValidatorAndCanonicalizer.ValidateAgainstCatalog(
            def, plugin.Id, verEntity.CatalogSnapshotId, conn.Environment, caps);
        if (!validation.IsValid)
        {
            return BusinessServiceResult<PublishedSkillDto>.Fail(422, BusinessSkillErrorCodes.CatalogChanged,
                "Target version failed live permission/catalog compatibility check.", fieldErrors: validation.Errors);
        }

        skill.ActiveVersion = request.Version;
        skill.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var allVersions = await _db.PublishedSkillVersions.AsNoTracking()
            .Where(v => v.SkillId == skillId)
            .OrderBy(v => v.Version)
            .ToArrayAsync(ct)
            .ConfigureAwait(false);
        return BusinessServiceResult<PublishedSkillDto>.Ok(MapPublishedSkillDto(skill, allVersions));
    }

    internal static string ComputeTestRunEvidenceHash(int draftRevision, string catalogSchemaHash, string staticValidationHash)
    {
        var raw = string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2}", draftRevision, catalogSchemaHash, staticValidationHash);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    internal static SkillDraftDto MapDraftDto(SkillDraftEntity e) => new()
    {
        Id = e.Id,
        PluginId = e.PluginId,
        CatalogSnapshotId = e.CatalogSnapshotId,
        Revision = e.Revision,
        Name = e.Name,
        BusinessGoal = e.BusinessGoal,
        SelectedCapabilityIds = DeserializeStringArray(e.SelectedCapabilityIdsJson),
        CandidateDefinition = string.IsNullOrWhiteSpace(e.CandidateDefinitionJson)
            ? null
            : JsonSerializer.Deserialize<SkillDefinitionV1>(e.CandidateDefinitionJson, JsonOptions),
        ClarificationQuestions = DeserializeStringArray(e.ClarificationQuestionsJson),
        ValidationErrors = string.IsNullOrWhiteSpace(e.ValidationErrorsJson)
            ? []
            : (JsonSerializer.Deserialize<BusinessSkillFieldError[]>(e.ValidationErrorsJson, JsonOptions) ?? []),
        NeedsRevalidation = e.NeedsRevalidation,
        UpdatedAtUtc = new DateTimeOffset(e.UpdatedAtUtc, TimeSpan.Zero)
    };

    private static PublishedSkillDto MapPublishedSkillDto(PublishedSkillEntity s, IReadOnlyList<PublishedSkillVersionEntity> versions) => new()
    {
        Id = s.Id,
        Name = s.Name,
        Description = s.Description,
        PluginId = s.PluginId,
        Environment = s.Environment,
        ActiveVersion = s.ActiveVersion,
        Versions = versions.Select(static v => new PublishedSkillVersionDto(
            v.Version,
            v.ContentHash,
            v.CatalogSnapshotId,
            v.NeedsRevalidation,
            v.WriteStepVerified,
            new DateTimeOffset(v.PublishedAtUtc, TimeSpan.Zero))).ToArray()
    };

    private async Task<string?> ValidateSelectedIdsExistInSnapshotAsync(string snapshotId, string[] selectedIds, CancellationToken ct)
    {
        var found = await _db.CatalogCapabilities.AsNoTracking()
            .Where(c => c.SnapshotId == snapshotId && selectedIds.Contains(c.Id))
            .Select(c => c.Id)
            .ToArrayAsync(ct)
            .ConfigureAwait(false);
        var foundSet = new HashSet<string>(found, StringComparer.Ordinal);
        var missing = selectedIds.FirstOrDefault(id => !foundSet.Contains(id));
        return missing is null ? null : $"Capability '{missing}' does not exist in snapshot '{snapshotId}'.";
    }

    private async Task<IReadOnlyList<CapabilityDetailDto>> LoadCapabilityDetailsAsync(string snapshotId, IReadOnlyList<string> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var rows = await _db.CatalogCapabilities.AsNoTracking()
            .Where(c => c.SnapshotId == snapshotId && ids.Contains(c.Id))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.Select(MapCapabilityDetail).ToArray();
    }

    private async Task<Dictionary<string, CapabilityDetailDto>> LoadCapabilityMapAsync(string snapshotId, CancellationToken ct)
    {
        var rows = await _db.CatalogCapabilities.AsNoTracking()
            .Where(c => c.SnapshotId == snapshotId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.Select(MapCapabilityDetail).ToDictionary(static d => d.Summary.Id, StringComparer.Ordinal);
    }

    private static CapabilityDetailDto MapCapabilityDetail(CatalogCapabilityEntity c) => new()
    {
        Summary = new CapabilitySummaryDto
        {
            Id = c.Id,
            SnapshotId = c.SnapshotId,
            NativeIdentity = c.NativeIdentity,
            ModelAlias = c.ModelAlias,
            Title = c.Title,
            Description = c.Description,
            SourceDescriptionMissing = c.SourceDescriptionMissing,
            GroupTag = c.GroupTag,
            Risk = Enum.TryParse<CapabilityRiskLevel>(c.Risk, out var r) ? r : CapabilityRiskLevel.Unknown,
            SuggestedSourceRiskHint = !string.IsNullOrEmpty(c.SuggestedSourceRiskHint) && Enum.TryParse<CapabilityRiskLevel>(c.SuggestedSourceRiskHint, out var sr) ? sr : null,
            Executability = Enum.TryParse<CapabilityExecutabilityStatus>(c.Executability, out var ex) ? ex : CapabilityExecutabilityStatus.Unsupported,
            Diagnostics = JsonSerializer.Deserialize<string[]>(c.DiagnosticsJson, JsonOptions) ?? []
        },
        InputSchemaJson = c.InputSchemaJson,
        OutputSchemaJson = c.OutputSchemaJson,
        Invocation = JsonSerializer.Deserialize<CapabilityInvocationSpecDto>(c.InvocationJson, JsonOptions) ?? new CapabilityInvocationSpecDto()
    };

    private static string[] DeserializeStringArray(string? json)
        => string.IsNullOrWhiteSpace(json) ? [] : (JsonSerializer.Deserialize<string[]>(json, JsonOptions) ?? []);
}
