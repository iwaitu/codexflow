// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using System.Xml.Linq;

namespace CodexFlow.Services.Verification;

/// <summary>
/// Reads the machine-written test report of a toolchain and accepts it only when it proves a clean run: at least one test,
/// and every test passed. A report that is malformed, empty, or that shows a failure, an error or a skipped test is rejected,
/// so a run that executed nothing can never pass acceptance (the same rule the .NET TRX check applies).
/// </summary>
public static class TestEvidence
{
    /// <summary>Jest's <c>--json</c> report.</summary>
    public static bool TryAcceptJest(ReadOnlySpan<byte> json, out int testCount)
    {
        testCount = 0;
        try
        {
            using var document = JsonDocument.Parse(json.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True ||
                !TryInt(root, "numTotalTests", out var total) || !TryInt(root, "numPassedTests", out var passed) ||
                !TryInt(root, "numFailedTests", out var failed) || total <= 0 || passed != total || failed != 0)
            {
                return false;
            }

            // Suites that failed to run (syntax error, missing module) are not counted in numFailedTests.
            if (TryInt(root, "numRuntimeErrorTestSuites", out var runtimeErrors) && runtimeErrors != 0) return false;
            if (TryInt(root, "numFailedTestSuites", out var failedSuites) && failedSuites != 0) return false;
            if (TryInt(root, "numPendingTests", out var pending) && pending != 0) return false;

            testCount = total;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>pytest's <c>--junitxml</c> report.</summary>
    public static bool TryAcceptJUnit(ReadOnlySpan<byte> xml, out int testCount)
    {
        testCount = 0;
        try
        {
            using var stream = new MemoryStream(xml.ToArray(), writable: false);
            using var reader = System.Xml.XmlReader.Create(stream, new() { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null });
            var document = XDocument.Load(reader);

            var suites = document.Descendants().Where(x => x.Name.LocalName == "testsuite").ToArray();
            var cases = document.Descendants().Where(x => x.Name.LocalName == "testcase").ToArray();
            if (suites.Length == 0 || cases.Length == 0) return false;

            // Counters are read from the outermost suites only, so nested suites are not counted twice.
            var outer = suites.Where(s => !s.Ancestors().Any(a => a.Name.LocalName == "testsuite")).ToArray();
            var total = 0;
            foreach (var suite in outer)
            {
                if (!int.TryParse(suite.Attribute("tests")?.Value, out var tests)) return false;
                if (Count(suite, "failures") != 0 || Count(suite, "errors") != 0 || Count(suite, "skipped") != 0) return false;
                total = checked(total + tests);
            }

            // The counters must agree with the cases actually listed, and no case may carry a problem element.
            if (total <= 0 || total != cases.Length) return false;
            if (cases.Any(c => c.Elements().Any(e => e.Name.LocalName is "failure" or "error" or "skipped"))) return false;

            testCount = total;
            return true;
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
    }

    private static int Count(XElement suite, string attribute)
        => int.TryParse(suite.Attribute(attribute)?.Value, out var value) ? value : 0;

    private static bool TryInt(JsonElement root, string name, out int value)
    {
        value = 0;
        return root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value);
    }
}
