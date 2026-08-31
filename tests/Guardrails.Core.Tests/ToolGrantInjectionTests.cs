using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests;

/// <summary>
/// Pins the harness's obligation to PROVISION the permissions its own protocol prescribes.
/// <para>
/// <c>RetryPolicy</c>'s salvage section tells a retrying agent to run <c>git show "&lt;ref&gt;:&lt;path&gt;"</c>,
/// but nothing ever granted that command: it depended on a plan author remembering the #252 read-only
/// default, or on the operator's own <c>~/.claude/settings.json</c>. <c>allowedTools</c> is a FLOOR, not
/// a ceiling — the CLI merges the harness list with the operator's settings — so on the maintainer's box
/// the prescription happened to work, and on a clean box or in CI the plan's list IS the whole grant and
/// the prescribed command is refused. The fix is the same shape as the <c>--add-dir &lt;planDirectory&gt;</c>
/// grant <see cref="ClaudePromptRunner.BuildArguments"/> already injects unconditionally so the agent can
/// reach <c>prior-attempt.patch</c>: the harness grants what it asks for, on every invocation, rather
/// than hoping someone else did.
/// </para>
/// <para>
/// Unconditional is deliberate. Conditioning on "this attempt carries a salvage ref" would make the
/// effective permission set vary between attempts of the SAME task — reintroducing exactly the
/// nondeterminism this change exists to remove.
/// </para>
/// <para>
/// READ-ONLY ONLY is equally deliberate, and the negative assertions below are load-bearing: a git write
/// verb is unnarrowable under a prefix glob, so none is ever injected.
/// </para>
/// </summary>
public sealed class ToolGrantInjectionTests
{
    /// <summary>
    /// The one grant the harness provisions for itself, pinned VERBATIM. Spelled exactly as the #252
    /// read-only default spells it, so an implementation that invents a near-miss (<c>Bash(git show:*)</c>,
    /// <c>Bash(git show *)</c>) fails here rather than shipping a grant the CLI does not match.
    /// </summary>
    private const string SalvageReadGrant = "Bash(git show*)";

    /// <summary>
    /// Every git WRITE verb that must NEVER appear among the injected additions. The decision of record
    /// is read-only ONLY: `git restore`, `git checkout`, `git reset`, `git apply` and `git stash` all
    /// mutate the tree (and `git stash` is repo-global, so it is unsafe across concurrent worktrees).
    /// Without these assertions a later change could widen the injection and every other test here would
    /// still pass.
    /// </summary>
    private static readonly string[] GitWriteVerbs =
    [
        "git restore",
        "git checkout",
        "git reset",
        "git apply",
        "git stash"
    ];

    private static PromptInvocation Invocation(params string[] declaredTools) => new()
    {
        ComposedPrompt = "prompt",
        Role = PromptRole.Action,
        WorkingDirectory = "/work",
        PlanDirectory = "/plan",
        Environment = new Dictionary<string, string>(),
        Settings = new PromptRunnerSettings { AllowedTools = declaredTools },
        Timeout = TimeSpan.FromMinutes(5),
        StreamLogPath = "/log/stream.jsonl"
    };

    /// <summary>
    /// The entries actually carried by <c>--allowedTools</c> on the built command line. Fails the test
    /// when the flag is absent at all: once the harness provisions its own grant the effective list is
    /// never empty, so omitting the flag means the injected grant never reaches the CLI.
    /// </summary>
    private static IReadOnlyList<string> GrantedTools(IReadOnlyList<string> args)
    {
        int index = args.ToList().IndexOf("--allowedTools");
        Assert.True(index >= 0, "--allowedTools was not passed at all — the injected grant never reaches the CLI");
        return args[index + 1].Split(',');
    }

    // ---------------------------------------------------------------------------------------------
    // 1. The grant is present UNCONDITIONALLY.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void SalvageGrant_IsInjected_WhenThePlanDeclaresNoToolsAtAll()
    {
        // The hardest case, and the real one: a plan whose allowedTools are empty grants nothing, so on a
        // clean box / CI the agent has no git at all — and the salvage text it is handed still says
        // `git show`. (Observed live: docs/plans/diagram-live-status-and-search/guardrails.json grants no
        // git whatsoever, and that plan did fire salvage.)
        IReadOnlyList<string> granted = GrantedTools(ClaudePromptRunner.BuildArguments(Invocation()));

        Assert.Contains(SalvageReadGrant, granted);
    }

    [Fact]
    public void SalvageGrant_IsInjected_WhenThePlanDeclaresAnUnrelatedSet()
    {
        // A plan that declares a perfectly reasonable stack-specific set — and no git — must still get it.
        string[] declared = ["Read", "Edit", "Write", "Grep", "Glob", "Bash(dotnet *)"];

        IReadOnlyList<string> granted = GrantedTools(ClaudePromptRunner.BuildArguments(Invocation(declared)));

        Assert.Contains(SalvageReadGrant, granted);
    }

    [Fact]
    public void DeclaredGrants_SurviveInjection_InTheirOriginalOrder()
    {
        // Injection ADDS; it must never drop or reorder what the plan asked for.
        string[] declared = ["Read", "Edit", "Bash(dotnet *)"];

        IReadOnlyList<string> granted = GrantedTools(ClaudePromptRunner.BuildArguments(Invocation(declared)));

        Assert.Equal(declared, granted.Where(t => declared.Contains(t, StringComparer.Ordinal)));
    }

