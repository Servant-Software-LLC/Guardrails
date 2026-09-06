// The defect this guardrail exists to catch: pwsh routes through the shim, the Windows fallback
// does NOT - so on a box without pwsh the exit-0 fail-open stays armed and nothing says so.
internal static class InterpreterMap
{
    private const string ScriptToken = "<script>";
    private const string ArgsToken = "<args>";

    private static IReadOnlyList<string> PwshTemplate =>
        ["pwsh", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ShimScript, ScriptToken, ArgsToken];

    private static IReadOnlyList<string> PowershellTemplate =>
        ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ScriptToken, ArgsToken];
}
