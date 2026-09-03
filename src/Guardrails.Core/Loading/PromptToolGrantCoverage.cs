using System.Text.RegularExpressions;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Loading;

/// <remarks>
/// <para><b>There is a LIVE positive control in this repository, and it is deliberate. Do not "fix" it
/// without replacing it.</b> <c>docs/plans/33-unproducible-requirements/tasks/02-add-git-tracked-file-probe/action.prompt.md</c>
/// instructs <c>grep -rn "new PlanValidator(" src tests --include=*.cs | wc -l</c>, and that plan grants
/// neither <c>grep</c> nor <c>wc</c>. The warning is TRUE: the prompt really does name a command the
/// grants refuse. It survives because plan 33 is finished and merged, so the folder is historical and
/// the finding costs nobody a run — while every <c>guardrail validate</c> of that folder exercises this
/// check against REAL bytes rather than only against fixtures.</para>
/// <para>It is also the second instance of the defect in that plan: task 09's <c>git ls-tree</c> was
/// found by the issue and remediated, and this one — one fence over, in the same plan — survived that
/// remediation and was found by the corpus sweep that validated this check. If you edit that prompt,
/// move the control into a committed fixture first; a check whose only real-bytes exercise is deleted
/// is a check that silently becomes fixture-only.</para>
/// </remarks>
/// <summary>
/// GR2071 (<c>PromptInstructsUngrantedCommand</c>) — issue #587 check A. A task's prompt names a shell
/// command it tells the agent to run; the task's own effective <c>allowedTools</c> decide whether that
/// command can run at all. Both inputs are static, sit in the same plan folder, and were never compared.
///
/// <para><b>The measured defect.</b> Plan 33 task 09's <c>action.prompt.md</c> read "you enumerate them
/// with <c>git ls-tree</c>", and the grants it resolved to were <c>Bash(dotnet *)</c>,
/// <c>Bash(git log*)</c>, <c>Bash(git diff*)</c>, <c>Bash(git show*)</c>, <c>Bash(git status*)</c>. The
/// one command the whole deliverable rested on was ungranted; every fallback the agent reached for
/// (<c>| grep</c>, <c>| awk</c>) was refused too, because the runner splits a compound and rejects the
/// whole thing on its ungranted part. Two attempts burned, <c>needs-human</c>, run halted — with
/// <c>validate</c>, <c>graph --check</c> and a full <c>/guardrails-review</c> all green on the folder
/// beforehand.</para>
///
/// <para><b>Conservatism is the design</b>, on the <c>ValidateGuardrailRequiresForbiddenToken</c> (GR2057)
/// model: every case it cannot prove is refused rather than guessed at. Measured over the committed corpus
/// — 21 plan folders, 336 prompt-action tasks, 488 backticked binary-led spans — the filters below reduce
/// that to <b>6</b> commands the check will adjudicate at all. Five narrowings, each costing recall on
/// purpose:</para>
/// <list type="number">
/// <item><b>Two sources, not "any code span".</b> An INLINE backticked span, or a line inside a fence the
///   prompt HANDS OVER (colon-introduced, shell-or-untagged, in a paragraph addressing the agent). A fence
///   in this corpus is otherwise an artifact the task must AUTHOR — a guardrail script, a JSON fragment,
///   an expected output — and the hand-over structure is what separates the two. Only two shell fences
///   exist across 365 prompt files, and one of them is a real defect.</item>
/// <item><b>A recognisable command shape only</b> — a head from <see cref="KnownBinaries"/>, plus a bare
///   verb when the span is inline. No attempt is made to parse arbitrary shell. <c>git -C &lt;path&gt; log</c>
///   has no verb in second position and is dropped; so is a bare <c>`git`</c> or a backticked path.</item>
/// <item><b>An imperative context only</b> — a trigger token in the two words before an inline span, and no
///   negation cue anywhere in that line's prefix. This is what separates "enumerate them with
///   <c>git ls-tree</c>" from "the <c>git stash</c> family is blocked" and from
///   "<c>git ls-tree … | grep …</c> is refused", which is the shape the FIX for the measured defect itself
///   wrote into the same file. The commonest inline shape in this corpus — "the harness runs a
///   <c>git diff</c> check", 45% of a stratified sample — is dropped here, by keeping the third-person
///   "runs"/"uses" out of the trigger set.</item>
/// <item><b>Addressed to the AGENT</b> — see <see cref="AddressesTheAgent"/>. The narrowing that carries
///   the whole precision result: without it the check produced 5 findings over the corpus and every one
///   was a prompt describing what the ARTIFACT the agent authors must do.</item>
/// <item><b>Two silence gates on the grants side.</b> No declared <c>allowedTools</c> ⇒ silent, because an
///   unconstrained task cannot violate a grant. No <c>Bash(...)</c> entry among the declared grants ⇒
///   silent too: <c>allowedTools</c> is a FLOOR and not a ceiling (#252), so a plan naming no shell grant
///   has expressed no shell policy at all, and measuring prose against a policy nobody wrote is how a
///   check earns its way into being muted. An unscoped <c>Bash</c> or <c>Bash(*)</c> grants everything.</item>
/// </list>
///
/// <para><b>The compound is split, not special-cased.</b> An instructed span is divided on unquoted
/// <c>|</c>/<c>||</c>/<c>&amp;&amp;</c>/<c>;</c> exactly as the runner divides it, and every segment is
/// tested against the grants. That covers the pipeline half of the defect without a second rule asserting
/// "a pipe is always a refusal" — which would be unsound, since a plan that grants both halves runs the
/// pipeline fine, and which would have fired on the remediation prompt that fixed plan 33. A compound with
/// two ungranted segments is still ONE finding naming both: one defect, one fix.</para>
///
/// <para><b>Known residual.</b> A prompt that instructs the agent, in the second person, about what the
/// ARTIFACT it authors must do — "your test should roll back with <c>git reset --hard</c>" — is
/// indistinguishable here from an instruction to run that command, and would be reported under a narrow
/// grant list. Three instances exist in the corpus (plan 08's author-tests trio); all three are silent
/// today because they say "your tests", not "you", and because plan 08 grants <c>Bash(git *)</c> anyway.
/// This is the shape to watch if the code is ever proposed for promotion to ERROR.</para>
///
/// <para><b>Static and offline.</b> Nothing here spawns a process, opens a socket or consults the repo
/// tree. The one thing it does share with the run path is <see cref="ClaudePromptRunner.ResolveToolGrants"/>
/// — the harness's own injected read-only git grant is folded in through the runner's function rather than
/// re-spelled here, so the effective set this check measures against cannot drift from the set the runner
/// actually passes.</para>
/// </summary>
internal static class PromptToolGrantCoverage
{
    /// <summary>
    /// The closed set of command heads this check will recognise. Closed on purpose: every addition widens
    /// the surface on which a backticked noun can be mistaken for an instruction, and the check's whole
    /// value is that a finding is worth reading. These are the binaries a Guardrails prompt actually tells
    /// an agent to run, plus the shell filters a compound reaches for.
    /// </summary>
    private static readonly HashSet<string> KnownBinaries = new(StringComparer.Ordinal)
    {
        "git", "dotnet", "guardrails", "gh",
        "npm", "npx", "node", "yarn", "pnpm",
        "python", "python3", "pip", "pip3",
        "cargo", "go", "make", "docker",
        "pwsh", "powershell", "bash", "sh",
        "rg", "grep", "sed", "awk", "find", "curl", "jq", "diff"
    };

