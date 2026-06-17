using Guardrails.Core.State;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace Guardrails.Core.Breakdown;

/// <summary>The outcome of ensuring the GitGuardian baseline exclusion (issue #67).</summary>
public enum GitGuardianEnsureOutcome
{
    /// <summary>No config existed; a fresh <c>.gitguardian.yaml</c> was written.</summary>
    Created,

    /// <summary>A config existed; the baseline exclusion was merged into it.</summary>
    Updated,

    /// <summary>A config already excluded the baseline glob; nothing changed.</summary>
    AlreadyPresent,

    /// <summary>No enclosing git repository was found; nothing was written.</summary>
    SkippedNoGitRepo,

    /// <summary>A config existed but could not be safely parsed/merged; it was left untouched.</summary>
    SkippedUnparseable
}

/// <summary>The result of <see cref="GitGuardianConfig.EnsureBaselineExclusion"/>.</summary>
public sealed record GitGuardianEnsureResult(GitGuardianEnsureOutcome Outcome, string? ConfigPath);

/// <summary>
/// Ensures the repository's GitGuardian/ggshield config excludes <c>guardrails.baseline</c> files
/// from secret scanning (issue #67).
///
/// <para><c>guardrails.baseline</c> is a committed, machine-generated <c>relativePath → SHA-256</c>
/// manifest (see <see cref="BreakdownManifest"/>). A SHA-256 hex digest is a high-entropy string
/// indistinguishable from an API key, so generic secret detectors flag it as a false positive and
/// block commits. The baseline MUST stay committed (it is the BASE for <c>guardrails merge</c>),
/// so the fix is a scanner path-exclude, not a gitignore.</para>
///
/// <para>This is called whenever the tool writes a baseline (<c>guardrails lock</c> and the
/// regeneration <c>merge --apply</c>). It walks up from the plan folder to the enclosing git repo
/// root and ensures <c>.gitguardian.yaml</c> (or an existing <c>.gitguardian.yml</c>) lists
/// <c>**/guardrails.baseline</c> under the appropriate ignored-paths key. It MERGES — it never
/// overwrites an existing config's other keys, and it is idempotent. Comments in an existing file
/// are not preserved on the merge path (YAML round-trip); a freshly created file carries an
/// explanatory comment header.</para>
/// </summary>
public static class GitGuardianConfig
{
    /// <summary>The glob that matches baseline files at any depth.</summary>
    public const string BaselineGlob = "**/guardrails.baseline";

    private const string PrimaryFileName = ".gitguardian.yaml";
    private const string AltFileName = ".gitguardian.yml";

    /// <summary>v2 ggshield key path: <c>secret.ignored-paths</c>.</summary>
    private const string SecretKey = "secret";
    private const string IgnoredPathsKey = "ignored-paths";

    /// <summary>v1 ggshield key: top-level <c>paths-ignore</c>.</summary>
    private const string PathsIgnoreKey = "paths-ignore";
    private const string VersionKey = "version";

    private const string FreshFileContent =
        "# ggshield / GitGuardian configuration\n" +
        "#\n" +
        "# guardrails.baseline files are committed, machine-generated breakdown manifests:\n" +
        "# a relativePath -> SHA-256 mapping. SHA-256 hex digests are high-entropy strings that the\n" +
        "# generic-secret detector flags as a false positive. They are NOT secrets, and the file must\n" +
        "# stay committed (it is the BASE for `guardrails merge`), so baseline files are excluded from\n" +
        "# secret scanning rather than gitignored.\n" +
        "version: 2\n" +
        "secret:\n" +
        "  ignored-paths:\n" +
        "    - \"" + BaselineGlob + "\"\n";

