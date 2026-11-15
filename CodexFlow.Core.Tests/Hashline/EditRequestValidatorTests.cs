// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Hashline.Constants;
using CodexFlow.Core.Hashline.Models;
using CodexFlow.Core.Hashline.Services;
using Xunit;

namespace CodexFlow.Core.Tests.Hashline;

/// <summary>
/// EditRequestValidator 单元测试。
/// </summary>
public class EditRequestValidatorTests
{
    private readonly HashlineOptions _options = new();
    private readonly EditRequestValidator _validator;

    public EditRequestValidatorTests()
    {
        _validator = new EditRequestValidator(_options);
    }

    private static FileSnapshot CreateSnapshot(params string[] lines)
    {
        var hashLines = lines.Select((line, index) => new HashlineRecord
        {
            LineNumber = index + 1,
            AnchorId = $"ANCHOR{index + 1}",
            RawText = line,
            NormalizedText = line
        }).ToList();

        return new FileSnapshot
        {
            SnapshotId = "test-snapshot",
            FilePath = "/test/file.txt",
            FileFingerprint = "test-fingerprint",
            EncodingName = "utf-8",
            HasBom = false,
            NewLineStyle = "\n",
            ReadAtUtc = DateTimeOffset.UtcNow,
            Lines = hashLines,
            RenderedText = string.Join("\n", lines)
        };
    }

    [Fact]
    public async Task ValidateAsync_ValidRequest_ReturnsValid()
    {
        var snapshot = CreateSnapshot("Line1", "Line2", "Line3");
        var request = new HashlineEditRequest
        {
            FilePath = "/test/file.txt",
            SnapshotId = "test-snapshot",
            FileFingerprint = "test-fingerprint",
            Operations = new List<EditOperation>
            {
                new InsertAfterOperation
                {
                    TargetLine = 1,
                    TargetAnchorId = "ANCHOR1",
                    NewLines = new List<string> { "NewLine" }
                }
            }
        };

        var result = await _validator.ValidateAsync(request, snapshot);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task ValidateAsync_FingerprintMismatch_ReturnsError()
    {
        var snapshot = CreateSnapshot("Line1", "Line2");
        var request = new HashlineEditRequest
        {
            FilePath = "/test/file.txt",
            SnapshotId = "test-snapshot",
            FileFingerprint = "different-fingerprint",
            Operations = new List<EditOperation>()
        };

        var result = await _validator.ValidateAsync(request, snapshot);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == HashlineErrorCodes.FileFingerprintMismatch);
    }

