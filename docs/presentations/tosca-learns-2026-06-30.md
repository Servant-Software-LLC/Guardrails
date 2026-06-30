# Are We Missing a Step in Agentic Engineering?

### Tosca Learns · 2026-06-30 · David Maltby

*An honest question about how we ship AI-written code — not a pitch. The last few slides
show one answer I happen to have built, kept deliberately high-level.*

---

## Timing guide
| Section | Slides | Time |
|---|---|---|
| The question + the one-shot we all do | 1–2 | 2 min |
| What the one-shot hides (3 problems) | 3–6 | 5 min |
| My first guess: Beads | 7–8 | 2 min |
| One answer: Guardrails (a taste) | 9–11 | 3 min |
| Back to the question | 12 | 1 min |

---

## SLIDE 1 — Title

# Are we missing a step in Agentic Engineering?

*David Maltby · Tosca Learns · June 2026*

> **Speaker note:** I want to ask a question, not sell anything. We've all gotten very good at
> getting an AI agent to write code for us. I think there might be a step in the middle we've
> quietly skipped — and I want to spend ten minutes on what that step is and why it matters. At
> the very end I'll show you one thing I built that fills it, but honestly the question is the
> point, not my tool.

---

## SLIDE 2 — How we build with agents today

The loop almost everyone has settled into:

```
   write a plan          hand the WHOLE file          a big PR
   in Markdown     ──►    to an agent: "implement"  ──►  of changes
   (plan.md)             one prompt · one shot
```

It's fast. It often works. It feels like magic.

**But step back and ask: what did we actually *verify*?** We reviewed the prose going in,
and the diff coming out. **Everything in between — the agent just *did*.**

> **Speaker note:** Here's the workflow. You write a nice plan in markdown — maybe a really good
> one. You paste the whole thing into an agent and say "implement this." One prompt, one shot, and
> out comes a pull request. And it's genuinely impressive how often this works. But notice what we
> reviewed: the plan on the way in, and the diff on the way out. Everything in the middle — how the
> work was split up, in what order, what got checked — the agent did on its own, and we never saw
> it. The next three slides are about what lives in that gap.

---

## SLIDE 3 — Problem 1: The breakdown is invisible

A markdown plan is **prose**. It doesn't show a **DAG**.

So the agent silently decides the things that matter most:
- how to **split** the work into tasks
- what **order** to do them in
- what **depends** on what

**That decomposition is the real engineering** — and it's **hidden from review.** You can't
review a plan-of-work that was never written down.

> *From real runs:* a task compiled against a type a **different** task hadn't produced yet —
> a dependency nothing in the plan ever surfaced.

