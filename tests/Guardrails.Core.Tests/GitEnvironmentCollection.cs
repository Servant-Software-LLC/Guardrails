namespace Guardrails.Core.Tests;

/// <summary>
/// Serializes every test class in this assembly that spawns a <c>git</c> child process.
///
/// <para><b>The measured failure.</b> The v1.15.0 release pipeline failed on
/// <c>windows-latest</c> — and only there, and only sometimes — with
/// <c>PromptRoleSeamTests.AiMergeResolver_PassesActionRole</c> reporting
/// <c>git reset --hard &lt;sha&gt; … exited 128: fatal: Could not parse object</c> inside its own
/// throwaway merge repo. The object was perfectly real; git was simply looking in the wrong
/// repository. The version number was burned for a defect that had nothing to do with the release.</para>
///
/// <para><b>The mechanism.</b> <c>ProducerCoverageTests.WithGitPointedAt</c> points the production
/// <see cref="Guardrails.Core.Loading.GitLsFilesProbe"/> at a temp repo the only way that probe can be
/// pointed — by setting <c>GIT_DIR</c> and <c>GIT_WORK_TREE</c>. Those are set with
/// <see cref="System.Environment.SetEnvironmentVariable(string,string)"/>, which mutates the
/// <b>whole process</b>, and the <c>lock</c> around them is private to that one class. xUnit runs
/// separate collections in parallel, so any git child started by ANOTHER class during that window
/// inherits the pointer and silently resolves against the wrong repository. A lock cannot fix this:
/// the state being shared is not the lock's, it is the process's.</para>
///
/// <para><b>Why a collection rather than a redesign.</b> Classes in one xUnit collection never run
/// concurrently, so the window cannot overlap another git child in this assembly — and this assembly
/// is the whole exposure, since <c>WithGitPointedAt</c> exists nowhere else and the integration suite
/// runs in its own process. Membership is the cheap, complete fix; it costs a little wall-clock and no
/// production code, which matters because that probe is GR2060's ERROR-severity oracle.</para>
///
/// <para><b>If you add a test class that shells out to git, add it here.</b> The real repair is to give
/// the probe an explicit working directory so the ambient environment stops being the anchor at all —
/// tracked separately; this keeps releases from failing on a coin flip in the meantime.</para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GitEnvironmentCollection
{
    /// <summary>The collection name. Referenced by every git-spawning test class in this assembly.</summary>
    public const string Name = "git-environment";
}
