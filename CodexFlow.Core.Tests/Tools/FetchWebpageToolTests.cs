// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using System.Net;
using Xunit;

namespace CodexFlow.Core.Tests.Tools;

public class FetchWebpageToolTests
{
    [Fact]
    public async Task ExecuteAsync_MissingUrl_ReturnsError()
    {
        // Arrange
        var mockFactory = new Mock<IHttpClientFactory>();
        var tool = new FetchWebpageTool(NullLogger<FetchWebpageTool>.Instance, mockFactory.Object);
        var args = new Dictionary<string, object?>();

        // Act
        var result = await tool.ExecuteAsync(args);

        // Assert
        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Contains("Missing required parameter: url", result.Output ?? "");
    }

    [Fact]
    public async Task ExecuteAsync_InvalidUrlScheme_ReturnsError()
    {
        // Arrange
        var mockFactory = new Mock<IHttpClientFactory>();
        var tool = new FetchWebpageTool(NullLogger<FetchWebpageTool>.Instance, mockFactory.Object);
        var args = new Dictionary<string, object?> { { "url", "ftp://example.com" } };

        // Act
        var result = await tool.ExecuteAsync(args);

        // Assert
        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Contains("Invalid URL format", result.Output ?? "");
    }

    [Fact]
    public async Task ExecuteAsync_ValidUrl_CleansHtmlCorrectly()
    {
        // Arrange
        var mockFactory = new Mock<IHttpClientFactory>();
        var mockHttpMessageHandler = new Mock<HttpMessageHandler>();

        var htmlToReturn = @"
            <!DOCTYPE html>
            <html>
            <head>
                <title>Test Page</title>
                <script>alert('test');</script>
                <style>body { color: red; }</style>
            </head>
            <body>
                <h1>Header Article</h1>
                <div>
                    <p>This is a paragraph.</p>
                    <a href=""https://example.com"">Link text</a>
                </div>
            </body>
            </html>
        ";

        mockHttpMessageHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(htmlToReturn)
            });

        var client = new HttpClient(mockHttpMessageHandler.Object);
        mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(client);

        var tool = new FetchWebpageTool(NullLogger<FetchWebpageTool>.Instance, mockFactory.Object);
        var args = new Dictionary<string, object?> { { "url", "https://example.com" } };

        // Act
        var result = await tool.ExecuteAsync(args);

        // Assert
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.NotNull(result.Output);
        
        var output = result.Output;
        Assert.DoesNotContain("<script>", output);
        Assert.DoesNotContain("alert('test');", output);
        Assert.DoesNotContain("<style>", output);
        Assert.DoesNotContain("color: red", output);
        Assert.Contains("Header Article", output);
        Assert.Contains("This is a paragraph.", output);
        Assert.Contains("Link text", output);
    }

    [Fact]
    public void Category_ShouldBeRead_NotForge()
    {
        var mockFactory = new Mock<IHttpClientFactory>();
        var tool = new FetchWebpageTool(NullLogger<FetchWebpageTool>.Instance, mockFactory.Object);

        Assert.Equal(ToolCategory.Read, tool.Category);
        Assert.True(((ICodexTool)tool).Metadata.IsReadOnly);
    }
}
