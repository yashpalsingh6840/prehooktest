using Ssis.Extract.Codegen;
using Ssis.Extract.Model.Pipeline;

namespace Ssis.Extract.Codegen.Tests;

public class FlatFileErrorScriptComponentRecognizerTests
{
    private const string RecognizedSource =
        "using System;\n" +
        "using Microsoft.SqlServer.Dts.Pipeline;\n" +
        "\n" +
        "[SSISScriptComponentEntryPoint]\n" +
        "public class ScriptMain : UserComponent\n" +
        "{\n" +
        "    public override void Input0_ProcessInputRow(Input0Buffer Row)\n" +
        "    {\n" +
        "        Row.ErrorDescription = ComponentMetaData.GetErrorDescription(Row.ErrorCode);\n" +
        "        Row.FileName = \"SyntheticFlatFileErrorOutput.csv\";\n" +
        "    }\n" +
        "}";

    private static PipelineComponentSpec BuildComponent(string source, params string[] producedColumnNames)
    {
        return new PipelineComponentSpec
        {
            RefId = "SCR_Test",
            Name = "Script Component",
            ComponentClassId = "Microsoft.ManagedComponentHost",
            ScriptComponent = new ScriptComponentPayload
            {
                Language = "CSharp",
                SourceFiles = [new ScriptComponentSourceFileSpec { Name = "main.cs", Content = source }],
            },
            Outputs =
            [
                new PipelineOutputSpec
                {
                    RefId = "SCR_Test.Outputs[Output 0]",
                    Name = "Output 0",
                    Columns = producedColumnNames.Select(name => new PipelineOutputColumnSpec
                    {
                        RefId = $"SCR_Test.Outputs[Output 0].Columns[{name}]",
                        Name = name,
                        LineageId = $"SCR_Test.Outputs[Output 0].Columns[{name}]",
                    }).ToList(),
                },
            ],
        };
    }

    [Fact]
    public void TryRecognize_ResolvesTheExactRealClientShape()
    {
        var component = BuildComponent(RecognizedSource, "ErrorDescription", "FileName");

        var result = FlatFileErrorScriptComponentRecognizer.TryRecognize(component);

        Assert.NotNull(result);
        Assert.Contains("-1071607676 => \"The data value cannot be converted for reasons other than sign mismatch or data overflow.\"", result!["ErrorDescription"]);
        Assert.Contains("-1071607675 => \"The data was truncated.\"", result["ErrorDescription"]);
        Assert.Equal("\"SyntheticFlatFileErrorOutput.csv\"", result["FileName"]);
    }

    [Fact]
    public void TryRecognize_ReturnsNull_WhenTheScriptHasAnyAdditionalStatement()
    {
        // The safety net: this component's SOURCE has a third statement beyond the one exact
        // recognized pattern -- a real deviation must fall back to the ordinary human-reviewed
        // seam, never a silent partial match.
        var mutatedSource = RecognizedSource.Replace(
            "Row.FileName = \"SyntheticFlatFileErrorOutput.csv\";",
            "Row.FileName = \"SyntheticFlatFileErrorOutput.csv\";\n        Console.WriteLine(\"extra\");");
        var component = BuildComponent(mutatedSource, "ErrorDescription", "FileName");

        Assert.Null(FlatFileErrorScriptComponentRecognizer.TryRecognize(component));
    }

    [Fact]
    public void TryRecognize_ReturnsNull_WhenTheComponentProducesAnyOtherColumnSet()
    {
        // Same recognized source, but a THIRD produced column -- not the one exact measured
        // shape (ErrorDescription + FileName, nothing else), so this must not auto-translate.
        var component = BuildComponent(RecognizedSource, "ErrorDescription", "FileName", "SomethingElse");

        Assert.Null(FlatFileErrorScriptComponentRecognizer.TryRecognize(component));
    }

    [Fact]
    public void TryRecognize_ReturnsNull_WhenSourceIsStripped()
    {
        var component = BuildComponent(RecognizedSource, "ErrorDescription", "FileName");
        var strippedComponent = new PipelineComponentSpec
        {
            RefId = component.RefId,
            Name = component.Name,
            ComponentClassId = component.ComponentClassId,
            ScriptComponent = new ScriptComponentPayload { Language = "CSharp", SourceStripped = true },
            Outputs = component.Outputs,
        };

        Assert.Null(FlatFileErrorScriptComponentRecognizer.TryRecognize(strippedComponent));
    }
}
