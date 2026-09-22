using Ssis.Extract.Model.Diagnostics;
using Xunit;

namespace Ssis.Extract.Tests;

/// <summary>
/// The real, ground-truth check this class needs: given a genuine .NET exception thrown from
/// deep inside code that LOOKS like this tool's own (namespace-wise), the written report must
/// never contain the exception's own <c>.Message</c> (which routinely embeds the exact client
/// value/path/column that triggered a real failure) or any value the caller passed in as a raw
/// command-line argument -- only type names, a filtered stack trace, and whatever safe
/// (ordinal/count) context the caller supplied.
/// </summary>
public class DiagnosticReportTests
{
    private const string SensitiveMessage = "Could not find file 'D:\\Contoso\\Client Data\\CustomerExport_Q3.dtsx'.";

    [Fact]
    public void Capture_NeverWritesTheExceptionMessage()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ssisx-diag-test-" + Guid.NewGuid());
        try
        {
            var ex = MakeExceptionWithMessage();
            var path = DiagnosticReport.Capture("ssisx", "ssisx generate --recursive", "package-generation", ex, outDir: dir);

            Assert.True(File.Exists(path));
            var content = File.ReadAllText(path);
            Assert.DoesNotContain(SensitiveMessage, content);
            Assert.DoesNotContain("CustomerExport_Q3", content);
            Assert.DoesNotContain("Contoso", content);
            Assert.Contains("FileNotFoundException", content);
            Assert.Contains("no client data included, safe to screenshot and share", content);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Capture_KeepsOnlyOwnCodeFrames_AndCollapsesTheRest()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ssisx-diag-test-" + Guid.NewGuid());
        try
        {
            Exception caught;
            try
            {
                // Guarantees a real stack trace with genuine framework frames beneath it
                // (int.Parse) -- those must be collapsed, not enumerated.
                _ = int.Parse("not-a-number");
                throw new InvalidOperationException("unreachable");
            }
            catch (Exception ex)
            {
                caught = ex;
            }

            var path = DiagnosticReport.Capture("ssisx", "ssisx extract", "extract", caught, outDir: dir);
            var content = File.ReadAllText(path);

            Assert.Contains("FormatException", content);
            Assert.DoesNotContain("not-a-number", content);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ScrubArgs_KeepsOnlyFlagNames_NeverValues()
    {
        var scrubbed = DiagnosticReport.ScrubArgs("ssisx", "generate",
            ["--input", @"D:\Contoso\ClientPortfolio", "--out", @"D:\Contoso\Output", "--recursive", "--package", "CustomerLoad,SalesFact"]);

        Assert.Equal("ssisx generate --input --out --recursive --package", scrubbed);
        Assert.DoesNotContain("Contoso", scrubbed);
        Assert.DoesNotContain("CustomerLoad", scrubbed);
    }

    [Fact]
    public void Capture_WritesTheSafeContextProvided()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ssisx-diag-test-" + Guid.NewGuid());
        try
        {
            var ex = new InvalidOperationException("some internal state was inconsistent");
            var path = DiagnosticReport.Capture(
                "ssisx", "ssisx generate", "package-generation", ex,
                context: [("Package", "3 of 12")],
                outDir: dir);

            var content = File.ReadAllText(path);
            Assert.Contains("3 of 12", content);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    private static Exception MakeExceptionWithMessage()
    {
        try
        {
            throw new FileNotFoundException(SensitiveMessage);
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}
