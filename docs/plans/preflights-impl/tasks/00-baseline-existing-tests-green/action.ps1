# A no-op: this task does no work. Its guardrail (the existing test suite passes on the
# starting code) is the point - it gates the DAG root on the repo being green before any
# work task runs (#181). It writes nothing (no file, no state fragment) so a RED baseline
# short-circuits fast via the #174/#182 no-op-deadlock rule.
exit 0
