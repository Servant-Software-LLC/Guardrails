using System.Reflection;

namespace Guardrails.Cli;

/// <summary>
/// The single source of truth for the harness version string. Reads the executing
/// assembly's <see cref="AssemblyInformationalVersionAttribute"/> (the value
/// <c>guardrails --version</c> prints), normalised by stripping any <c>+build</c> metadata —
/// everything from the first <c>'+'</c> onward. Falls back to the assembly version when the
/// attribute is absent. The version stamped into a bundled skill's <c>SKILL.md</c> frontmatter
/// (<c>metadata.guardrails-version</c>, at build) and the harness version compared against it
/// both derive from the same build, so they stay consistent.
///
/// A local Debug build reports the csproj placeholder (e.g. <c>1.0.0-preview.1</c>); a packed
/// tool reports the real package version. The pure drift logic
/// (<see cref="SkillVersionReport"/>) never reads this attribute — versions are injected — so
/// tests do not depend on the build's stamped value.
/// </summary>
public static class GuardrailsVersion
{
    /// <summary>The normalised harness version (no <c>+build</c> metadata).</summary>
    public static string Current { get; } = Resolve(typeof(GuardrailsVersion).Assembly);

    /// <summary>
    /// The version string for <paramref name="assembly"/>: its informational version with any
    /// <c>+build</c> metadata stripped, or its assembly version if the attribute is absent.
    /// Exposed for tests; production callers use <see cref="Current"/>.
    /// </summary>
    public static string Resolve(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            return Normalize(informational);
        }

        return assembly.GetName().Version?.ToString() ?? "0.0.0";
    }

    /// <summary>
    /// Strip <c>+build</c> metadata (everything from the first <c>'+'</c>) and surrounding
    /// whitespace, so <c>1.2.3+abcdef</c> and <c>1.2.3</c> compare equal.
    /// </summary>
    public static string Normalize(string version)
    {
        ArgumentNullException.ThrowIfNull(version);

        string trimmed = version.Trim();
        int plus = trimmed.IndexOf('+');
        return plus >= 0 ? trimmed[..plus] : trimmed;
    }
}
