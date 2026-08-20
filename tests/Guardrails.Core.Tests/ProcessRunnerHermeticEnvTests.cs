using Guardrails.Core.Execution;

namespace Guardrails.Core.Tests;

/// <summary>
/// Unit half of issue #442: <see cref="ProcessRunner.ApplyEnvironment"/> makes the caller's dictionary
/// the child's COMPLETE view of the <c>GUARDRAILS_*</c> namespace. The dictionary here stands in for a
/// <c>ProcessStartInfo.Environment</c> — which is pre-seeded with a copy of the harness's own block, so
/// these tests seed the "inherited" side the same way.
/// <para>
/// The end-to-end proof (a real child spawned from a genuinely poisoned parent) lives in
/// <c>HermeticChildEnvironmentTests</c>; this pins the sweep's edges, which a single end-to-end run
/// cannot cover cheaply.
/// </para>
/// </summary>
public sealed class ProcessRunnerHermeticEnvTests
{
    private static Dictionary<string, string?> Inherited(params (string Name, string Value)[] entries)
    {
        // ProcessStartInfo.Environment matches the platform's own name semantics; mirror that here or
        // the tests would prove something the real dictionary does not do.
        var env = new Dictionary<string, string?>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach ((string name, string value) in entries)
        {
            env[name] = value;
        }

        return env;
    }

    [Fact]
    public void UndeclaredHarnessKey_IsRemovedNotBlanked()
    {
        Dictionary<string, string?> env = Inherited(("GUARDRAILS_STATE_OUT", "/outer/run/fragment.json"));

        ProcessRunner.ApplyEnvironment(env, new Dictionary<string, string> { ["GUARDRAILS_TASK_ID"] = "01-x" });

        // Removed, not blanked: a shell reads an empty-but-present variable as SET, so blanking would
        // still satisfy `test -n "${V+x}"` and every role-detection branch keyed on presence.
        Assert.False(env.ContainsKey("GUARDRAILS_STATE_OUT"));
        Assert.Equal("01-x", env["GUARDRAILS_TASK_ID"]);
    }

    [Fact]
    public void WholeHarnessNamespace_IsSwept_NotJustTheKnownKeys()
    {
        // #442 is a CLASS, not two call sites: any GUARDRAILS_* key the harness did not declare for this
        // child is a leak, including ones that do not exist yet.
        Dictionary<string, string?> env = Inherited(
            ("GUARDRAILS_WORKSPACE", "/other/run/_integration"),
            ("GUARDRAILS_VERDICT_OUT", "/other/run/verdict.json"),
            ("GUARDRAILS_SOME_FUTURE_KEY", "whatever"));

        ProcessRunner.ApplyEnvironment(env, new Dictionary<string, string>());

        Assert.Empty(env);
    }

    [Fact]
    public void DeclaredKey_OverridesTheInheritedValue()
    {
        Dictionary<string, string?> env = Inherited(("GUARDRAILS_WORKSPACE", "/other/run/_integration"));

        ProcessRunner.ApplyEnvironment(env, new Dictionary<string, string> { ["GUARDRAILS_WORKSPACE"] = "/this/run/segment" });

        Assert.Equal("/this/run/segment", env["GUARDRAILS_WORKSPACE"]);
    }

    [Fact]
    public void NonHarnessInheritance_IsUntouched()
    {
        // A child action needs its ambient toolchain; the sweep is scoped to the namespace the harness
        // owns, never to inheritance in general.
        Dictionary<string, string?> env = Inherited(
            ("PATH", "/usr/bin"),
            ("HOME", "/home/dev"),
            ("GUARDRAILSISH", "not in the namespace — no underscore"),
            ("GUARDRAILS_STATE_OUT", "/outer/run/fragment.json"));

        ProcessRunner.ApplyEnvironment(env, new Dictionary<string, string>());

        Assert.Equal("/usr/bin", env["PATH"]);
        Assert.Equal("/home/dev", env["HOME"]);
        Assert.Equal("not in the namespace — no underscore", env["GUARDRAILSISH"]);
        Assert.False(env.ContainsKey("GUARDRAILS_STATE_OUT"));
    }

    [Fact]
    public void NonHarnessOverlayEntries_AreStillApplied()
    {
        // Callers legitimately set non-GUARDRAILS_* vars (the Claude output-token cap, a task's `env`
        // passthrough). The sweep must not turn ApplyEnvironment into a GUARDRAILS-only channel.
        Dictionary<string, string?> env = Inherited(("PATH", "/usr/bin"));

        ProcessRunner.ApplyEnvironment(env, new Dictionary<string, string>
        {
            ["CLAUDE_CODE_MAX_OUTPUT_TOKENS"] = "8192",
            ["MY_TASK_VAR"] = "on"
        });

        Assert.Equal("8192", env["CLAUDE_CODE_MAX_OUTPUT_TOKENS"]);
        Assert.Equal("on", env["MY_TASK_VAR"]);
        Assert.Equal("/usr/bin", env["PATH"]);
    }

    [Fact]
    public void WindowsCaseInsensitivity_MatchesThePlatformsOwnNameSemantics()
    {
        // Windows env names are case-insensitive, POSIX names are not — and ProcessStartInfo.Environment
        // is keyed that way too. An ordinal-only sweep on Windows would walk past `Guardrails_State_Out`
        // that the child would nonetheless read back as GUARDRAILS_STATE_OUT.
        Dictionary<string, string?> env = Inherited(("Guardrails_State_Out", "/outer/run/fragment.json"));

        ProcessRunner.ApplyEnvironment(env, new Dictionary<string, string>());

        if (OperatingSystem.IsWindows())
        {
            Assert.Empty(env);
        }
        else
        {
            // On POSIX that is a genuinely different variable, outside the harness namespace.
            Assert.Equal("/outer/run/fragment.json", env["Guardrails_State_Out"]);
        }
    }
}
