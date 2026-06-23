using Guardrails.Core.Execution;

namespace Guardrails.Core.Tests;

/// <summary>
/// Proof harness for the WriteScope glob matcher — plan 08 §2.1/§2.2 milestone gate.
///
/// This file references <see cref="WriteScope.IsInScope"/> and <see cref="WriteScope.Overlaps"/>
/// which live in <c>Execution/WriteScope.cs</c>, a file that does NOT YET EXIST.
/// The project will therefore NOT COMPILE against current code — this is the intended
/// RED signal. Do NOT implement the matcher here; implement M3/WriteScope.cs instead.
///
/// Two test suites:
///
///   1. <c>IsInScope_TruthTable</c> — the 27-row truth table from §2.1(d), pinned verbatim
///      as a <c>[Theory]/[InlineData]</c> set.  Trap rows (1, 4, 7, 19, 21, 23, 25) assert
///      <c>false</c>; a deliberately-naive permissive matcher (the "if a segment contains
///      <c>*</c>, skip its literal prefix/suffix and accept" shortcut) returns <c>true</c> for
///      these rows, so the suite is RED against that matcher.
///
///   2. <c>MembershipImpliesOverlap</c> and <c>OverlapsCompleteness</c> — seeded, reproducible
///      generative tests (§2.2) whose seeds are embedded in the method; a counterexample
///      replays exactly by re-running the same method.
/// </summary>
public sealed class WriteScopeMatcherTests
{
    // =========================================================================
    // §2.1(d) — 27-row IsInScope truth table, pinned verbatim
    //
    // Each row: InlineData("<glob>", "<path>", <expected>)
    // Row 11 uses "" as a sentinel for the empty scope []: the test helper converts
    // it to an empty list so every row stays in the single InlineData format.
    // Trap rows (1, 4, 7, 19, 21, 23, 25) assert false — a naive permissive matcher
    // returns true for all of them, making the suite RED against that matcher.
    // =========================================================================

