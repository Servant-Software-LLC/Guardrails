using Guardrails.Core.Execution;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// #838 — a bare <c>writeScope</c> entry with no extension is read by the matcher as a DIRECTORY prefix, so
/// <c>Dockerfile</c> never claims the file <c>Dockerfile</c>. <c>GR2090</c> (a warning) names the <c>Name*</c>
/// workaround for a closed list of well-known extensionless file names; legitimate directory entries must stay silent.
/// </summary>
public sealed class WriteScopeExtensionlessFileTests
{
    [Fact]
    public void Matcher_ExtensionlessEntry_IsADirectory_SoTheFileItselfIsOutOfScope()
    {
        // Pins the shipped behavior the diagnostic describes: if this ever flips, GR2090 becomes a lie.
        Assert.False(WriteScope.IsInScope("Dockerfile", ["Dockerfile"]));
        Assert.True(WriteScope.IsInScope("Dockerfile/x", ["Dockerfile"]));
        Assert.True(WriteScope.IsInScope("Dockerfile", ["Dockerfile*"]));
    }

    [Theory]
    [InlineData("Dockerfile")]
    [InlineData("Makefile")]
    [InlineData("LICENSE")]
    [InlineData("Jenkinsfile")]
    [InlineData("docker/Dockerfile")]
    [InlineData("makefile")]
    public void KnownExtensionlessFile_IsFlagged(string entry)
    {
        Assert.True(WriteScope.IsExtensionlessFileEntry(entry, out string name));
        Assert.Equal(entry.Split('/')[^1], name);
    }

    [Theory]
    [InlineData("Dockerfile*")]        // the workaround itself
    [InlineData("Dockerfile/")]        // explicit directory marker
    [InlineData("Dockerfile.dev")]     // has an extension
    [InlineData("src")]                // a legitimate directory
    [InlineData("src/Foo/")]
    [InlineData("src/**")]
    [InlineData(".dockerignore")]      // dotfile, handled by the #262 literal arm
    [InlineData("docker-compose.yml")]
    [InlineData("Procfile.local")]
    [InlineData("MyUnknownFile")]      // unknown extensionless name is indistinguishable from a directory
    [InlineData("")]
    public void LegitimateOrUnknownEntry_IsNotFlagged(string entry)
    {
        Assert.False(WriteScope.IsExtensionlessFileEntry(entry, out _));
    }

    private static string BuildPlan(string writeScopeJson)
    {
        string planDir = Path.Combine(Path.GetTempPath(), "gr-838-ws-" + Guid.NewGuid().ToString("N"));
        string taskDir = Path.Combine(planDir, "tasks", "01-do-thing");
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"), "{\n  \"version\": 1\n}\n");
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            "{\n  \"writeScope\": " + writeScopeJson + ",\n  \"description\": \"Do the one thing\",\n  \"dependsOn\": []\n}\n");
        File.WriteAllText(Path.Combine(taskDir, "action.sh"), "#!/usr/bin/env bash\nexit 0\n");
        File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-it-worked.sh"),
            "# catches: the action printed nothing / produced no evidence it ran\nexit 0\n");
        return planDir;
    }

    private static IReadOnlyList<Diagnostic> Validate(string writeScopeJson)
    {
        string planDir = BuildPlan(writeScopeJson);
        try
        {
            PlanDefinition plan = new PlanLoader().Load(planDir).Plan!;
            return new PlanValidator(FakeExecutableProbe.All).Validate(plan);
        }
        finally
        {
            try { Directory.Delete(planDir, recursive: true); }
            catch (IOException) { /* best-effort */ }
        }
    }

    [Fact]
    public void Validate_DockerfileEntry_ReportsGR2090Warning_NamingTheWorkaround()
    {
        IReadOnlyList<Diagnostic> diags = Validate("[\"Dockerfile\", \"docker-compose.yml\", \".dockerignore\"]");

        Diagnostic d = Assert.Single(diags, x => x.Code == DiagnosticCodes.WriteScopeExtensionlessFile);
        Assert.Equal("GR2090", d.Code);
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        Assert.Contains("Dockerfile*", d.Message);
    }

    [Fact]
    public void Validate_DotSlashAndBackslashEntries_SuggestTheNormalizedWorkaround()
    {
        IReadOnlyList<Diagnostic> diags = Validate("[\"./Dockerfile\", \"docker\\\\Makefile\", \"Jenkinsfile\"]");

        List<Diagnostic> hits = diags.Where(x => x.Code == DiagnosticCodes.WriteScopeExtensionlessFile).ToList();
        Assert.Equal(3, hits.Count);
        Assert.Contains(hits, h => h.Message.Contains("Write 'Dockerfile*'"));
        Assert.Contains(hits, h => h.Message.Contains("Write 'docker/Makefile*'"));
        Assert.Contains(hits, h => h.Message.Contains("Write 'Jenkinsfile*'"));
    }

    [Fact]
    public void Validate_DirectoryAndWorkaroundEntries_ReportNoGR2090()
    {
        IReadOnlyList<Diagnostic> diags = Validate("[\"src\", \"docs/\", \"Dockerfile*\", \"Makefile/\", \"docker-compose.yml\"]");

        Assert.DoesNotContain(diags, x => x.Code == DiagnosticCodes.WriteScopeExtensionlessFile);
    }
}
