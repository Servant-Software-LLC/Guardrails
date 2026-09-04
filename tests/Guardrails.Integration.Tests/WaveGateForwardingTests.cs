namespace Guardrails.Integration.Tests;

/// <summary>
/// #513 — the decorator half. <c>WaveGateFinished</c> is a DEFAULT-METHOD member, so a transparent
/// decorator that does not DECLARE it inherits the empty body and swallows the event in every mode: the
/// renderer behind it never hears that a wave gate finished, and the gate stays unbadged exactly as it did
/// before the event existed.
///
/// <para>The reflection sweep that used to live here — over every <c>IRunObserver</c> decorator in the CLI
/// assembly, checking each declares <c>WaveGateFinished</c> — is superseded by
/// <c>RunEvents.ObserverForwardingSweepTests.EveryTransparentDecorator_DeclaresEveryIRunObserverMember</c>,
/// which sweeps EVERY member across BOTH assemblies (this file's sweep covered only
/// <c>Guardrails.Cli</c>, so the two <c>Guardrails.Core</c> projections were never checked). This file is
/// kept — empty of tests — as the place a future <c>WaveGateFinished</c>-specific BEHAVIOURAL test belongs,
/// mirroring <c>AttemptModelForwardingTests</c>' shape after the same supersession.</para>
/// </summary>
public sealed class WaveGateForwardingTests
{
}