    [Theory]
    // Row 1 — literal prefix `Feat` must match the segment start (PERMISSIVE-BUG TRAP → false)
    [InlineData("src/Feat*/**", "src/OtherDir/Z.cs", false)]
    // Row 2 — `left` prefix; the membership/overlap divergence case
    [InlineData("marks/left*", "marks/right.start", false)]
    // Row 3 — prefix matches
    [InlineData("marks/left*", "marks/left.start", true)]
    // Row 4 — final `*.cs` segment must end in `.cs` (PERMISSIVE-BUG TRAP → false)
    [InlineData("src/**/*.cs", "src/x/secrets.json", false)]
    // Row 5 — `**` spans `x/y`, final segment matches `*.cs`
    [InlineData("src/**/*.cs", "src/x/y/Thing.cs", true)]
    // Row 6 — not under `src/`, wrong extension
    [InlineData("src/**/*.cs", "Foo.txt", false)]
    // Row 7 — sibling-prefix trap: `FeatureX ≠ Feature` as a whole segment (PERMISSIVE-BUG TRAP → false)
    [InlineData("src/Feature/**", "src/FeatureX/Z.cs", false)]
    // Row 8 — `**` spans `a`
    [InlineData("src/Feature/**", "src/Feature/a/b.cs", true)]
    // Row 9 — bare dir normalizes to `src/Feature/**`
    [InlineData("src/Feature", "src/Feature/x.cs", true)]
    // Row 10 — a directory scope matches files *under* it, not the dir entry itself
    [InlineData("src/Feature", "src/Feature", false)]
    // Row 11 — empty scope [] owns nothing; "" is the sentinel for [] (converted in the test body)
    [InlineData("", "anything/x.cs", false)]
    // Row 12 — universal scope ["**"] owns everything
    [InlineData("**", "anything/x.cs", true)]
    // Row 13 — single `*` is one level only
    [InlineData("src/A/*", "src/A/B/c.cs", false)]
    // Row 14 — one level matches
    [InlineData("src/A/*", "src/A/b.cs", true)]
    // Row 15 — `*` matches `Foo`, `**` spans the rest
    [InlineData("src/*/Tests/**", "src/Foo/Tests/X.cs", true)]
    // Row 16 — top-level extension glob
    [InlineData("*.md", "README.md", true)]
    // Row 17 — `*.md` is one segment; not under `docs/`
    [InlineData("*.md", "docs/README.md", false)]
    // Row 18 — literal SUFFIX after `*` — segment must END in `Tests`
    [InlineData("src/*Tests/**", "src/UnitTests/X.cs", true)]
    // Row 19 — suffix `Tests` must be the segment END; `UnitTestsExtra` ends in `Extra` (PERMISSIVE-BUG TRAP → false)
    [InlineData("src/*Tests/**", "src/UnitTestsExtra/X.cs", false)]
    // Row 20 — multiple `*` in one segment — both consumed (`foo` `-` `bar` `.cs`)
    [InlineData("src/*-*.cs", "src/foo-bar.cs", true)]
    // Row 21 — the literal `-` between the two `*`s must appear; `foobar` has none (PERMISSIVE-BUG TRAP → false)
    [InlineData("src/*-*.cs", "src/foobar.cs", false)]
    // Row 22 — bounded mid-`**` — first `**` spans `x`, literal `b` reappears, second `**` spans `y`
    [InlineData("a/**/b/**", "a/x/b/y.cs", true)]
    // Row 23 — bounded mid-`**`: no `b` segment between the two `**`s (PERMISSIVE-BUG TRAP → false)
    [InlineData("a/**/b/**", "a/x/c/y.cs", false)]
    // Row 24 — leading `**/` — `**` spans `src/x`, final segment matches `*.cs`
    [InlineData("**/*.cs", "src/x/Thing.cs", true)]
    // Row 25 — leading `**/`: depth spans, but final segment is not `*.cs` (PERMISSIVE-BUG TRAP → false)
    [InlineData("**/*.cs", "src/x/Thing.txt", false)]
    // Row 26 — `*`-matches-EMPTY — `Feat*` matches the bare segment `Feat`, then `**` spans `x`
    [InlineData("src/Feat*/**", "src/Feat/x.cs", true)]
    // Row 27 — `*`-matches-empty at the leaf: `Feat*` matches exactly `Feat`
    [InlineData("src/Feat*", "src/Feat", true)]
    public void IsInScope_TruthTable(string glob, string path, bool expected)
    {
        // Row 11 sentinel: "" → empty scope [].
        // An empty scope owns nothing regardless of path (§2.1(a)).
        IReadOnlyList<string> scope = glob == "" ? [] : [glob];

        bool actual = WriteScope.IsInScope(path, scope);

        Assert.Equal(expected, actual);
    }

    // =========================================================================
    // Issue #136 — a trailing slash is an EXPLICIT directory marker.
    // 'src/Foo/' (and the dotted 'src/Foo.Bar/') must claim their nested files,
    // not collapse to 'dir//**' (an empty segment matching nothing). Kept as a
    // separate theory so the pinned §2.1(d) 27-row table stays verbatim.
    // =========================================================================

    [Theory]
    // trailing-slash dir claims nested files
    [InlineData("src/Feature/", "src/Feature/x.cs", true)]
    // ...but not the bare dir entry itself (matches row-10 semantics)
    [InlineData("src/Feature/", "src/Feature", false)]
    // the #136 repro: a DOTTED directory name with a trailing slash
    [InlineData("src/TextTools.Strings/", "src/TextTools.Strings/Slug.cs", true)]
    [InlineData("tests/TextTools.Tests/", "tests/TextTools.Tests/SlugTests.cs", true)]
    // dotfile directory + trailing slash stays in scope (no regression of the WS_1 .github case)
    [InlineData(".github/", ".github/workflows/ci.yml", true)]
    // a file literal (no trailing slash, real extension) still matches exactly
    [InlineData("src/Thing.cs", "src/Thing.cs", true)]
    // a trailing-slash dir does not over-claim a sibling directory
    [InlineData("src/Feature/", "src/Other/x.cs", false)]
    public void IsInScope_TrailingSlashDirectory_Issue136(string glob, string path, bool expected)
    {
        Assert.Equal(expected, WriteScope.IsInScope(path, [glob]));
    }

