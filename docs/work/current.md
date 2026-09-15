# Current work handoff

Updated: 2026-09-14 (Asia/Seoul). Snapshot, not live status.
Follow [AGENTS.md](../../AGENTS.md); recheck volatile facts.

## Active work

- Request: reusable agent-oriented English guidelines and a separate Korean
  explanation in the repository and Notion. Version: `2026-09-14.v1`.
- Sources: [playbook](../engineering/agent-workflow.md) and
  [Korean explanation](../engineering/agent-workflow.ko.md).
- Week 5 is the next implementation milestone. Guideline work is not Redis
  implementation. This request changes no application source.

## Last observed baseline

- Root: `C:\Users\Administrator\source\repos\CoopGameServer`.
- Application baseline: `b3315c2fbcb20085f373b1edfb1c0acfc430b410`, branch `main`.
- Earlier in this task on 2026-09-14: local/remote `main` matched; remote
  [CI #26](https://github.com/fishbowl92/coop-game-server/actions/runs/34785706812)
  succeeded for that revision.
- Earlier local build command: `dotnet build CoopGameServer.slnx --configuration Release --no-restore --verbosity quiet`.
  Result: zero warnings/errors.
- Earlier local unit command: `dotnet test tests/CoopGameServer.UnitTests/CoopGameServer.UnitTests.csproj --configuration Release --no-build --verbosity quiet`.
  Result: 108 passed.
- Previous implementation record: 122 integration tests, 230 total. Not rerun here;
  Docker's Linux engine pipe was unavailable when probed. Recheck before testing.
- Pre-existing untracked data: 26 Notion review Markdown files under
  `CoopGameServer/notion-review/2026-09-07/`. Preserve; exclude from task staging.

## Week 5 context

- Week 4 combat/connections/reconnect/persistence/recovery implemented:
  [implementation record](../design/week-04-implementation-review.md).
- Redis configuration exists in `compose.yaml`; no application Redis cache/client
  exists at the recorded baseline.
- `src/CoopGameServer.Grains/Players/PlayerGrain.cs` has
  `GetProgressionPageAsync`: gold/inventory with RepeatableRead; no external
  progression endpoint. Profile/nickname uses `Api/Controllers/PlayersController.cs`.
- [Week 5 plan](https://app.notion.com/p/3caff0d6971781fa9e51e7f099994498):
  profile + gold + inventory first page, Cache-Aside, invalidation, fallback,
  counters/timing, real Redis tests. Its embedded old HEAD/date is historical.
- [Scope plan](https://app.notion.com/p/3c5ff0d6971781cd98a6c6a9fe9c0ff4):
  cache one progression query; dashboards belong to week 7.
- Briefing proposal: add a combined progression contract and authenticated endpoint.
  This is an assistant recommendation, not a separately approved user decision.
  Settle ownership, writers, key/page dimensions, TTL/timeout, freshness in design.
- README's final limitations still say combat/reconnect are unimplemented. Correct
  during implementation-status maintenance; do not repeat the stale claim.

## Next action

Guideline work is complete. When Week 5 implementation is active,
verify checkout, use the local startup runbook to restore Docker access, obtain
required integration baseline evidence, and finalize the progression/cache contract.

## Guideline delivery

- Local guidelines and Korean explanation complete; contributing rules aligned.
- [Notion Korean companion](https://app.notion.com/p/3dbff0d6971781809102f2f05545b0e3)
  published under CoopGameServer documents and fetched back for verification.
- Document checks: 5 files, 21 relative links, 8 PowerShell example blocks; no
  missing links, syntax errors, unbalanced fences, or whitespace issues.
- Application tests were not repeated for this documentation-only request.
- Working tree adds AGENTS.md, two engineering documents, and this handoff;
  modifies docs/contributing.md. Pre-existing review files remain untouched.
- No commit/push performed for guideline changes.