> **Speaker note:** First problem. A plan in markdown is prose — it reads top to bottom. But real
> work is a graph: this task depends on that one, these two are independent, that one has to come
> last. When you one-shot a markdown file, the agent invents that graph in its head and never shows
> you. And the breakdown is arguably the most important engineering decision in the whole job — get
> the dependencies wrong and everything downstream is shaky. We literally hit this: one task was
> writing code that referenced a type another task was supposed to create first, and nothing in the
> plan made that ordering visible. You can't review a structure that doesn't exist on paper.
> *(Your reference, not the audience's: that was issue #176 — a failure mode the gate caught by dogfooding.)*

---

## SLIDE 4 — Problem 2: The agent grades its own homework

Who decides a task is **done**? The agent.

"Definition of done" becomes **whatever the model's output claims** — there's no deterministic
check that the claim is *true*.

**Green ≠ correct:**
- all the tests pass… over a feature that was **never actually wired up**
- a "tests pass" check that happily passes on code that **doesn't even compile**

A prompt is very good at telling you **what you want to hear.**

> **Speaker note:** Second problem, and it's the big one: determinism. In a one-shot, the thing that
> decides whether the work succeeded is… the same model that did the work. Definition of done is
> whatever it says in its summary — "Done! All tests pass." But "the agent says it's done" is not a
> proof. We've seen all-green test suites sitting on top of a feature that was never connected to
> anything — the tests passed because they tested the wrong layer. We've seen a "tests pass" check
> report success on code that didn't compile. A language model is, by design, a machine for producing
> plausible-sounding output. If you let it certify its own work, plausible is all you're guaranteed.
> *(Your reference: those were issues #120 and #155.)*

---

## SLIDE 5 — Problem 3: …and it'll move the goalposts to pass

Tell an agent *"make the tests pass,"* and one of the easiest paths is to **edit the test.**

Or it quietly writes files **outside the task it was given** — touching things it shouldn't.

In a one-shot, **nothing stops either.** You get a green check and a quiet lie.

> *The fix is deterministic, not hopeful:* the implementation task is **only allowed to write the
> source** — its scope **excludes the test files**, so editing a test to pass is a hard,
> mechanical failure.

> **Speaker note:** Third problem follows straight from the second. If the agent both does the work
> and decides the work is done, then the path of least resistance can be to game the check. Told
> "make the failing test pass," editing the test is a perfectly valid way to make it pass — and a
> terrible way to make the code correct. Same with scope: an agent fixing one thing wanders off and
> edits five other files, and in a one-shot you'd never notice until something breaks later. Here's
> the thing — this isn't solved by a better prompt or a sterner instruction. It's solved by a
> deterministic rule: the implementation task is mechanically only allowed to touch the source
> files, not the tests. Edit a test and it's an automatic failure, full stop. No trust required.
> *(Your reference: that scope-enforcement work was issue #136.)*

---

## SLIDE 6 — The pattern: a prompt proposes, nothing certifies

Line the three up and the same gap appears every time:

| What we one-shot | What's missing |
|---|---|
| The agent **decides** the breakdown | a **visible DAG** you can review |
| The agent **writes** each task's prompt in its head | the **prompts**, out in the open |
| The agent **declares** it's done | a **deterministic gate** that proves it |

We've automated the **proposing**. We **skipped the certifying.**

> ### That skipped step is the question.

> **Speaker note:** So here's the synthesis. Across all three problems it's the same shape: the agent
> proposes — it proposes a breakdown, it proposes the work, it proposes that it's finished — and
> nothing independent and deterministic ever certifies any of it. We've gotten incredibly good at the
> proposing half and we've quietly skipped the certifying half. That missing certifying step — a
> breakdown you can see, prompts you can read, and a gate that mechanically proves "done" — that's
> the step I think we might be missing. Everything from here is just: what would filling it look like?

---

## SLIDE 7 — My first guess: Beads

Back in March I thought I'd found the step. **Beads** —
*"a distributed graph issue tracker for AI agents."*

A real **dependency graph** of tasks, persistent across long sessions, with `ready`-task
detection. Genuinely clever. I was hooked.

*(Full disclosure: a certain **Gastown** obsession may have helped.)*

> **Speaker note:** My first attempt at filling the gap wasn't to build anything — it was to adopt
> something. Back in March I found Beads, which bills itself as a distributed graph issue tracker for
> AI agents. And it's a genuinely smart project: instead of a flat markdown to-do list, your tasks
> live in a real dependency graph, it persists across long agent sessions, it can tell an agent which
> tasks are "ready" because their blockers are done. I was sold. I'll admit the name didn't hurt —
> anyone who knows me knows I have a bit of a Gastown thing. So for a while I thought: this is the
> missing step. It wasn't — and the way it fell short is what told me what the step actually is.

---

## SLIDE 8 — Why Beads fell short (for me)

It organized the work — but left the *engineering judgment* with the agent:

| | Beads |
|---|---|
| **1. Who runs the tasks** | the **agents** managed their own tasks |
| **2. Can I see it** | tasks **and** dependency chains were **hard to visualize** |
| **3. Can I see the prompts** | **no** — the prompt used for each task wasn't visible |
| **4. Who defines "done"** | left to the **agent** performing the task |

Same gap as the one-shot — just better organized. **The human still wasn't in the loop where it
counts.**

> **Speaker note:** Here's where it came up short, for my purposes. One: the agents managed the
> tasks — claimed them, updated them, closed them — so I was still trusting the agent to drive.
> Two: I couldn't easily *see* it. The tasks and especially the dependency chains were hard to
> visualize; there was no picture I could put in front of a reviewer. Three: I couldn't see the
> prompt that drove each task — the actual instruction was invisible. And four, the big one:
> definition of done was still left to the agent doing the task. So Beads organized the work
> beautifully, but it left every piece of *engineering judgment* — what's done, what's correct —
> with the agent. It's the same gap as the one-shot, just tidier. That's when I stopped looking for
> a tool to adopt and started building the missing step itself.

---

## SLIDE 9 — One answer: Guardrails (kept deliberately simple)

Not a prescription — just what I built to fill the gap. The same four steps, but the **middle is
now visible and verified:**

```
  ① Write a Plan  →  ② Break It Down  →  ③ Review  →  ④ Execute
     (Markdown)       a VISIBLE DAG        a human       each task proves
                      of task folders      signs off     it worked
```

| The gap | How this fills it |
|---|---|
| breakdown was invisible | a **DAG of task folders** you open and review |
| prompts were hidden | every task's **prompt is a file**, in the open |
| "done" was the agent's word | **deterministic guardrails** — executable pass/fail checks |
| agent owned the structure | **you** review and sign off **before** anything runs |

> **Speaker note:** So this is the thing I built, and I'm going to keep it deliberately high-level
> because the mechanism matters less than the shape. It's the same four steps you'd expect — write a
> plan, break it down, review, execute — but the middle two, the part the one-shot hides, are now out
> in the open. The breakdown is a real DAG you can look at: a folder per task, with its dependencies.
> Each task's prompt is a plain file you can read and edit. And critically, "done" for a task isn't
> the agent's opinion — it's a set of deterministic guardrails, ordinary executable checks that pass
> or fail. And before a single task runs, a human reviews the whole structure. Every row in that table
> is just one of the problems from earlier, turned around.

---

## SLIDE 10 — The one idea

If you remember one sentence:

> ### "A prompt may propose — only a deterministic gate may certify."

Let the AI do **more** — by **trusting it less**. The agent proposes the work; a cheap,
deterministic check decides whether it counts.

> **Speaker note:** If you take one thing away, it's this line. A prompt may propose — only a
> deterministic gate may certify. The agent is allowed to be creative, fast, even brilliant — it
> proposes. But the thing that decides whether the work actually counts is never the prompt; it's a
> cheap, boring, deterministic check. The trick isn't trusting the AI more — it's trusting it less,
> in exactly the right place, so you can safely let it do far more everywhere else. That's the whole
> idea, and it's the step I think the one-shot skips. And to be clear — the gate certifies what you *can*
> make deterministic (build, tests, scope, wiring); the genuinely subjective calls stay with the human review
> step. Put another way: the human review is where you decide what "done" *means* for a task; the gate just
> enforces that decision so the agent can't quietly redefine it.

---

## SLIDE 11 — See it for yourself

A tiny public demo — **already broken down** (6 tasks), ready to run. Watch **two tasks run in
parallel** and an **AI resolve a real merge conflict behind a deterministic gate.**

```bash
# 1. install the harness (latest preview)
dotnet tool install -g ServantSoftware.Guardrails --version 1.0.0-preview.34

# 2. clone the pre-broken-down demo
git clone https://github.com/DaveRMaltby/guardrails-texttools-demo
cd guardrails-texttools-demo

# 3. see the breakdown, preview the run, then run it
guardrails graph   texttools     # writes diagram.html — the visible DAG
guardrails run     texttools --dry-run   # preview waves, no cost
guardrails run     texttools     # execute — each task proves it worked
```

*Requires .NET 8+, git, and the `claude` CLI. Open `texttools/diagram.html` to see the breakdown
you'd normally never get to review. (`--dry-run` is a safe, zero-cost preview.)*

> **Speaker note:** And if you want to feel it rather than take my word, here's a tiny public repo
> you can run yourself — I'll be running this exact one in the demo. It's already broken down, so you
> can skip straight to looking at the structure and running it. Install the tool, clone the repo,
> and the two commands that matter are `graph` — which opens that visible DAG, the thing the one-shot
> never shows you — and `run`, which executes it with every task proving itself. The fun part to
> watch: two tasks run in parallel, both edit the same file, and an AI resolves the merge conflict —
> but only after a deterministic re-verify says the merged result actually builds and passes. Try it
> on your own machine while I run it up here.

---

## SLIDE 12 — Back to the question

# So — are we missing a step?

I think we might be. Between *"write a plan"* and *"ship the diff"* there's a step where a human
should still see the breakdown, read the prompts, and trust a **gate**, not a **claim.**

**I'm not prescribing. Just putting it on the table.**

*Demo repo: `github.com/DaveRMaltby/guardrails-texttools-demo` · Project:
`github.com/Servant-Software-LLC/Guardrails`*

> **Speaker note:** So I'll end where I started, with the question. Are we missing a step in agentic
> engineering? I think we might be — a step between writing the plan and shipping the diff, where a
> human still gets to see the breakdown, read the prompts, and rely on a deterministic gate instead
> of the agent's word. I built one answer to that, but I'm genuinely not here to prescribe it. I just
> wanted to put the question on the table, because I don't think we talk about that middle step
> enough. Happy to run the demo, and happier to argue about whether the step is real. Thank you.

---

*End of presentation*