    // =========================================================================
    // §2.2 — Generative / property tests, seeded and reproducible
    //
    // Seed 0x08C0FFEE (plan-08 + COFFEE mnemonic).  A failing counterexample from
    // the seed below replays exactly: re-run the method with the same seed.
    //
    // Both tests are RED against a naive permissive matcher:
    //   - MembershipImpliesOverlap: the naive IsInScope wrongly returns true for
    //     paths outside the scope (trap rows).  The property then demands
    //     Overlaps(S, [p]) = true, but [p] as a literal can never overlap with a
    //     scope that shouldn't include it — contradiction → RED.
    //   - OverlapsCompleteness: constructed pairs with suffix (`*Tests`), multi-star
    //     (`*-*`), and bounded-`**` shapes expose a naive structural Overlaps that
    //     correctly handles prefix/suffix constraints but fails to detect genuine
    //     overlap when the shared witness is reachable only through those constraints.
    // =========================================================================

    /// <summary>
    /// Membership-implies-overlap (§2.2 property 1):
    /// For any non-empty scope S and path p, if <c>IsInScope(p, S)</c> then
    /// <c>Overlaps(S, [p])</c> — where <c>[p]</c> is the scope whose sole entry is
    /// the literal path <c>p</c> (no wildcards; matches exactly <c>p</c>).
    ///
    /// Catches the historical divergence where a permissive <c>IsInScope</c> returns
    /// true for a path that is structurally impossible to reach from the scope globs,
    /// while a structurally-correct <c>Overlaps</c> correctly returns false.
    /// </summary>
    [Fact]
    public void MembershipImpliesOverlap()
    {
        const int Seed = 0x08C0FFEE;
        var rng = new Random(Seed);
        int propertyChecked = 0;

        for (int i = 0; i < 5000; i++)
        {
            string[] scope = GenerateScope(rng);
            if (scope.Length == 0) continue;

            string path = GeneratePath(rng);

            if (!WriteScope.IsInScope(path, scope))
                continue;

            // Property: if p is in scope S, then S overlaps the literal-path scope [p].
            // A literal-path scope only matches exactly p, so Overlaps(S, [p]) is true
            // iff S contains a glob that can reach p — which IsInScope just confirmed.
            string[] literalScope = [path];
            bool overlaps = WriteScope.Overlaps(scope, literalScope);

            Assert.True(
                overlaps,
                $"MembershipImpliesOverlap violated at iteration {i} (seed=0x{Seed:X}):\n" +
                $"  IsInScope(\"{path}\", [{string.Join(", ", scope.Select(s => $"\"{s}\""))}]) = true\n" +
                $"  Overlaps([...], [\"{path}\"]) = false\n" +
                $"  If a path is in scope, the scope must overlap the singleton containing that path.");

            propertyChecked++;
        }

        // Sanity guard: ensure the generator produced enough IsInScope=true cases to
        // make the property non-vacuous.
        Assert.True(propertyChecked >= 100,
            $"Only {propertyChecked} IsInScope=true cases were generated in 5000 tries " +
            $"(seed=0x{Seed:X}); check GenerateScope / GeneratePath to produce more matches.");
    }

    /// <summary>
    /// Overlaps completeness — no false negatives (§2.2 property 2):
    /// For scope pairs (A, B) that share a CONSTRUCTED witness path w
    /// (<c>IsInScope(w, A) ∧ IsInScope(w, B)</c> by construction), <c>Overlaps(A, B)</c>
    /// must be <c>true</c>.
    ///
    /// Catches an <c>Overlaps</c> implementation that under-detects genuine overlap when
    /// the shared path is reachable via suffix wildcards (<c>*Tests</c>), multi-star
    /// segments (<c>*-*</c>), or bounded mid-<c>**</c> patterns (<c>a/**/b/**</c>) — the
    /// shapes where a naive structural check can diverge from IsInScope's reachability.
    /// </summary>
    [Fact]
    public void OverlapsCompleteness()
    {
        const int Seed = 0x08C0FFEE;
        var rng = new Random(Seed);

        for (int i = 0; i < 3000; i++)
        {
            (string witness, string[] scopeA, string[] scopeB) = ConstructOverlappingPair(rng);

            // Verify construction: both scopes must accept the witness.
            // These assertions are on the construction logic, not the property itself;
            // a failure here means ConstructOverlappingPair has a bug, not the matcher.
            Assert.True(
                WriteScope.IsInScope(witness, scopeA),
                $"Construction error at iteration {i}: IsInScope(\"{witness}\", [{string.Join(", ", scopeA.Select(s => $"\"{s}\""))}]) should be true by construction");
            Assert.True(
                WriteScope.IsInScope(witness, scopeB),
                $"Construction error at iteration {i}: IsInScope(\"{witness}\", [{string.Join(", ", scopeB.Select(s => $"\"{s}\""))}]) should be true by construction");

            // Property: two scopes that share a witness must report as overlapping.
            Assert.True(
                WriteScope.Overlaps(scopeA, scopeB),
                $"OverlapsCompleteness violated at iteration {i} (seed=0x{Seed:X}):\n" +
                $"  Witness: \"{witness}\"\n" +
                $"  ScopeA:  [{string.Join(", ", scopeA.Select(s => $"\"{s}\""))}]\n" +
                $"  ScopeB:  [{string.Join(", ", scopeB.Select(s => $"\"{s}\""))}]\n" +
                $"  Both IsInScope checks returned true for the witness, so Overlaps must be true.");
        }
    }

    // =========================================================================
    // Generators — shared by both property tests
    // =========================================================================

    private static readonly string[] Prefixes = ["src", "tests", "lib", "a", "docs"];
    private static readonly string[] Dirs = ["Feature", "Components", "UnitTests", "b", "Core", "Feat"];
    private static readonly string[] Names = ["X", "Thing", "foo-bar", "a-b", "Widget", "Z"];
    private static readonly string[] Extensions = [".cs", ".json", ".md", ".ts", ".txt"];

    /// <summary>
    /// Generate a random scope (0–2 entries), covering the shapes from the §2.1(d) truth
    /// table: prefix-star, suffix-star, multi-star, double-star, bounded mid-**, leading **.
    /// </summary>
    private static string[] GenerateScope(Random rng)
    {
        int count = rng.Next(0, 3); // 0, 1, or 2 entries
        if (count == 0) return [];
        return Enumerable.Range(0, count).Select(_ => RandomGlob(rng)).ToArray();
    }

    /// <summary>
    /// Generate a random file path with 1–3 directory components.
    /// All path components are drawn from the same vocabulary as the glob generator
    /// so hits and misses are roughly balanced.
    /// </summary>
    private static string GeneratePath(Random rng)
    {
        string p1 = Prefixes[rng.Next(Prefixes.Length)];
        string name = Names[rng.Next(Names.Length)] + Extensions[rng.Next(Extensions.Length)];

        return rng.Next(3) switch
        {
            0 => $"{p1}/{name}",
            1 => $"{p1}/{Dirs[rng.Next(Dirs.Length)]}/{name}",
            _ => $"{p1}/{Dirs[rng.Next(Dirs.Length)]}/{Dirs[rng.Next(Dirs.Length)]}/{name}"
        };
    }

    /// <summary>
    /// Generate a single random glob pattern covering every §2.1(a) shape:
    /// pure-literal dir, prefix-star, suffix-star, multi-star (interior literal),
    /// double-star extension, bounded mid-**, leading **, and bare-dir normalization.
    /// </summary>
    private static string RandomGlob(Random rng)
    {
        string p = Prefixes[rng.Next(Prefixes.Length)];
        string d = Dirs[rng.Next(Dirs.Length)];
        string ext = Extensions[rng.Next(Extensions.Length)];

        return rng.Next(12) switch
        {
            0  => $"{p}/**",                    // pure double-star under prefix
            1  => $"{p}/**/*{ext}",             // double-star + extension suffix
            2  => $"{p}/{d[..Math.Min(3, d.Length)]}*/**",  // prefix-star segment
            3  => $"{p}/*{d[Math.Max(0, d.Length - 5)..]}/**",  // suffix-star segment
            4  => $"{p}/*-*{ext}",              // multi-star with interior literal `-`
            5  => $"**/*{ext}",                 // leading double-star + extension
            6  => $"{p}/{d}/**",                // exact dir + double-star (bare-dir equivalent)
            7  => $"a/**/b/**",                 // bounded mid-** (row 22/23 shape)
            8  => $"{p}/{d}",                   // bare dir (normalises to {p}/{d}/**)
            9  => $"*{ext}",                    // top-level extension glob
            10 => $"{p}/*/{d}/**",              // wildcard mid-segment
            _  => $"{p}/*{d[Math.Max(0, d.Length - 4)..]}s/**" // suffix-star + trailing 's'
        };
    }

    /// <summary>
    /// Construct a pair (A, B) that provably share a witness path w, covering the
    /// §2.1(d) trap shapes: suffix-star, multi-star (interior `-`), bounded mid-**,
    /// and prefix-star.  ScopeA and ScopeB are built from DIFFERENT glob shapes so
    /// Overlaps cannot cheat by detecting syntactic identity.
    /// </summary>
    private static (string witness, string[] scopeA, string[] scopeB) ConstructOverlappingPair(Random rng)
    {
        string p = Prefixes[rng.Next(Prefixes.Length)];
        string ext = Extensions[rng.Next(Extensions.Length)];

        return rng.Next(8) switch
        {
            // Shape A: suffix-star vs double-star-under-prefix
            // e.g. witness=src/UnitTests/Thing.cs, A=["src/*Tests/**"], B=["src/**"]
            0 => (
                $"{p}/UnitTests/Thing{ext}",
                [$"{p}/*Tests/**"],
                [$"{p}/**"]),

            // Shape B: multi-star (interior `-`) vs exact-dir double-star
            // e.g. witness=src/foo-bar.cs, A=["src/*-*.cs"], B=["src/**"]
            1 => (
                $"{p}/foo-bar{ext}",
                [$"{p}/*-*{ext}"],
                [$"{p}/**"]),

            // Shape C: bounded mid-** vs exact-path literal
            // e.g. witness=a/x/b/y.cs, A=["a/**/b/**"], B=["a/x/b/**"]
            2 => (
                $"a/x/b/y{ext}",
                ["a/**/b/**"],
                ["a/x/b/**"]),

            // Shape D: prefix-star vs double-star-with-extension
            // e.g. witness=src/Feature/X.cs, A=["src/Feat*/**"], B=["src/**/*.cs"]
            3 => (
                $"{p}/Feature/X{ext}",
                [$"{p}/Feat*/**"],
                [$"{p}/**/*{ext}"]),

            // Shape E: leading-** vs prefix double-star
            // e.g. witness=src/x/Thing.cs, A=["**/*.cs"], B=["src/**"]
            4 => (
                $"{p}/x/Thing{ext}",
                [$"**/*{ext}"],
                [$"{p}/**"]),

            // Shape F: suffix-star vs prefix-star (overlap only through the shared literal chars)
            // e.g. witness=src/UnitTests/X.cs, A=["src/*Tests/**"], B=["src/Unit*/**"]
            5 => (
                $"{p}/UnitTests/X{ext}",
                [$"{p}/*Tests/**"],
                [$"{p}/Unit*/**"]),

            // Shape G: multi-star (interior `-`) vs leading-** with extension
            // e.g. witness=src/foo-bar.cs, A=["src/*-*.cs"], B=["**/*.cs"]
            6 => (
                $"{p}/foo-bar{ext}",
                [$"{p}/*-*{ext}"],
                [$"**/*{ext}"]),

            // Shape H: bounded mid-** vs leading-**
            // e.g. witness=a/x/b/y.cs, A=["a/**/b/**"], B=["**/*.cs"]
            _ => (
                $"a/x/b/y{ext}",
                ["a/**/b/**"],
                [$"**/*{ext}"])
        };
    }
}
