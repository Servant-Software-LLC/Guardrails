namespace Guardrails.Integration.Tests;

/// <summary>
/// Serializes every test that constructs a <see cref="Guardrails.Cli.Ui.LiveRunObserver"/>.
/// </summary>
/// <remarks>
/// <para>
/// Spectre.Console's <c>DefaultExclusivityMode</c> is a <b>process-wide</b> lock, not a per-console one:
/// starting a second <c>LiveDisplay</c> while another is running throws
/// <c>"Trying to run one or more interactive functions concurrently"</c> — and the throw surfaces from
/// <c>DisposeAsync</c>, so it fails the test that was tearing down rather than the one that collided
/// with it. That misattribution is most of why this was hard to read from the CI log.
/// </para>
/// <para>
/// xUnit puts each test CLASS in its own collection by default and runs collections in parallel, so two
/// observer-driving classes are free to overlap. They did: CI went red on ubuntu only, while windows and
/// macos passed, because the collision is a timing race and the runners schedule differently.
/// </para>
/// <para>
/// <b>This was latent, not new.</b> Neither colliding class was touched by the change that exposed it —
/// the Stage 3 model-tiering merge (#201) added 15 integration tests elsewhere, which was enough to
/// shift scheduling and make an existing race land. A green suite before that merge was luck, not proof,
/// and the same luck held on two of three platforms afterwards.
/// </para>
/// <para>
/// <c>DisableParallelization</c> is deliberately set rather than relying on membership alone: sharing one
/// collection would serialize these classes against each other, but the lock is process-wide, so any
/// FUTURE test that starts a live display — in any other collection — would collide again. This makes the
/// constraint match the resource.
/// </para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LiveDisplayCollection
{
    public const string Name = "live-display (serialized: Spectre's exclusivity lock is process-wide)";
}
