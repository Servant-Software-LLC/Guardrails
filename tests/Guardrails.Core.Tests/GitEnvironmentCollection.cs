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
/// <para><b>The mechanism, which is now GONE (#593).</b> <c>ProducerCoverageTests.WithGitPointedAt</c>
/// pointed the production <see cref="Guardrails.Core.Loading.GitLsFilesProbe"/> at a temp repo the only
/// way that probe could then be pointed — by setting <c>GIT_DIR</c> and <c>GIT_WORK_TREE</c> with
/// <see cref="System.Environment.SetEnvironmentVariable(string,string)"/>, which mutates the
/// <b>whole process</b>, guarded by a <c>lock</c> private to that one class. xUnit runs separate
/// collections in parallel, so any git child started by ANOTHER class during that window inherited the
/// pointer and silently resolved against the wrong repository. A lock could not fix it: the state being
/// shared was not the lock's, it was the process's.</para>
///
/// <para>The repair this file's earlier revision named as "tracked separately" has landed. The probe now
/// takes an explicit <c>workingDirectory</c>, the test passes its temp repo as a constructor argument, and
/// <c>WithGitPointedAt</c> — the only process-global git mutation in this assembly — is deleted.</para>
///
/// <para><b>So why is this collection still here?</b> Because the issue that produced the repair also
/// recorded a second symptom that was never explained: <c>ProducerCoverageTests</c> was observed failing
/// once and passing on re-run, which suggests it can be a <i>victim</i> of something else racing it, not
/// only the polluter. That is unconfirmed either way, and this collection is what would be hiding it.</para>
///
/// <para><b>The condition for deleting this file</b>, stated so the next person does not have to
/// re-derive it: a run of this assembly WITHOUT the collection, on all three OSes, repeated enough to mean
/// something. Until then the membership costs a little wall-clock and proves nothing false — whereas
/// removing it on the strength of "the cause we know about is gone" is exactly the reasoning that spends a
/// release. <b>If you add a test class that shells out to git, add it here.</b></para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GitEnvironmentCollection
{
    /// <summary>The collection name. Referenced by every git-spawning test class in this assembly.</summary>
    public const string Name = "git-environment";
}
