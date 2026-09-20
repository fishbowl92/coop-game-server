# Current work handoff

Updated: 2026-09-21 (Asia/Seoul). Snapshot, not live status.
Follow [AGENTS.md](../../AGENTS.md); recheck volatile facts.

## Active work

- Week 6 administrator lookup, audited reward grants, and the Blazor operations tool are implemented.
- The three administrator UI failure paths identified in review are corrected and locally validated.
- Fix details, changed symbols, verification, and limits: [Week 6 UI reliability](week-06-admin-ui-reliability.md).
- Original contract: [Week 6 administrator tool and audit](../design/week-06-admin-tool-and-audit.md).
- The user deferred all learning-center updates until the remaining weeks are complete. Do not synchronize Notion as part of this fix.

## Current implementation

- Administrator-only APIs require the Administrator role and a valid non-empty account ID claim.
- Administrator identity travels from the verified JWT through the API and Grain to PostgreSQL.
- Reward effects, reward receipts, and administrator audit records commit in one transaction.
- Replay equality includes administrator identity and reward content; Redis remains a deletable cache.
- The administrator UI preserves the confirmed reward receipt even when subsequent reads fail.
- Player selection changes only after lookup, progression, and history all succeed. Failed switches clear the prior selection.
- An uncertain grant freezes the original administrator, target, payload, and request ID. Retrying cannot adopt edited form values or another administrator.
- Refresh after a confirmed grant issues reads only. New grants remain blocked until the selected data has been refreshed.
- Initial definitive rejections allow correction; rejection after an uncertain attempt does not discard the original request.

## Verification and delivery

- The corrective slice passed the solution Release build and complete unit/integration regression tests.
- Targeted administrator tests include HTTP fault injection and rendering the same Razor component used by the live page.
- Commands, counts, and evidence scope are recorded in the fix report rather than duplicated here.
- This corrective slice is for a local commit. It has not been pushed, and remote CI has not verified it.
- Prior Week 6 publication records in Git and existing Notion pages describe the earlier source snapshot.

## Workspace note

- The pre-existing untracked `CoopGameServer/` review-data directory is preserved and excluded from staging.
- No server API, Grain, database schema, reward persistence, or cache policy changed in this correction.

## Outstanding boundaries

- Pending UI requests survive logout/login within the same Blazor circuit, not browser reload into a new circuit or server restart.
- Reward history is limited to the most recent 100 rows; continuation-token paging is not implemented.
- Production provisioning, SSO, MFA, fine-grained RBAC, approval workflows, bulk rewards, and long-lived pending-request recovery remain outside this correction.
- Only committed successful administrator operations enter `admin_audits`; rejected attempts use diagnostic/security logging.
- Existing learning-center status and some answers need a final code-backed review. Keep its broad rewrite deferred as requested.

## Next action

Start Week 7 observability from the corrected administrator reward flow: connect API, Orleans, PostgreSQL, and cache invalidation with secret-safe traces, structured logs, and metrics.
