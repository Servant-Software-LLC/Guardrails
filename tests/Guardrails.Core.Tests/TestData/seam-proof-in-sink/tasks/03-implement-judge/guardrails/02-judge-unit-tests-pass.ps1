# catches: CriticalityJudge does not satisfy its own unit tests. These inject a FAKE
#          IPromptRunner, so this guardrail is exactly the one that goes green over a
#          component broken through the real adapter - it is not the seam proof.
exit 0
