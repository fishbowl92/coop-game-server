# Current work handoff

Updated: 2026-09-23 (Asia/Seoul). Snapshot, not live status. Follow [AGENTS.md](../../AGENTS.md); recheck volatile facts.

## Active work

- Week 8 load test implementation and local baseline are complete. The experiment contract is [Week 8 design](../design/week-08-load-testing.md); the nine-run evidence and raw samples are [Week 8 performance](../performance/week-08/README.md).
- Local guideline commit `d0e3a4e` records weekly freshness, stale Notion status, missing artifacts, and valid load-test requirements in AGENTS.md and both engineering playbooks. Two Notion guideline pages were updated and refetched.
- Implementation commit `fe61f83` adds the separate NBomber load runner, isolated Compose, repeat script, and solution entry. The game server production behavior was not modified. Measurement found no demonstrated persistent bottleneck, so there is no After optimization commit.
- The initial GitHub `main` push was rejected by automatic approval review. The user then explicitly approved publication. `origin/main` reached `76939f7`, and push CI run `35850550530` passed restore, build, and tests at that exact commit. Verify the final remote commit when finishing this status update.

## Local verification

- Release solution build: 0 warnings and 0 errors.
- Complete regression after Week 8 implementation: 141 unit + 139 integration tests passed, 0 failed/skipped.
- On `fe61f83`, nine isolated runs yielded 768/768 valid HTTP responses with zero unexpected errors: progression at 20/s for 10s, combat at 2/s and 5/s for 8s, three runs per condition. Post-run read-only persistence checks passed in all runs.
- Generated NBomber reports and logs stay in ignored `artifacts/week08/`; the safe raw samples and summaries are in the performance document directory.
- The dedicated containers and child server processes were stopped after each run. The pre-existing untracked `CoopGameServer/` review-data directory remains untouched.

## Boundaries and next action

- Week 8 combat load covers valid partial combat, not room completion or exactly-once reward delivery under load. The existing integration suite covers those guarantees at its own scope.
- Warm progression reads are prepared, but Redis hit ratio was not independently measured. First combat attacks consistently had higher latency; the cause was not isolated with traces. Single-machine tmpfs measurements do not establish production SLO or capacity.
- Week 7's promised Redis incident report is still absent. Historical Notion dashboard, roadmap, and Week 8 learning note remain stale; the user previously deferred broad learning-center updates until remaining weeks complete. The 2026-09-23 guideline update is the user-authorized exception.
- Next: verify CI at the final documentation status commit, then begin Week 9 only when requested.
