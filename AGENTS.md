# CoopGameServer: agent operating contract

Version: 2026-09-23.v2. Scope: this repository. Audience: coding agents.

Optimize for completed, verifiable changes with minimal repeated discovery. Keep
stable rules here and volatile task status in `docs/work/current.md`. System,
developer, and explicit user instructions take precedence. Resolve narrower
repository instructions for edited files. Links are read on demand, not automatically.

## 1. Establish the checkout

- Run `git rev-parse --show-toplevel`, `git status --short --branch`, and
  `git log -1 --oneline`. Use the verified root as subsequent command working directory.
- Confirm `CoopGameServer.slnx`, `src/`, and `tests/`. Discover filenames with
  `rg --files` before reading; do not invent service/repository paths.
- Find applicable `AGENTS.md` / `AGENTS.override.md`, including hidden locations when
  relevant. Exclude Git objects and build output from content discovery.
- Inspect nested same-name directories and untracked files before drawing conclusions.
  Their presence alone does not justify deletion, Git initialization, moving the
  checkout, or blocking unrelated work. Preserve pre-existing changes.

## 2. Load context selectively

- Resuming work: read [current handoff](docs/work/current.md); recheck volatile facts.
- Nontrivial code, API, persistence, or dependency change: read the
  [execution playbook](docs/engineering/agent-workflow.md).
- Formatting/commits: read [contributing rules](docs/contributing.md), `.editorconfig`,
  and `Directory.Build.props` as needed.
- Design: read only relevant `docs/adr/`, `docs/design/`, `docs/architecture/` files.
- Local services: use [local runbook](docs/runbooks/local-development.md),
  `compose.yaml`, and the relevant test fixture.
- Human explanation: [Korean companion](docs/engineering/agent-workflow.ko.md).
  Agents normally skip this explanatory duplicate.
- Follow the actual entry point through contracts, implementation, persistence, and
  representative tests. Previous tasks/Notion: start with one relevant summary/page;
  expand only to resolve a specific missing fact. Do not dump full histories.
- Batch independent reads, inspect every result, and bound output. Narrow truncated
  queries. Reuse retained results instead of refetching unchanged content.
- Code describes current behavior, accepted design describes intent, tests establish
  verification scope. Historical notes aid discovery. Resolve contradictions explicitly.
- Before starting a new week or trusting a Notion status page, compare its dated
  claims and completion checklist with local HEAD, remote main, CI for that commit,
  current code/tests, and docs/work/current.md. Record stale claims and missing
  deliverables; an old page must not become the implementation baseline.

## 3. Brief and execute

- Before nontrivial implementation, briefly state outcome, rationale, ownership,
  scope, commit grouping, and validation. Distinguish new behavior from connecting
  or regression-testing an existing guarantee.
- A briefing is informational unless the user explicitly requested approval first.
  Continue authorized work; do not ask again for already authorized actions.
- Make reversible implementation choices and record material assumptions. Ask only
  for necessary missing information, an explicit user gate, or permission required
  by the active environment. Finish independent work while waiting; time is not consent.
- Finish the active request through validation and handoff. Implementation requests
  do not end at a plan. Planning/documentation requests end at their own criteria.
- Default to one agent. This guide does not authorize delegation or mandate a reviewer.
  Use subagents only with explicit authorization and separable ownership.

## 4. Preserve correctness

- PostgreSQL owns durable correctness: transactions, constraints, persisted receipts.
  Redis must never become the final proof of exactly-once reward effects.
- API owns authentication, authorization, validation, and response mapping. Derive
  caller identity from validated claims, not an untrusted request player ID.
- Grain owns entity orchestration. Its scheduling cannot protect writes that bypass
  it; trace every writer before relying on serialized access.
- Domain rules stay independent of HTTP, Redis clients, and database infrastructure.
- Preserve replay/conflict semantics, connection ID/generation checks, and durable
  persistence before adopting candidate state on every affected path.
- Client cancellation does not undo an accepted durable operation. Read
  `docs/architecture/request-cancellation-and-retry.md` when changing that boundary.
- Cache design must specify fallback, invalidation, timeout, and freshness behavior.
- For load work, fix the scenario, valid business responses, data lifecycle, offered
  load, environment, and acceptance thresholds before the baseline. Recheck durable
  invariants after load. Optimize only a measured bottleneck; an unchanged result
  is preferable to an unjustified code change.

## 5. Edit and verify

- Implement one coherent slice. Avoid unrelated refactors, dependencies, or tooling.
- Document changed contracts, parameters, invariants, and failure ordering in code;
  retain Korean comments. Put long teaching explanations in separate documentation.
- Use the playbook validation matrix. Pure tests do not prove database behavior;
  controller-direct tests do not prove HTTP authentication/authorization.
- Completed code features require solution build and full regression validation.
  Use `--no-build` only for the exact source/configuration already built.
- Prose/instruction-only changes require link, consistency, and diff checks, not
  application tests, unless executable commands/configuration or behavior changed.
- Repeated identical failures require a new hypothesis or method, not blind retries,
  weaker tests, or premature abandonment while useful work remains.
- Never print `.env`, user secrets, JWTs, passwords, or resolved Compose secrets.
  Use `docker compose config -q` or `--services`. Preserve data volumes and unrelated
  Git changes; destructive cleanup needs applicable authorization.

## 6. Deliver with evidence

- Stage explicit task paths and review staged diff. Use Korean purpose-based commit
  subjects. Respect existing authorization for commits, pushes, and publication.
- Separate implementation, tests, commit, push, remote CI, and documentation status.
  Bind evidence to date, command/configuration, source state, and scope; label prior runs.
- Check every promised weekly artifact, including reports and raw measurements,
  before calling a week complete. Name deferred or missing artifacts separately
  even when code and CI pass.
- For code changes, record files and added/changed/removed functions, meaningful
  parameters/state, and reasons. Link details from a concise final report.
- Update `docs/work/current.md` at meaningful handoffs: current snapshot, outstanding
  decisions, next action. Do not append transcripts or put current test counts here.
- Communicate in Korean by default. Give substantive brief progress updates and a
  final report, not routine tool narration or repeated permission questions.