    /// <summary>
    /// The tokens that make a backticked command an INSTRUCTION rather than a mention. Imperative and
    /// second-person forms only — the third-person descriptive "runs"/"uses" is deliberately absent,
    /// because "the harness runs a <c>git diff</c> check" is prose ABOUT a command the agent is not being
    /// asked to run, and it is the single commonest inline-command shape in this corpus.
    /// </summary>
    private static readonly HashSet<string> InstructionTriggers = new(StringComparer.Ordinal)
    {
        "run", "use", "using", "with", "via", "invoke", "execute", "exec", "call"
    };

    /// <summary>
    /// Cues that turn an apparent instruction into a prohibition, an alternative-rejected, or a
    /// what-NOT-to-do example. Tested over the WHOLE line prefix, not the trigger window, because the
    /// negation routinely sits further left than the verb ("do not filter with <c>| grep</c>"). Tested over
    /// the PREFIX ONLY: the measured true positive carries "never by walking the working tree" AFTER its
    /// span, and a whole-line test would have silenced the one case this check exists for.
    /// </summary>
    private static readonly string[] NegationCues =
    [
        "not ", "n't", "never", "without", "instead of", "rather than", "avoid",
        "refus", "reject", "forbid", "blocked", "ungranted", "denied", "no longer", "cannot"
    ];

