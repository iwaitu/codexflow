// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text;
using CodexFlow.Core.Execution;
using CodexFlow.Core.Hashline.Infrastructure;
using CodexFlow.Core.Hashline.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodexFlow.Core.Tests.Hashline;

/// <summary>
/// Regression for cleanapp-bug live run 14: hs_write stripped the UTF-8 BOM of FileServiceTests.cs. The writer picked
/// <c>Encoding.UTF8</c> for "has a BOM", but <c>Encoding.GetBytes</c> never emits the preamble (only a StreamWriter does),
/// so the snapshot's HasBom flag was ignored on every write.
/// </summary>
public sealed class HashlineBomPreservationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cf-hashline-bom-" + Guid.NewGuid().ToString("N"));
    private readonly AtomicFileWriter _writer = new(NullLogger<AtomicFileWriter>.Instance);

    public HashlineBomPreservationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Theory]
    [InlineData("utf-8", true, new byte[] { 0xEF, 0xBB, 0xBF })]
    [InlineData("utf-8", false, new byte[0])]
    [InlineData("utf-16", true, new byte[] { 0xFF, 0xFE })]
    [InlineData("utf-16be", true, new byte[] { 0xFE, 0xFF })]
    public async Task WriteVerified_WritesThePreambleExactlyWhenTheSnapshotHadOne(string encodingName, bool hasBom, byte[] expectedPreamble)
    {
        var path = Path.Combine(_root, "File.cs");
        await File.WriteAllTextAsync(path, "old");
        var version = WorkspaceFileWriter.Sha256Hex(File.ReadAllBytes(path));

        await _writer.WriteVerifiedAsync(path, _root, version, "using System;\nclass A { }\n", encodingName, hasBom, "LF", true);

        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.AsSpan().StartsWith(expectedPreamble), $"preamble mismatch for {encodingName}/hasBom={hasBom}: {Convert.ToHexString(bytes.AsSpan(0, Math.Min(4, bytes.Length)))}");
        if (expectedPreamble.Length == 0)
        {
            // Not a BOM-less file that merely happens to start like one of the preambles.
            Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
        }

        var body = EncodingDetector.GetEncodingFromName(encodingName, hasBom: false).GetString(bytes, expectedPreamble.Length, bytes.Length - expectedPreamble.Length);
        Assert.Equal("using System;\nclass A { }\n", body);
    }

    [Fact]
    public async Task WriteVerified_Utf8Bom_IsWrittenOnceNotDoubled()
    {
        var path = Path.Combine(_root, "Once.cs");
        await File.WriteAllBytesAsync(path, [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. Encoding.UTF8.GetBytes("old")]);
        var version = WorkspaceFileWriter.Sha256Hex(File.ReadAllBytes(path));

        await _writer.WriteVerifiedAsync(path, _root, version, "new", "utf-8", true, "LF", false);

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.Equal("new", Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
    }

    [Fact]
    public async Task UnguardedWrite_AlsoKeepsTheBom()
    {
        var path = Path.Combine(_root, "Plain.cs");

        await _writer.WriteAsync(path, "class A { }\n", "utf-8", hasBom: true, "LF", true);

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, File.ReadAllBytes(path)[..3]);
    }
}
