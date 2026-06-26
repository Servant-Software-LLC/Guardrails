using Guardrails.Core.Execution;

namespace Guardrails.Core.Tests;

/// <summary>
/// The failure-kind-agnostic "the segment will be reset before the next attempt" predicate
/// (issue #167). It is the single signal the executor uses to BOTH perform the F2 retry reset AND
/// thread <c>fileWritesRolledBack</c> into the timeout / max-turns retry feedback — so a feedback
/// that claims a rollback can never disagree with whether the reset actually happens. True only in
/// worktree mode (a real on-disk segment with a real <c>taskBase</c> sha) for a non-final attempt;
/// false in serial mode, for a fake-provider placeholder, and on the final attempt.
/// </summary>
public sealed class WorktreeWillResetTests : IDisposable
{
    private readonly string _segmentDir;

    public WorktreeWillResetTests()
    {
        // IsRealGitSegment requires the path to exist on disk, so back the handle with a temp dir.
        _segmentDir = Path.Combine(Path.GetTempPath(), "gr-167-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_segmentDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_segmentDir, recursive: true); }
        catch (IOException) { /* best-effort cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
    }

    private WorktreeHandle RealSegment() => new()
    {
        WorktreePath = _segmentDir,
        TaskBase = "0123456789abcdef0123456789abcdef01234567" // a real (non-all-zeros) sha
    };

    [Fact]
    public void WorktreeMode_NonFinal_WillReset()
    {
        // Worktree mode + a retry remaining → the segment is reset to taskBase + cleaned before the
        // next attempt, so the prior attempt's file writes ARE rolled back: the disclosure is correct.
        Assert.True(TaskExecutor.WorktreeWillReset(RealSegment(), isFinal: false));
    }

    [Fact]
    public void WorktreeMode_FinalAttempt_DoesNotReset()
    {
        // The final attempt is never reset (no next attempt to prepare), so it must NOT claim a
        // rollback — consistent with #162.
        Assert.False(TaskExecutor.WorktreeWillReset(RealSegment(), isFinal: true));
    }

    [Fact]
    public void SerialMode_EmptyWorktreePath_DoesNotReset()
    {
        // Serial mode (no segment) never resets — file writes persist across attempts.
        var serial = new WorktreeHandle { WorktreePath = "", TaskBase = "" };
        Assert.False(TaskExecutor.WorktreeWillReset(serial, isFinal: false));
        Assert.False(TaskExecutor.WorktreeWillReset(serial, isFinal: true));
    }

    [Fact]
    public void FakeProvider_AllZerosTaskBase_DoesNotReset()
    {
        // A fake worktree provider supplies an all-zeros taskBase placeholder; that is not a real git
        // segment, so no reset (and no rollback claim) even with a real on-disk path.
        var fake = new WorktreeHandle
        {
            WorktreePath = _segmentDir,
            TaskBase = new string('0', 40)
        };
        Assert.False(TaskExecutor.WorktreeWillReset(fake, isFinal: false));
    }
}