    // ---------------------------------------------------------------------------------------------
    // 2. Injected exactly once.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void SalvageGrant_IsNotDuplicated_WhenThePlanAlreadyDeclaresIt()
    {
        // The #252 default already lists it, so this is the COMMON case, not an edge case. A duplicate
        // entry is not fatal to the CLI, but it makes the provenance record lie about what was added.
        // Green before the injection lands (there is nothing to duplicate yet) and must stay green after.
        string[] declared = ["Read", SalvageReadGrant, "Bash(git status*)"];

        IReadOnlyList<string> granted = GrantedTools(ClaudePromptRunner.BuildArguments(Invocation(declared)));

        int occurrences = granted.Count(t => string.Equals(t, SalvageReadGrant, StringComparison.Ordinal));
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public void NothingIsReportedAsInjected_WhenThePlanAlreadyDeclaresIt()
    {
        // The other half of "exactly once": the harness ADDED nothing here, and must say so. Provenance
        // that claims an injection which never happened is as misleading as one that hides a real one.
        ToolGrantResolution resolution =
            ClaudePromptRunner.ResolveToolGrants(["Read", SalvageReadGrant, "Bash(git status*)"]);

        Assert.Empty(resolution.Injected);
        Assert.Contains(SalvageReadGrant, resolution.Effective);
    }

    // ---------------------------------------------------------------------------------------------
    // 3. NO write verb is ever injected. Load-bearing negative: read-only ONLY.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void NoGitWriteVerb_IsEverInjected_ForAnyDeclaredSet()
    {
        string[][] declaredSets =
        [
            [],                                     // no grants at all
            ["Read", "Edit"],                       // an unrelated set
            [SalvageReadGrant],                     // already has the read grant
            ["Bash(git *)"],                        // the plan itself granted wide git — still inject no more
            ["Bash(git restore*)"]                  // even a plan that granted a WRITE verb does not license one
        ];

        foreach (string[] declared in declaredSets)
        {
            string injected = string.Join(",", ClaudePromptRunner.ResolveToolGrants(declared).Injected);

            foreach (string verb in GitWriteVerbs)
            {
                Assert.DoesNotContain(verb, injected);
            }
        }
    }

    [Fact]
    public void NoGitWriteVerb_ReachesTheCommandLine_WhenThePlanDeclaredNone()
    {
        // The end-to-end form of the same negative: with an empty declared list, EVERY entry on the
        // command line is something the harness put there — so the whole flag value must be write-free.
        IReadOnlyList<string> granted = GrantedTools(ClaudePromptRunner.BuildArguments(Invocation()));
        string joined = string.Join(",", granted);

        foreach (string verb in GitWriteVerbs)
        {
            Assert.DoesNotContain(verb, joined);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // 4. The runner SURFACES what it injected instead of silently mutating the list.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void TheInjection_IsSurfaced_ExactlyAndOnlyTheReadGrant()
    {
        // Provenance (and the attempt log header) must be able to distinguish what the HARNESS ADDED from
        // what the PLAN DECLARED. An implementation that quietly appends to the settings list and returns
        // nothing leaves the effective permission set unauditable — which is the failure this whole change
        // corrects: nobody could see it.
        var declared = new List<string> { "Read", "Edit" };

        ToolGrantResolution resolution = ClaudePromptRunner.ResolveToolGrants(declared);

        Assert.Equal([SalvageReadGrant], resolution.Injected);

        // The caller's own list is left alone — the resolution is a returned VALUE, not a mutation.
        Assert.Equal(["Read", "Edit"], declared);

        // Effective = declared (order preserved) + exactly the one addition, and nothing else.
        Assert.Equal(declared, resolution.Effective.Where(t => declared.Contains(t)));
        Assert.Contains(SalvageReadGrant, resolution.Effective);
        int effectiveCount = resolution.Effective.Count;
        Assert.Equal(declared.Count + 1, effectiveCount);
    }

    [Fact]
    public void TheSurfacedResolution_IsWhatTheCommandLineActuallyCarries()
    {
        // Without this, the surfaced value could drift from the flag that is really passed — provenance
        // would record an injection the CLI never received. The two must come from the same resolution.
        string[] declared = ["Read", "Bash(dotnet *)"];

        ToolGrantResolution resolution = ClaudePromptRunner.ResolveToolGrants(declared);
        IReadOnlyList<string> granted = GrantedTools(ClaudePromptRunner.BuildArguments(Invocation(declared)));

        Assert.Equal(resolution.Effective, granted);
    }

    [Fact]
    public void TheSurfacedResolution_IsWhatTheCommandLineCarries_ForAPlanWithNoDeclaredTools()
    {
        // Same consistency check on the unconditional path, where the ENTIRE flag value is harness-injected.
        ToolGrantResolution resolution = ClaudePromptRunner.ResolveToolGrants([]);
        IReadOnlyList<string> granted = GrantedTools(ClaudePromptRunner.BuildArguments(Invocation()));

        Assert.Equal(resolution.Effective, granted);
        Assert.Equal(resolution.Injected, resolution.Effective);
    }
}