    /// <summary>
    /// One inline code span on one line, plus enough of its surroundings to justify a finding. A single
    /// backtick pair with no newline inside — a fenced block never reaches here.
    /// </summary>
    private static readonly Regex InlineCodeSpan =
        new(@"(?<!`)`(?<code>[^`\n]+)`(?!`)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>A head from <see cref="KnownBinaries"/> followed by a bare verb token.</summary>
    private static readonly Regex CommandShape =
        new(@"^(?<bin>[a-z][a-z0-9.+_-]*)\s+(?<verb>[a-z][a-z0-9._-]*)\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>A head from <see cref="KnownBinaries"/> followed by anything at all — the fence arm.</summary>
    private static readonly Regex CommandHead =
        new(@"^(?<bin>[a-z][a-z0-9.+_-]*)\s+\S", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Longest command text echoed into a message.</summary>
    private const int MaxCommandLength = 90;

    /// <summary>How many words before the span may carry the instruction trigger.</summary>
    private const int TriggerWindowWords = 2;

    /// <summary>
    /// Append a warning for every command a task's prompt instructs but its grants refuse. Silent — appends
    /// nothing at all — for a task with no prompt action, an unresolvable runner, no declared
    /// <c>allowedTools</c>, no <c>Bash(...)</c> grant among them, an unscoped Bash grant, or an unreadable
    /// prompt file.
    /// </summary>
    internal static void Validate(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        foreach (TaskNode task in plan.Tasks)
        {
            if (task.Action.Kind != ActionKind.Prompt)
            {
                continue;
            }

            if (ResolveRunner(plan.Config, task.Action.Runner) is not { } runner)
            {
                continue;
            }

            // The ACTION's settings, never the guardrail overrides: guardrailOverrides.allowedTools applies
            // to a prompt GUARDRAIL (EffectiveSettings(isGuardrail: true)), and this check reads the action
            // prompt. Reading the wrong one would measure the prompt against a grant list it never runs on.
            IReadOnlyList<string> declared = runner.EffectiveSettings(isGuardrail: false).AllowedTools;
            if (declared.Count == 0)
            {
                continue; // Gate 1: an unconstrained task cannot violate a grant.
            }

            if (BashGrantContents(declared) is not { } grants)
            {
                continue; // Gate 2: no shell policy declared, or one that grants everything.
            }

            if (TryReadAllText(task.Action.Path) is not { } body)
            {
                continue;
            }

            AppendTask(task, runner.Name, declared, grants, body, diagnostics);
        }
    }

    /// <summary>
    /// The runner a prompt ACTION resolves to: its <c>action.runner</c> pin, else the plan default —
    /// <c>promptRunners.default</c> when it names a declared block, else the sole declared block. The same
    /// two-level notion <c>PlanValidator.ResolveDefaultRunner</c> uses; an unresolvable one is another
    /// check's finding (GR2010) and silence here, never a guess at which block was meant.
    /// </summary>
    private static PromptRunnerConfig? ResolveRunner(RunConfig config, string? pinned)
    {
        if (pinned is not null)
        {
            return config.PromptRunners.TryGetValue(pinned, out PromptRunnerConfig? pin) ? pin : null;
        }

        string? name = config.DefaultPromptRunner is { } named && config.PromptRunnerNames.Contains(named)
            ? named
            : config.PromptRunnerNames.Count == 1 ? config.PromptRunnerNames.Single() : null;

        return name is not null && config.PromptRunners.TryGetValue(name, out PromptRunnerConfig? runner)
            ? runner
            : null;
    }

    /// <summary>
    /// The scoped contents of every <c>Bash(...)</c> grant in the EFFECTIVE set — the declared entries plus
    /// whatever <see cref="ClaudePromptRunner.ResolveToolGrants"/> injects — or <c>null</c> when this check
    /// must stay silent: no <c>Bash</c> entry was DECLARED (gate 2), or one of them grants everything.
    ///
    /// <para>The DECLARED list is what gate 2 asks about and the EFFECTIVE list is what the comparison uses.
    /// That asymmetry is deliberate: the harness injects <c>Bash(git show*)</c> unconditionally, so an
    /// effective-set gate would treat every plan on earth as having declared a shell policy — including the
    /// ones that declared none — while an effective-set COMPARISON is the only honest one, because
    /// <c>git show</c> genuinely does run.</para>
    /// </summary>
    private static IReadOnlyList<string>? BashGrantContents(IReadOnlyList<string> declared)
    {
        if (!declared.Any(entry => ScopedBashContent(entry) is not null))
        {
            return null;
        }

        var contents = new List<string>();
        foreach (string entry in ClaudePromptRunner.ResolveToolGrants(declared).Effective)
        {
            if (ScopedBashContent(entry) is not { } content)
            {
                continue;
            }

            if (content.Length == 0 || content == "*")
            {
                return null; // Grants every command; nothing to report against.
            }

            contents.Add(Normalize(content));
        }

        return contents;
    }

    /// <summary>
    /// The text inside a <c>Bash(...)</c> grant's parentheses, or <c>null</c> when the entry is not a Bash
    /// grant at all. A BARE <c>Bash</c> (no parentheses) returns the empty string, which
    /// <see cref="BashGrantContents"/> reads as grants-everything — the same disposition as <c>Bash(*)</c>,
    /// and the reason the two are not collapsed into one test here.
    /// </summary>
    private static string? ScopedBashContent(string entry)
    {
        string trimmed = entry.Trim();
        int open = trimmed.IndexOf('(', StringComparison.Ordinal);
        if (open < 0)
        {
            return string.Equals(trimmed, "Bash", StringComparison.Ordinal) ? string.Empty : null;
        }

        if (!trimmed.EndsWith(')') ||
            !string.Equals(trimmed[..open].Trim(), "Bash", StringComparison.Ordinal))
        {
            return null;
        }

        return trimmed[(open + 1)..^1].Trim();
    }

    /// <summary>
    /// Report each distinct ungranted COMMAND once per task, in the order the prompt names it — never once
    /// per ungranted segment. A pipeline whose two halves are both ungranted is one authoring defect with
    /// one fix, and splitting it into two warnings is how a reader learns to skim the code (#229). A prompt
    /// that repeats the same instruction three times is deduplicated for the same reason.
    /// </summary>
    private static void AppendTask(
        TaskNode task,
        string runnerName,
        IReadOnlyList<string> declared,
        IReadOnlyList<string> grants,
        string body,
        List<Diagnostic> diagnostics)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (InstructedCommand command in InstructedCommands(body))
        {
            IReadOnlyList<string> segments = Segments(command.Text);
            string[] ungranted = [.. segments.Where(s => !IsGranted(s, grants))];

            if (ungranted.Length == 0 || !seen.Add(command.Text))
            {
                continue;
            }

            bool compound = segments.Count > 1;
            string what = compound
                ? $"instructs the compound command `{Excerpt(command.Text)}` (line {command.Line}), whose " +
                  $"segment{(ungranted.Length > 1 ? "s" : string.Empty)} " +
                  $"{string.Join(" and ", ungranted.Select(s => $"`{Excerpt(s)}`"))} " +
                  $"{(ungranted.Length > 1 ? "are" : "is")} not granted"
                : $"instructs `{Excerpt(command.Text)}` (line {command.Line}), which is not granted";

            string why = compound
                ? "The runner SPLITS a compound and refuses the whole thing on its ungranted part, so the " +
                  "granted half buys nothing — this is refused on the first try, exactly as if the ungranted " +
                  "segment had been the only command. "
                : string.Empty;

            diagnostics.Add(Warning(DiagnosticCodes.PromptInstructsUngrantedCommand, task.Action.Path,
                $"Task '{task.Id}' {what} by the `allowedTools` it resolves to on prompt runner " +
                $"'{runnerName}': {string.Join(", ", declared)}. {why}" +
                "A command the prompt names but the grants refuse is a wall the agent hits on its first " +
                "turn and cannot argue its way past: it burns the retry budget discovering the " +
                "contradiction, then settles at needs-human having never been able to do the work. Fix " +
                "at whichever end is wrong — grant the command on this runner, or stop naming it and " +
                "instruct a route the grants already permit. This is a WARNING because the extractor " +
                "reads free prose and the grants are only the plan's own floor (an operator's " +
                "~/.claude/settings.json can also permit it, #252); if the command really is reachable " +
                "here, the prompt is still the wrong place to leave that unsaid."));
        }
    }

    /// <summary>One backticked command a prompt instructs, with the line it sits on.</summary>
    private readonly record struct InstructedCommand(string Text, int Line);

    /// <summary>
    /// Every inline backticked command the prompt INSTRUCTS. Fenced blocks are skipped whole — the fence
    /// state is tracked line by line so an indented or tilde fence closes the one that opened it, and a
    /// backtick inside a fence never reaches the span matcher.
    ///
    /// <para>The paragraph — a run of non-blank lines, which is what a markdown hard-wrap makes of a
    /// sentence — is accumulated as it goes, because the second-person test that decides who the
    /// instruction is ADDRESSED to routinely reads across a wrapped line. In the measured defect the
    /// pronoun and its verb are on different physical lines ("… and you" / "enumerate them with
    /// <c>git ls-tree</c>"), so a line-scoped test would have missed the one case this check exists for.</para>
    /// </summary>
    private static IEnumerable<InstructedCommand> InstructedCommands(string body)
    {
        string[] lines = body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        string? fence = null;
        var paragraph = new System.Text.StringBuilder();

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string opener = FenceMarker(line);

            if (fence is not null)
            {
                if (opener.Length > 0 && opener[0] == fence[0] && opener.Length >= fence.Length)
                {
                    fence = null;
                }

                paragraph.Clear();
                continue;
            }

            if (opener.Length > 0)
            {
                fence = opener;

                // A fence the prompt HANDS OVER: the sentence that introduces it ends in a colon, and
                // the paragraph it closes addresses the agent. Markdown's own "here it is" structure,
                // which is why this arm needs no instruction VERB — "Verify that count yourself before
                // and after:" names no running-verb at all and is unmistakably an instruction to run
                // what follows. Both the colon and the second person are required, and the fence's
                // language tag must not name a data format, so a fence introduced by "Write this
                // guardrail:" carrying a script is refused on its tag and an untagged one on its
                // content — every line of which must independently be a recognisable command.
                if (IsCommandFence(lines, i, opener))
                {
                    for (int j = i + 1; j < lines.Length; j++)
                    {
                        string closer = FenceMarker(lines[j]);
                        if (closer.Length > 0 && closer[0] == fence[0] && closer.Length >= fence.Length)
                        {
                            break;
                        }

                        string candidate = lines[j].Trim();
                        if (LooksLikeCommand(candidate, requireVerb: false))
                        {
                            yield return new InstructedCommand(Normalize(candidate), j + 1);
                        }
                    }
                }

                paragraph.Clear();
                continue;
            }

            if (line.Trim().Length == 0)
            {
                paragraph.Clear();
                continue;
            }

            foreach (Match span in InlineCodeSpan.Matches(line))
            {
                string code = span.Groups["code"].Value.Trim();
                if (!LooksLikeCommand(code, requireVerb: true) ||
                    !IsInstructed(line[..span.Index]) ||
                    !AddressesTheAgent(paragraph.ToString() + line[..span.Index]))
                {
                    continue;
                }

                yield return new InstructedCommand(Normalize(code), i + 1);
            }

            paragraph.Append(line).Append(' ');
        }
    }

    /// <summary>
    /// Whether the paragraph up to the span addresses the AGENT — a second-person pronoun somewhere before
    /// the command. This is the one narrowing that separates "you run this" from the false-positive class
    /// nothing else touches: a prompt describing what the ARTIFACT the agent authors must do. All five
    /// findings this check produced across the whole committed corpus before this gate were that shape —
    /// "roll back with <c>git reset --hard &lt;preHead&gt;</c>" inside a spec for a test HELPER, a table
    /// cell describing what a TEST does, "instruct the skill to obtain the hash via
    /// <c>guardrails plan-hash</c>" inside a spec for a SKILL — and each is a grammatically perfect
    /// imperative that the agent must never execute in its own shell.
    ///
    /// <para>It is a crude proxy for the clause's SUBJECT and it is honest about the recall it costs: a
    /// bare "Run <c>dotnet test</c> and confirm it passes" is a real instruction this gate refuses. That
    /// trade is deliberate and in the only safe direction — a check whose findings are worth reading is
    /// worth more than one that catches every case and is muted inside a week (#229).</para>
    /// </summary>
    private static bool AddressesTheAgent(string paragraphPrefix)
    {
        foreach (Match word in SecondPerson.Matches(paragraphPrefix))
        {
            _ = word;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Language tags a fence may carry and still be read as commands to run: none at all, or a shell.
    /// A tag naming a language or a data format is a fence holding an ARTIFACT — the JSON the task must
    /// emit, the C# it must write — and is refused outright rather than having its lines inspected.
    /// </summary>
    private static readonly HashSet<string> ShellFenceTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "sh", "bash", "shell", "console", "zsh", "ps1", "powershell", "pwsh"
    };

    /// <summary>
    /// Whether the fence opening at <paramref name="index"/> is one the prompt hands the agent to run:
    /// a shell (or untagged) fence, introduced by a colon-terminated sentence, in a paragraph that
    /// addresses the agent. All three are required; the fence's own lines are then filtered again by
    /// <see cref="LooksLikeCommand"/>, so a fence that merely LOOKS handed-over but holds a script
    /// contributes nothing.
    /// </summary>
    private static bool IsCommandFence(string[] lines, int index, string opener)
    {
        string tag = lines[index].TrimStart(' ')[opener.Length..].Trim();
        if (tag.Length > 0 && !ShellFenceTags.Contains(tag))
        {
            return false;
        }

        // The introducer is the last non-blank line before the fence. ONE blank line may separate them —
        // the ordinary markdown spelling, and the spelling the second real defect this check found is
        // written in — but no more: a fence two paragraphs below a colon is introduced by nothing.
        int k = index - 1;
        if (k >= 0 && lines[k].Trim().Length == 0)
        {
            k--;
        }

        if (k < 0 || lines[k].Trim().Length == 0 || FenceMarker(lines[k]).Length > 0)
        {
            return false;
        }

        string introducer = lines[k].TrimEnd();
        if (!introducer.EndsWith(':') ||
            NegationCues.Any(cue => introducer.Contains(cue, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        // The paragraph the introducer closes, rebuilt from the array rather than from the accumulator:
        // the blank line above would have cleared the accumulator, and it is exactly that shape the
        // second defect takes.
        int start = k;
        while (start > 0 && lines[start - 1].Trim().Length > 0 && FenceMarker(lines[start - 1]).Length == 0)
        {
            start--;
        }

        return AddressesTheAgent(string.Join(' ', lines[start..(k + 1)]));
    }

    private static readonly Regex SecondPerson =
        new(@"\b(you|yourself)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// The fence run a line opens or closes (<c>```</c>/<c>~~~</c> or longer), or the empty string. Up to
    /// three leading spaces, per CommonMark; four would make it an indented code block, which carries no
    /// backticks to confuse anyway.
    /// </summary>
    private static string FenceMarker(string line)
    {
        string trimmed = line.TrimStart(' ');
        if (line.Length - trimmed.Length > 3)
        {
            return string.Empty;
        }

        char marker = trimmed.StartsWith("```", StringComparison.Ordinal) ? '`'
            : trimmed.StartsWith("~~~", StringComparison.Ordinal) ? '~'
            : '\0';

        if (marker == '\0')
        {
            return string.Empty;
        }

        int run = 0;
        while (run < trimmed.Length && trimmed[run] == marker)
        {
            run++;
        }

        return new string(marker, run);
    }

    /// <summary>
    /// A known binary at the head, and — <paramref name="requireVerb"/> — a bare verb after it.
    ///
    /// <para>The verb is REQUIRED of an inline span and NOT of a line inside a handed-over fence, and the
    /// difference is what each context has already proved. Inline, the verb is the only thing separating a
    /// command from a noun: it drops a backticked tool NAME (<c>`git`</c>, <c>`dotnet`</c>), a path
    /// (<c>`docs/plans/`</c>) and a flag-first invocation (<c>`git -C &lt;path&gt; log`</c>) — the last a real
    /// command this check deliberately cannot see, because reading it would mean parsing shell. Inside a
    /// fence the colon-introduced hand-over has already established command-hood, so demanding a verb there
    /// buys nothing and costs the shape the second real defect takes: <c>grep -rn "…" src tests | wc -l</c>
    /// is binary-plus-FLAG, and the verb rule would have missed it.</para>
    /// </summary>
    private static bool LooksLikeCommand(string code, bool requireVerb)
    {
        Match shape = (requireVerb ? CommandShape : CommandHead).Match(code);
        return shape.Success && KnownBinaries.Contains(shape.Groups["bin"].Value);
    }

    /// <summary>
    /// Whether the text before a span makes it an instruction: a trigger among the last
    /// <see cref="TriggerWindowWords"/> words, and no negation cue anywhere in the prefix. Markdown
    /// emphasis, list bullets and punctuation are stripped from a token's edges so <c>**run**</c> and
    /// <c>run:</c> read as <c>run</c>.
    /// </summary>
    private static bool IsInstructed(string prefix)
    {
        string lowered = prefix.ToLowerInvariant();
        if (NegationCues.Any(cue => lowered.Contains(cue, StringComparison.Ordinal)))
        {
            return false;
        }

        string[] words = lowered.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        return words
            .Reverse()
            .Take(TriggerWindowWords)
            .Select(word => word.Trim('*', '_', '`', '"', '\'', '(', ')', '[', ']', ',', '.', ':', ';', '-', '>', '#'))
            .Any(InstructionTriggers.Contains);
    }

    /// <summary>
    /// Split a command the way the runner does — on unquoted <c>|</c>, <c>||</c>, <c>&amp;&amp;</c> and
    /// <c>;</c>. Quote-aware, so a pipe inside <c>rg "a|b"</c> is not an operator; <c>&amp;</c> splits only
    /// as the doubled form, so a redirect (<c>2&gt;&amp;1</c>) stays with its command. Empty segments are
    /// dropped, which is what makes <c>||</c> behave as one operator without a special case.
    /// </summary>
    private static IReadOnlyList<string> Segments(string command)
    {
        var segments = new List<string>();
        int start = 0;
        char quote = '\0';

        for (int i = 0; i < command.Length; i++)
        {
            char c = command[i];

            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '\'' or '"')
            {
                quote = c;
                continue;
            }

            int width = c switch
            {
                '|' or ';' => 1,
                '&' when i + 1 < command.Length && command[i + 1] == '&' => 2,
                _ => 0
            };

            if (width == 0)
            {
                continue;
            }

            Add(command[start..i]);
            i += width - 1;
            start = i + 1;
        }

        Add(command[start..]);

        return segments.Count == 0 ? [command] : segments;

        void Add(string piece)
        {
            string trimmed = piece.Trim();
            if (trimmed.Length > 0)
            {
                segments.Add(trimmed);
            }
        }
    }

    /// <summary>
    /// Whether any grant permits this segment, replicating the CLI's prefix-glob matching. A trailing
    /// <c>*</c> makes the rest a prefix (and a <c>:</c> before it — the <c>Bash(git show:*)</c> spelling —
    /// is part of the separator, not of the command); a grant with no <c>*</c> is still treated as a prefix.
    ///
    /// <para>Every ambiguity here is resolved PERMISSIVELY, and that is the point: this check's errors must
    /// fall on the side of saying nothing. Treating a bare grant as a prefix, and letting
    /// <c>Bash(dotnet *)</c>'s trailing space cover a bare <c>dotnet</c>, both cost findings and buy the
    /// guarantee that a grant which really does permit the command is never reported as refusing it.</para>
    /// </summary>
    private static bool IsGranted(string segment, IReadOnlyList<string> grants)
    {
        string normalized = Normalize(segment);

        foreach (string grant in grants)
        {
            string prefix = grant.EndsWith('*') ? grant[..^1] : grant;
            if (prefix.EndsWith(':'))
            {
                prefix = prefix[..^1];
            }

            if (prefix.Length == 0 ||
                normalized.StartsWith(prefix, StringComparison.Ordinal) ||
                normalized == prefix.TrimEnd())
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Collapse internal whitespace runs to one space, preserving a single trailing space. The trailing
    /// space is load-bearing on the grant side: <c>Bash(dotnet *)</c> means "dotnet, then something", and
    /// trimming it would silently widen the grant to every command starting with the letters "dotnet".
    /// </summary>
    private static string Normalize(string text) =>
        Regex.Replace(text.TrimStart(), @"\s+", " ", RegexOptions.CultureInvariant);

    private static string Excerpt(string text) =>
        text.Length <= MaxCommandLength ? text : text[..MaxCommandLength] + "…";

    private static string? TryReadAllText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static Diagnostic Warning(string code, string path, string message) => new()
    {
        Code = code,
        Severity = DiagnosticSeverity.Warning,
        Path = path,
        Message = message
    };
}