    [Fact]
    public async Task ValidateAsync_MissingFilePath_ReturnsError()
    {
        var snapshot = CreateSnapshot("Line1");
        var request = new HashlineEditRequest
        {
            FilePath = "",
            SnapshotId = "test-snapshot",
            FileFingerprint = "test-fingerprint",
            Operations = new List<EditOperation>()
        };

        var result = await _validator.ValidateAsync(request, snapshot);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == HashlineErrorCodes.InvalidRequest);
    }

    [Fact]
    public async Task ValidateAsync_MissingSnapshotId_ReturnsError()
    {
        var snapshot = CreateSnapshot("Line1");
        var request = new HashlineEditRequest
        {
            FilePath = "/test/file.txt",
            SnapshotId = "",
            FileFingerprint = "test-fingerprint",
            Operations = new List<EditOperation>()
        };

        var result = await _validator.ValidateAsync(request, snapshot);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == HashlineErrorCodes.SnapshotIdRequired);
    }

    [Fact]
    public async Task ValidateAsync_EmptyOperations_ReturnsError()
    {
        var snapshot = CreateSnapshot("Line1");
        var request = new HashlineEditRequest
        {
            FilePath = "/test/file.txt",
            SnapshotId = "test-snapshot",
            FileFingerprint = "test-fingerprint",
            Operations = new List<EditOperation>()
        };

        var result = await _validator.ValidateAsync(request, snapshot);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == HashlineErrorCodes.InvalidRequest);
    }

    [Fact]
    public async Task ValidateAsync_AnchorMismatch_ReturnsError()
    {
        var snapshot = CreateSnapshot("Line1", "Line2");
        var request = new HashlineEditRequest
        {
            FilePath = "/test/file.txt",
            SnapshotId = "test-snapshot",
            FileFingerprint = "test-fingerprint",
            Operations = new List<EditOperation>
            {
                new InsertAfterOperation
                {
                    TargetLine = 1,
                    TargetAnchorId = "WRONG_ANCHOR",
                    NewLines = new List<string> { "NewLine" }
                }
            }
        };

        var result = await _validator.ValidateAsync(request, snapshot);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == HashlineErrorCodes.AnchorMismatch);
    }

    [Fact]
    public async Task ValidateAsync_LineOutOfRange_ReturnsError()
    {
        var snapshot = CreateSnapshot("Line1", "Line2");
        var request = new HashlineEditRequest
        {
            FilePath = "/test/file.txt",
            SnapshotId = "test-snapshot",
            FileFingerprint = "test-fingerprint",
            Operations = new List<EditOperation>
            {
                new InsertAfterOperation
                {
                    TargetLine = 10, // 超出范围
                    TargetAnchorId = "ANCHOR10",
                    NewLines = new List<string> { "NewLine" }
                }
            }
        };

        var result = await _validator.ValidateAsync(request, snapshot);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == HashlineErrorCodes.LineOutOfRange);
    }

    [Fact]
    public async Task ValidateAsync_InvalidRange_ReturnsError()
    {
        var snapshot = CreateSnapshot("Line1", "Line2", "Line3");
        var request = new HashlineEditRequest
        {
            FilePath = "/test/file.txt",
            SnapshotId = "test-snapshot",
            FileFingerprint = "test-fingerprint",
            Operations = new List<EditOperation>
            {
                new ReplaceRangeOperation
                {
                    StartLine = 3,
                    StartAnchorId = "ANCHOR3",
                    EndLine = 1, // EndLine < StartLine
                    EndAnchorId = "ANCHOR1",
                    NewLines = new List<string> { "New" }
                }
            }
        };

        var result = await _validator.ValidateAsync(request, snapshot);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == HashlineErrorCodes.InvalidRange);
    }

    [Fact]
    public async Task ValidateAsync_RewriteWithOtherOperations_ReturnsError()
    {
        var snapshot = CreateSnapshot("Line1", "Line2");
        _options.AllowRewriteWholeFile = true;
        var request = new HashlineEditRequest
        {
            FilePath = "/test/file.txt",
            SnapshotId = "test-snapshot",
            FileFingerprint = "test-fingerprint",
            Operations = new List<EditOperation>
            {
                new RewriteWholeFileOperation { NewContent = "New Content" },
                new InsertAfterOperation
                {
                    TargetLine = 1,
                    TargetAnchorId = "ANCHOR1",
                    NewLines = new List<string> { "Extra" }
                }
            }
        };

        var result = await _validator.ValidateAsync(request, snapshot);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == HashlineErrorCodes.RewriteWithOtherOperations);
    }

    [Fact]
    public async Task ValidateAsync_RewriteNotAllowed_ReturnsError()
    {
        var snapshot = CreateSnapshot("Line1", "Line2");
        _options.AllowRewriteWholeFile = false;
        var request = new HashlineEditRequest
        {
            FilePath = "/test/file.txt",
            SnapshotId = "test-snapshot",
            FileFingerprint = "test-fingerprint",
            Operations = new List<EditOperation>
            {
                new RewriteWholeFileOperation { NewContent = "New Content" }
            }
        };

        var result = await _validator.ValidateAsync(request, snapshot);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == HashlineErrorCodes.InvalidOperationType);
    }

    [Fact]
    public async Task ValidateAsync_ValidReplaceRange_ReturnsValid()
    {
        var snapshot = CreateSnapshot("Line1", "Line2", "Line3");
        var request = new HashlineEditRequest
        {
            FilePath = "/test/file.txt",
            SnapshotId = "test-snapshot",
            FileFingerprint = "test-fingerprint",
            Operations = new List<EditOperation>
            {
                new ReplaceRangeOperation
                {
                    StartLine = 1,
                    StartAnchorId = "ANCHOR1",
                    EndLine = 2,
                    EndAnchorId = "ANCHOR2",
                    NewLines = new List<string> { "New1", "New2" }
                }
            }
        };

        var result = await _validator.ValidateAsync(request, snapshot);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task ValidateAsync_ValidDeleteRange_ReturnsValid()
    {
        var snapshot = CreateSnapshot("Line1", "Line2", "Line3");
        var request = new HashlineEditRequest
        {
            FilePath = "/test/file.txt",
            SnapshotId = "test-snapshot",
            FileFingerprint = "test-fingerprint",
            Operations = new List<EditOperation>
            {
                new DeleteRangeOperation
                {
                    StartLine = 2,
                    StartAnchorId = "ANCHOR2",
                    EndLine = 2,
                    EndAnchorId = "ANCHOR2"
                }
            }
        };

        var result = await _validator.ValidateAsync(request, snapshot);

        Assert.True(result.IsValid);
    }
}