    /// <summary>
    /// Ensure the git repo enclosing <paramref name="startDirectory"/> has a GitGuardian config that
    /// excludes <see cref="BaselineGlob"/> from secret scanning. No-op (with a descriptive outcome)
    /// when there is no enclosing git repo, when the exclusion is already present, or when an existing
    /// config cannot be safely merged.
    /// </summary>
    public static GitGuardianEnsureResult EnsureBaselineExclusion(string startDirectory)
    {
        string? gitRoot = FindGitRoot(Path.GetFullPath(startDirectory));
        if (gitRoot is null)
        {
            return new GitGuardianEnsureResult(GitGuardianEnsureOutcome.SkippedNoGitRepo, null);
        }

        string configPath = ResolveConfigPath(gitRoot);

        if (!File.Exists(configPath))
        {
            AtomicFile.WriteAllText(configPath, FreshFileContent);
            return new GitGuardianEnsureResult(GitGuardianEnsureOutcome.Created, configPath);
        }

        object? root;
        try
        {
            root = new DeserializerBuilder().Build().Deserialize<object?>(File.ReadAllText(configPath));
        }
        catch (YamlException)
        {
            return new GitGuardianEnsureResult(GitGuardianEnsureOutcome.SkippedUnparseable, configPath);
        }

        // An empty/whitespace-only file is equivalent to no config — write the fresh, commented form.
        if (root is null)
        {
            AtomicFile.WriteAllText(configPath, FreshFileContent);
            return new GitGuardianEnsureResult(GitGuardianEnsureOutcome.Created, configPath);
        }

        // Anything that is not a YAML mapping (a scalar or a sequence at the root) is not a shape we
        // can safely extend — leave the user's file untouched rather than risk clobbering it.
        if (root is not IDictionary<object, object> map)
        {
            return new GitGuardianEnsureResult(GitGuardianEnsureOutcome.SkippedUnparseable, configPath);
        }

        IList<object>? targetList = ResolveOrCreateIgnoredPathsList(map);
        if (targetList is null)
        {
            // An existing `secret`/`paths-ignore` key held an unexpected (non-mapping/non-sequence)
            // value — don't clobber it.
            return new GitGuardianEnsureResult(GitGuardianEnsureOutcome.SkippedUnparseable, configPath);
        }

        if (targetList.Any(item => item is string s && string.Equals(s, BaselineGlob, StringComparison.Ordinal)))
        {
            return new GitGuardianEnsureResult(GitGuardianEnsureOutcome.AlreadyPresent, configPath);
        }

        targetList.Add(BaselineGlob);
        AtomicFile.WriteAllText(configPath, new SerializerBuilder().Build().Serialize(map));
        return new GitGuardianEnsureResult(GitGuardianEnsureOutcome.Updated, configPath);
    }

    /// <summary>
    /// Find the existing ignored-paths sequence to extend, creating one if absent, while respecting
    /// the file's existing schema. Returns <c>null</c> when an existing <c>secret</c>/<c>paths-ignore</c>
    /// key holds an unexpected value (so the caller skips rather than clobbers).
    /// Priority: existing v2 <c>secret.ignored-paths</c> → existing v1 top-level <c>paths-ignore</c> →
    /// otherwise introduce a v2 <c>secret.ignored-paths</c> (and <c>version: 2</c> if absent).
    /// </summary>
    private static IList<object>? ResolveOrCreateIgnoredPathsList(IDictionary<object, object> map)
    {
        // 1. Existing v2 secret mapping.
        if (TryGetValue(map, SecretKey, out object? secretObj))
        {
            if (secretObj is not IDictionary<object, object> secretMap)
            {
                return null;
            }

            if (TryGetValue(secretMap, IgnoredPathsKey, out object? ignored))
            {
                return ignored as IList<object>; // null when it isn't a sequence → caller skips
            }

            var created = new List<object>();
            secretMap[IgnoredPathsKey] = created;
            return created;
        }

        // 2. Existing v1 top-level paths-ignore.
        if (TryGetValue(map, PathsIgnoreKey, out object? v1))
        {
            return v1 as IList<object>;
        }

        // 3. Neither present — introduce the v2 shape, declaring version 2 only if the file had none.
        if (!TryGetValue(map, VersionKey, out _))
        {
            map[VersionKey] = 2;
        }

        var freshList = new List<object>();
        map[SecretKey] = new Dictionary<object, object> { [IgnoredPathsKey] = freshList };
        return freshList;
    }

    /// <summary>Walk up from <paramref name="startDir"/> to the nearest ancestor containing <c>.git</c>.</summary>
    private static string? FindGitRoot(string startDir)
    {
        for (DirectoryInfo? dir = new(startDir); dir is not null; dir = dir.Parent)
        {
            string gitPath = Path.Combine(dir.FullName, ".git");
            // A directory for a normal repo; a file for worktrees/submodules.
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
            {
                return dir.FullName;
            }
        }

        return null;
    }

    /// <summary>Prefer an existing <c>.gitguardian.yml</c>; otherwise default to <c>.gitguardian.yaml</c>.</summary>
    private static string ResolveConfigPath(string gitRoot)
    {
        string yml = Path.Combine(gitRoot, AltFileName);
        return File.Exists(yml) ? yml : Path.Combine(gitRoot, PrimaryFileName);
    }

    private static bool TryGetValue(IDictionary<object, object> map, string key, out object? value)
    {
        foreach (KeyValuePair<object, object> pair in map)
        {
            if (pair.Key is string s && string.Equals(s, key, StringComparison.Ordinal))
            {
                value = pair.Value;
                return true;
            }
        }

        value = null;
        return false;
    }
}
