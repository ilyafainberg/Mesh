---
id: builtin:skill:adhd
type: skill
name: Convert big reports into bite-sized decision making process
description: Turns complex, long agent reports into short, bite-sized decision points, guides the user through each choice, records every answer, and requests approval before executing the resulting plan.
roles: owner
triggers: agent responds
---

# Instructions

Help the user process a complex or lengthy report by converting it into a guided sequence of small decisions.

Workflow:
1. Identify the report to process. Use the report already present in the conversation when unambiguous; otherwise ask the user to provide or identify it.
2. Read the full report before presenting decisions. Extract only decisions that require user judgment, approval, prioritization, selection, or acceptance of a tradeoff. Do not turn factual background or already-settled items into questions.
3. Group related decisions and order them by dependency and impact. Resolve foundational choices before dependent ones.
4. Present exactly one decision point at a time so the user is never faced with a long questionnaire.
5. For each decision point, provide:
   - A short plain-language heading.
   - One or two sentences explaining why the decision matters.
   - Two to five concrete options with concise benefits, drawbacks, and meaningful consequences.
   - A clearly marked recommendation when the evidence supports one, with a brief reason.
   - An "Other" or free-text path when the listed options may not cover the user's preference.
6. Use the interactive user-choice tool when two to five discrete options are available. Use free-text input when the answer cannot be reduced to discrete options. After asking, stop and wait for the user's response before moving to the next decision.
7. Record each answer faithfully. If an answer is ambiguous or conflicts with an earlier answer, pause and ask a focused clarification rather than guessing.
8. After every three to five completed decisions, show a very short progress checkpoint: decisions completed, decisions remaining, and any dependency affected. Do not repeat the full report.
9. Continue until every material decision in the report is resolved. Explicitly identify any unresolved decision, assumption, risk acceptance, or deferred item.
10. Produce one consolidated decision report containing:
    - Objective and scope.
    - A table with columns: #, Decision, Chosen answer, Key rationale, Consequence, Status.
    - Assumptions and constraints.
    - Deferred or unresolved items.
    - A sequenced execution plan derived from the approved answers.
    - Risks, safeguards, and rollback points where relevant.
11. Do not execute, send, publish, modify files, change settings, or perform other consequential actions while gathering decisions.
12. After showing the consolidated report, ask for explicit permission to execute the plan. Offer concise choices such as "Approve and execute", "Revise decisions", and "Stop here". Treat only an explicit approval as authorization.
13. If approved, execute only the agreed plan. If revisions are requested, return to the affected decision points, update the table, and ask for approval again.

Style:
- Keep each turn short, calm, and scannable.
- Use plain language and avoid unnecessary jargon.
- Never dump all decision points at once.
- Preserve critical facts, constraints, and caveats from the source report even while simplifying presentation.
- Distinguish recommendations from decisions the user has actually made.
- Never claim approval or infer consent from silence, vague agreement, or unrelated replies.