# Attempt 1 of task '01-author-tests-diagram-status-overlay-renderer' failed

Task: Author failing tests + minimal stubs for a per-node status-overlay rendering capability in HtmlDiagramRenderer

Fix the specific problems below. Do NOT start over from scratch — keep what
already works and address only what failed.

## Failed guardrails
### 02-tests-fail-on-stubs
Checks: The new status-overlay tests compile and genuinely fail against the throwing stub (TDD red)
Reason: Determining projects to restore...
## Full output (tail)
```
String mermaidSource, String sourceHash, IReadOnlyDictionary`2 taskFolderTargets, IReadOnlyDictionary`2 nodeStatuses, Boolean includeRefresh) in C:\Users\David\AppData\Local\Temp\guardrails-worktrees\diagram-live-status-and-search-80c8913d\71e1ab5c\01-author-tests-diagram-status-overlay-renderer\attempt-1\src\Guardrails.Core\Graph\HtmlDiagramRenderer.cs:line 147
   at Guardrails.Core.Tests.HtmlDiagramRendererTests.Render_WithNodeStatuses_EmitsAMetaRefreshTag() in C:\Users\David\AppData\Local\Temp\guardrails-worktrees\diagram-live-status-and-search-80c8913d\71e1ab5c\01-author-tests-diagram-status-overlay-renderer\attempt-1\tests\Guardrails.Core.Tests\HtmlDiagramRendererTests.cs:line 408
   at System.RuntimeMethodHandle.InvokeMethod(Object target, Void** arguments, Signature sig, Boolean isConstructor)
   at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj, BindingFlags invokeAttr)
  Failed Guardrails.Core.Tests.HtmlDiagramRendererTests.Render_WithNodeStatuses_EmitsAnOverlayForEachStatusEntry [< 1 ms]
  Error Message:
   System.NotImplementedException : Live per-node status overlay rendering (nodeStatuses) is not implemented yet — see task 02-implement-diagram-status-overlay-renderer (issue #219). Call Render without nodeStatuses for the static graph.
  Stack Trace:
     at Guardrails.Core.Graph.HtmlDiagramRenderer.Render(String mermaidSource, String sourceHash, IReadOnlyDictionary`2 taskFolderTargets, IReadOnlyDictionary`2 nodeStatuses, Boolean includeRefresh) in C:\Users\David\AppData\Local\Temp\guardrails-worktrees\diagram-live-status-and-search-80c8913d\71e1ab5c\01-author-tests-diagram-status-overlay-renderer\attempt-1\src\Guardrails.Core\Graph\HtmlDiagramRenderer.cs:line 147
   at Guardrails.Core.Tests.HtmlDiagramRendererTests.Render_WithNodeStatuses_EmitsAnOverlayForEachStatusEntry() in C:\Users\David\AppData\Local\Temp\guardrails-worktrees\diagram-live-status-and-search-80c8913d\71e1ab5c\01-author-tests-diagram-status-overlay-renderer\attempt-1\tests\Guardrails.Core.Tests\HtmlDiagramRendererTests.cs:line 443
   at System.RuntimeMethodHandle.InvokeMethod(Object target, Void** arguments, Signature sig, Boolean isConstructor)
   at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj, BindingFlags invokeAttr)
  Failed Guardrails.Core.Tests.HtmlDiagramRendererTests.Render_WithRunningStatus_EmitsASpinnerClass_AndSettledStatusesEmitAGlyph [< 1 ms]
  Error Message:
   System.NotImplementedException : Live per-node status overlay rendering (nodeStatuses) is not implemented yet — see task 02-implement-diagram-status-overlay-renderer (issue #219). Call Render without nodeStatuses for the static graph.
  Stack Trace:
     at Guardrails.Core.Graph.HtmlDiagramRenderer.Render(String mermaidSource, String sourceHash, IReadOnlyDictionary`2 taskFolderTargets, IReadOnlyDictionary`2 nodeStatuses, Boolean includeRefresh) in C:\Users\David\AppData\Local\Temp\guardrails-worktrees\diagram-live-status-and-search-80c8913d\71e1ab5c\01-author-tests-diagram-status-overlay-renderer\attempt-1\src\Guardrails.Core\Graph\HtmlDiagramRenderer.cs:line 147
   at Guardrails.Core.Tests.HtmlDiagramRendererTests.Render_WithRunningStatus_EmitsASpinnerClass_AndSettledStatusesEmitAGlyph() in C:\Users\David\AppData\Local\Temp\guardrails-worktrees\diagram-live-status-and-search-80c8913d\71e1ab5c\01-author-tests-diagram-status-overlay-renderer\attempt-1\tests\Guardrails.Core.Tests\HtmlDiagramRendererTests.cs:line 460
   at System.RuntimeMethodHandle.InvokeMethod(Object target, Void** arguments, Signature sig, Boolean isConstructor)
   at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj, BindingFlags invokeAttr)

Failed!  - Failed:     4, Passed:     1, Skipped:     0, Total:     5, Duration: 77 ms - Guardrails.Core.Tests.dll (net8.0)
---
Expected at least one failing test matching --filter FullyQualifiedName~Render_With, but none ran or none failed as expected. Check the filter matches the new test names.
```

Guardrails that PASSED (do not break these): 01-build-passes
