// A correct InterpreterMap: BOTH pwsh templates route through the shim.
//
// This sample carries the #561 trap the guardrail's preprocessing must survive. The literal below
// spells an UNCLOSED `/*`, and a later literal spells the matching `*/`, with PowershellTemplate
// sitting between them. Under the old "strip comments FIRST" order those two pair up and blank
// PowershellTemplate entirely, so the guardrail reports a correctly-routed file as unrouted - a
// false RED on the exact shape this repo's guardrails inspect (glob spellings in source).
internal static class InterpreterMap
{
    private const string ScriptToken = "<script>";
    private const string ArgsToken = "<args>";

    // Opens a phantom block comment under the wrong order - there is no `*/` inside this literal:
    private const string SourceGlob = "src/*";

    private static IReadOnlyList<string> PwshTemplate =>
        ["pwsh", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ShimScript, ScriptToken, ArgsToken];

    private static IReadOnlyList<string> PowershellTemplate =>
        ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ShimScript, ScriptToken, ArgsToken];

    // ... and the closer of the phantom pair, also inside a literal:
    private const string BinGlob = "bin/**/Debug";
}
