using System.Text;
using System.Text.Json;
using Guardrails.Core.Bundle;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Core.Tests;

/// <summary>
/// A plan folder on disk with a journal and a run's logs, for <see cref="BundleBuilder"/> tests (#799). The plan model is
/// built directly (no loader), and every machine fact the bundle asks is injected, so two builds over the same folder
/// are comparable byte for byte.
/// </summary>
internal sealed class BundlePlanFixture : IDisposable
{
    public const string RunId = "2026-09-27T10-00-00Z-ab12";
    public const string KnownToken = "fixture-known-token-value-0001";
    public static readonly DateTimeOffset Clock = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    public BundlePlanFixture(IReadOnlyDictionary<string, PromptRunnerConfig>? runners = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "guardrails-bundle-tests", Guid.NewGuid().ToString("N"));
        Workspace = Path.Combine(Root, "repo");
        PlanDirectory = Path.Combine(Workspace, "plan-x");
        Home = Path.Combine(Root, "home");
        Directory.CreateDirectory(PlanDirectory);
        Directory.CreateDirectory(Home);
        File.WriteAllText(Path.Combine(PlanDirectory, "guardrails.json"), "{ \"version\": 1 }\n");

        Plan = new PlanDefinition
        {
            PlanDirectory = PlanDirectory,
            Workspace = Workspace,
            Config = new RunConfig
            {
                Version = 1,
                PromptRunners = runners ?? new Dictionary<string, PromptRunnerConfig>
                {
                    ["claude"] = Runner("claude"),
                },
            },
            Tasks = [Task("01-first"), Task("02-second")],
        };
    }

    public string Root { get; }

    public string Workspace { get; }

    public string PlanDirectory { get; }

    public string Home { get; }

    public PlanDefinition Plan { get; set; }

    public string RunLogs => Path.Combine(PlanDirectory, "logs", RunId);

    public List<IReadOnlyList<string>> GitCalls { get; } = [];

    public static PromptRunnerConfig Runner(string name, string? authTokenEnv = null, string? baseUrl = null,
        PromptRunnerKind kind = PromptRunnerKind.Claude, string? apiKeyEnv = null) => new()
    {
        Name = name,
        Command = "claude",
        Kind = kind,
        BaseUrl = baseUrl,
        AuthTokenEnv = authTokenEnv,
        ApiKeyEnv = apiKeyEnv,
        Settings = new PromptRunnerSettings { Model = "fixture-model" },
    };

    private TaskNode Task(string id)
    {
        string dir = Path.Combine(PlanDirectory, "tasks", id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"), $"{{ \"description\": \"task {id}\" }}\n");
        return new TaskNode
        {
            Id = id,
            Directory = dir,
            Description = $"task {id}",
            Action = new ActionDefinition { Path = "action.prompt.md", Kind = ActionKind.Prompt },
            Guardrails = [],
            DefinitionHashAtLoad = "sha256:def-" + id,
        };
    }

    public static AttemptRecord Attempt(int n, AttemptOutcome outcome, string? worktree = null, string? baseCommit = null) => new()
    {
        Attempt = n,
        StartedAt = Clock.AddMinutes(n * 10),
        EndedAt = Clock.AddMinutes((n * 10) + 3),
        ActionExitCode = outcome == AttemptOutcome.Succeeded ? 0 : 1,
        Outcome = outcome,
        LogDir = $"logs/{RunId}/x/attempt-{n}",
        Provenance = new AttemptProvenance { Model = "fixture-model", Runner = "claude", WorktreePath = worktree, BaseCommit = baseCommit },
    };

    public static JournalDocument Journal(
        JournalTaskStatus secondStatus = JournalTaskStatus.NeedsHuman, IReadOnlyList<AttemptRecord>? secondAttempts = null,
        InFlightAttemptRecord? inFlight = null, RunHalt? halt = null) => new()
    {
        RunId = RunId,
        PlanHash = "sha256:plan",
        Tasks = new Dictionary<string, TaskJournalEntry>
        {
            ["01-first"] = new()
            {
                Status = JournalTaskStatus.Succeeded,
                DefinitionHash = "sha256:def-01-first",
                Attempts = [Attempt(1, AttemptOutcome.Succeeded)],
            },
            ["02-second"] = new()
            {
                Status = secondStatus,
                DefinitionHash = "sha256:def-02-second",
                Attempts = secondAttempts ?? [Attempt(1, AttemptOutcome.GuardrailFailed)],
                InFlightAttempt = inFlight,
            },
        },
        Halt = halt,
        Owner = new RunOwner { Pid = 4242, ProcessStartedAt = Clock, Host = "fixture-host" },
        Environment = new RunEnvironment { HarnessVersion = "1.26.0", Os = "FixtureOS 1", Host = "fixture-host" },
    };

    public void WriteJournal(JournalDocument document)
    {
        string path = RunJournal.PathFor(PlanDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(document, JournalJson.Options));
    }

    /// <summary>Write a file under <c>logs/&lt;runId&gt;/</c>.</summary>
    public string Log(string relative, string content) => Log(relative, Encoding.UTF8.GetBytes(content));

    public string Log(string relative, byte[] content)
    {
        string path = Path.Combine(RunLogs, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    /// <summary>A typical prompt attempt's files, with <paramref name="planted"/> in every text artifact.</summary>
    public void PromptAttempt(string task, int attempt, string planted)
    {
        string dir = $"{task}/attempt-{attempt}/";
        Log(dir + "transcript.md", $"I ran the tool and saw {planted}.\n");
        Log(dir + "claude-stream.jsonl",
            $"{{\"type\":\"assistant\",\"message\":{{\"content\":[{{\"type\":\"text\",\"text\":\"I ran the tool and saw {planted}.\"}}]}}}}\n");
        Log(dir + "composed-prompt.md", $"Do the task. Context: {planted}\n");
        Log(dir + "prior-attempt.patch", $"--- a/x\n+++ b/x\n@@ -1 +1 @@\n-old\n+{planted}\n");
        Log(dir + "feedback.md", $"Guardrail failed: {planted}\n");
        Log(dir + "attempt-provenance.json", "{\"model\":\"fixture-model\",\"runner\":\"claude\"}\n");
        Log(dir + "attempt-route.log", "runner: claude\nmodel: fixture-model\n");
        Log(dir + "action-result.json", $"{{\"kind\":\"prompt\",\"exitCode\":1,\"summary\":\"agent said {planted}\"}}\n");
        Log(dir + "guardrail-02-tests.stdout.log", $"test output {planted}\n");
        Log(dir + "guardrail-02-tests.verdict.json", $"{{\"pass\":false,\"reason\":\"{planted}\"}}\n");
    }

    public RunLivenessState Liveness { get; set; } = RunLivenessState.Ended;

    /// <summary>The process-tree probe: a fixed answer, so a Running bundle stays deterministic.</summary>
    public Func<int, BundleProcessTree> ProcessTree { get; set; } = pid => new BundleProcessTree(
        [new BundleProcessRow(pid, 1, "S", "00:05:00", "0.0", "guardrails run plan-x")], null);

    public Dictionary<string, string> Environment { get; } = new(StringComparer.Ordinal) { ["QWEN_TOKEN"] = KnownToken };

    public BundleProbes Probes(Func<DateTimeOffset>? now = null) => new()
    {
        Environment = Environment,
        Now = now ?? (() => Clock),
        HarnessVersion = "1.26.0",
        BundlingOs = "FixtureOS 1",
        ToolVersions = () => [new("claude", "2.0.0 (Claude Code)"), new("agent", "not on PATH"), new("dotnet", "10.0.100"), new("git", "git version 2.50.0")],
        Liveness = _ => Liveness,
        ProcessTree = pid => ProcessTree(pid),
        Git = new FakeGit(GitCalls),
        Validate = () => "OK: plan is valid.\n",
        Home = Home,
        UserName = "fixture-user",
        CaseInsensitivePaths = false,
    };

    public BundleOutcome Build(BundleOptions? options = null, BundleProbes? probes = null) =>
        BundleBuilder.Build(new BundleRequest { Plan = Plan, Options = options ?? new BundleOptions() }, probes ?? Probes());

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of a temp directory.
        }
    }

    /// <summary>A git that answers every call with fixed text, and records the calls.</summary>
    private sealed class FakeGit(List<IReadOnlyList<string>> calls) : IBundleGit
    {
        public BundleProcessResult Run(string workingDirectory, IReadOnlyList<string> arguments)
        {
            calls.Add(arguments);
            string output = arguments[0] switch
            {
                "status" => "## guardrails/plan-x\n M src/app.cs\n",
                "log" => "abc1234 Sat Sep 27 10:00:00 2026 fix the thing\n",
                "diff" when arguments.Contains("--stat") => " src/app.cs | 2 +-\n 1 file changed\n",
                "diff" => "diff --git a/src/app.cs b/src/app.cs\n-old\n+new\n",
                _ => string.Empty,
            };
            return new BundleProcessResult(0, output, string.Empty, TimedOut: false, NotFound: false);
        }
    }
}

internal static class BundleOutcomeExtensions
{
    public static string? Text(this BundleOutcome outcome, string path) =>
        outcome.Entries.FirstOrDefault(e => e.Key == path) is { Value: { } bytes } ? Encoding.UTF8.GetString(bytes) : null;

    public static bool Has(this BundleOutcome outcome, string path) => outcome.Entries.Any(e => e.Key == path);

    public static BundleManifestRow Row(this BundleOutcome outcome, string bundlePath) =>
        outcome.Manifest.Single(r => r.BundlePath == bundlePath);

    public static IEnumerable<string> AllText(this BundleOutcome outcome) =>
        outcome.Entries.Select(e => Encoding.UTF8.GetString(e.Value));
}
