namespace Guardrails.Core.Execution;

/// <summary>
/// The shared "does this halt name a workspace path?" predicate (design 41 §2.1). Moved out of
/// <c>RunCommand.MissingResourceHaltLines</c> so the halt text and the overwatcher consult read the
/// SAME signal and can never disagree about what the agent asked for.
/// </summary>
public static class MissingResourceSignal
{
    /// <summary>Every workspace-relative path token in <paramref name="question"/>, in order, deduplicated.</summary>
    public static IReadOnlyList<string> PathsIn(string? question) => throw new NotImplementedException();
}
