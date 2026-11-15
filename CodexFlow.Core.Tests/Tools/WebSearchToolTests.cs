// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Net;
using Moq.Protected;
using Xunit;

namespace CodexFlow.Core.Tests.Tools;

public class WebSearchToolTests
{
    [Fact]
    public async Task ExecuteAsync_WithValidQuery_ReturnsParsedResults()
    {
        // Arrange
        var mockFactory = new Mock<IHttpClientFactory>();
        var mockHttpMessageHandler = new Mock<HttpMessageHandler>();

        var fakeHtmlResponse = @"
            <html><body>
                <tr>
                    <td><a class=""result-url"" href=""https://example.com/test"">Example Test Page</a></td>
                </tr>
                <tr>
                    <td class=""result-snippet"">DuckDuckGo HTML is great!</td>
                </tr>
            </body></html>";

        mockHttpMessageHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(fakeHtmlResponse)
            });

        var client = new HttpClient(mockHttpMessageHandler.Object);
        mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(client);

        var tool = new WebSearchTool(NullLogger<WebSearchTool>.Instance, mockFactory.Object);
        var args = new Dictionary<string, object?> { { "query", "test query site:example.com" } };

        // Act
        var result = await tool.ExecuteAsync(args);

        // Assert
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Contains("Example Test Page", result.Output ?? "");
        Assert.Contains("https://example.com/test", result.Output ?? "");
        Assert.Contains("DuckDuckGo HTML is great!", result.Output ?? "");
    }

    [Fact]
    public async Task ExecuteAsync_WithEmptyQuery_ReturnsError()
    {
        // Arrange
        var mockFactory = new Mock<IHttpClientFactory>();
        var tool = new WebSearchTool(NullLogger<WebSearchTool>.Instance, mockFactory.Object);
        var args = new Dictionary<string, object?> { { "query", "  " } };

        // Act
        var result = await tool.ExecuteAsync(args);

        // Assert
        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Contains("Missing required parameter", result.Output ?? "");
    }

    [Fact]
    public void Category_ShouldBeAnalysis_NotForge()
    {
        var mockFactory = new Mock<IHttpClientFactory>();
        var tool = new WebSearchTool(NullLogger<WebSearchTool>.Instance, mockFactory.Object);

        Assert.Equal(ToolCategory.Analysis, tool.Category);
        Assert.True(((ICodexTool)tool).Metadata.IsReadOnly);
    }
}
