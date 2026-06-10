using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests;

public sealed class GuardrailVerdictReaderTests
{
    [Fact]
    public void ValidPass_ParsesPassAndReason()
    {
        GuardrailVerdict verdict = GuardrailVerdictReader.Parse("""{ "pass": true, "reason": "tone is friendly" }""");

        Assert.True(verdict.Pass);
        Assert.Equal("tone is friendly", verdict.Reason);
    }

    [Fact]
    public void ValidFail_ParsesPassFalseAndReason()
    {
        GuardrailVerdict verdict = GuardrailVerdictReader.Parse("""{ "pass": false, "reason": "no tone section" }""");

        Assert.False(verdict.Pass);
        Assert.Equal("no tone section", verdict.Reason);
    }

    [Fact]
    public void InvalidJson_FailsWithContractualReason()
    {
        GuardrailVerdict verdict = GuardrailVerdictReader.Parse("not json {");

        Assert.False(verdict.Pass);
        Assert.Equal(GuardrailVerdictReader.NoValidVerdictReason, verdict.Reason);
    }

    [Fact]
    public void MissingPassKey_FailsWithContractualReason()
    {
        GuardrailVerdict verdict = GuardrailVerdictReader.Parse("""{ "reason": "forgot the pass key" }""");

        Assert.False(verdict.Pass);
        Assert.Equal(GuardrailVerdictReader.NoValidVerdictReason, verdict.Reason);
    }

    [Fact]
    public void PassNotBoolean_FailsWithContractualReason()
    {
        GuardrailVerdict verdict = GuardrailVerdictReader.Parse("""{ "pass": "yes", "reason": "stringly typed" }""");

        Assert.False(verdict.Pass);
        Assert.Equal(GuardrailVerdictReader.NoValidVerdictReason, verdict.Reason);
    }

    [Fact]
    public void NonObjectRoot_Fails()
    {
        GuardrailVerdict verdict = GuardrailVerdictReader.Parse("[true]");

        Assert.False(verdict.Pass);
        Assert.Equal(GuardrailVerdictReader.NoValidVerdictReason, verdict.Reason);
    }

    [Fact]
    public void PassWithoutReason_IsTolerated()
    {
        GuardrailVerdict verdict = GuardrailVerdictReader.Parse("""{ "pass": true }""");

        Assert.True(verdict.Pass);
        Assert.Equal(string.Empty, verdict.Reason);
    }

    [Fact]
    public void MissingFile_Fails()
    {
        string path = Path.Combine(Path.GetTempPath(), "no-verdict-" + Guid.NewGuid().ToString("N") + ".json");

        GuardrailVerdict verdict = GuardrailVerdictReader.Read(path);

        Assert.False(verdict.Pass);
        Assert.Equal(GuardrailVerdictReader.NoValidVerdictReason, verdict.Reason);
    }

    [Fact]
    public void ReadFromFile_ValidVerdict()
    {
        string path = Path.Combine(Path.GetTempPath(), "verdict-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """{ "pass": true, "reason": "ok" }""");

        try
        {
            GuardrailVerdict verdict = GuardrailVerdictReader.Read(path);
            Assert.True(verdict.Pass);
            Assert.Equal("ok", verdict.Reason);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
