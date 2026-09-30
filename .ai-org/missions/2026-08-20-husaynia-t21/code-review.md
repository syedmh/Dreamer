# T21 independent code review

Status: **CHANGES_REQUIRED**

## Blocking finding

The authored nonproduction workflow paths do not supply the deployment, preflight, and prepared
input artifacts now required by their scripts. If installed and enabled, Development and automatic
Staging fail before evidence or receipt production.

Evidence:

- `pipelines/github/release-build-and-nonproduction.yml:225,239,374,460,542`
- `eng/promotion/Invoke-OperationEvidenceProducer.ps1:24,111-120`
- `eng/promotion/Invoke-AutomaticStagingPromotion.ps1:46-49,197-216`
- `eng/promotion/Invoke-StageGate.ps1:19,117-158`

Executed review checks:

- `pwsh -NoProfile -File .\eng\ci\Test-PipelineDefinitions.ps1` — 534/534 passed.
- `pwsh -NoProfile -File .\eng\test\Invoke-T21Validation.ps1 -ContractOnly` —
  127 passed, 0 failed, 4 environmental blocks.
- A targeted parameter check found missing `DeploymentEvidencePath` on automatic/manual
  nonproduction gate paths and missing preflight/prepared-input arguments on automatic Staging.

Required remediation: implement explicit producer/consumer artifact wiring and enabled-path
contract tests. The superseding architecture additionally moves cloud-authenticated execution to a
pinned reusable-workflow subject and an immutable C6 operations bundle.

Verdict: **CHANGES_REQUIRED**

---

## T21-R10-R2 gate-finding remediation response — 2026-08-27

Status: **REMEDIATED LOCALLY — INDEPENDENT CODE REVIEW PENDING RERUN**

- The stale migration provenance policy expectation is now `2.1.0`.
- The application migration DLL is resolved from the validated C6 release artifact rather than
  the protected bundle. A real protected producer/wrapper Preflight fixture passes with the DLL
  absent from the protected closure.
- The SqlClient closure copies the exact selected Unix archive path and binds package/asset/RID,
  hash/length, assembly/file versions, and full managed identity; the ALC rejects same-simple-name
  identity substitution.
- Local lease and Azure CLI/SQL seams require explicit opt-in and fail before invocation in
  GitHub Actions. Timeout and nonzero token-process paths are deterministic, single-attempt, and
  redacted.
- Local focused evidence is `263/263`; both harness modes are `13/13`; cached/static checks pass.

This response does not alter the independent verdict above and does not approve R2-GR.

---

## T21-R10-R2 final independent code review — 2026-08-27

Verdict: **APPROVED**

The final review verified that:

- the trusted workflow no longer assigns `IsReadOnly` to directories and removes write access
  recursively on Linux;
- Preflight binds the protected execution bundle path/SHA, independently from the Apply-only
  managed migration DLL;
- current null application-bundle policy permits real Preflight with no DLL, while non-Preflight
  modes remain fail-closed;
- release-c6 provenance does not require an unrelated application-bundle SHA, while producer roles
  still require the protected bundle SHA;
- Azure CLI timeout handling kills and waits, and actual local timeout/nonzero processes are
  covered;
- v2.1 authorization, exact SqlClient closure/ALC, distinct commits, SQL/fence semantics, and
  policy hashes remain intact.

Residual risk is limited to intentionally unperformed live hosted Azure/SQL execution and actual
C6 construction.

---

## T21-R10-R3 independent finding and remediation response — 2026-08-27

Independent review superseded the R2 approval after finding that all 13 transactional lease
guards used `RAISERROR`, which does not honor `XACT_ABORT` and can allow subsequent mutation or
`COMMIT`.

Local remediation replaced those guards with unique semicolon-safe terminating errors
`51021` through `51033`, added exact static ordering coverage, and added a stale-release local-model
case that preserves the current holder. Affected checks passed 133/133 and the full focused matrix
passed 269/269.

Status: **REMEDIATED LOCALLY — INDEPENDENT R3 CODE REVIEW PENDING**

This response does not self-approve R3.

---

## T21-R10-R6 final independent code review — 2026-08-27

Verdict: **APPROVED**

The reviewer independently rechecked all prior R10/R1-R6 findings and found no remaining blocking
correctness or architecture defect. The final review specifically approved exact subject-set and
supported-claim handling, terminating lease guards, app-bound SqlClient execution, distinct run
commits, completed-run lifecycle, step-scoped provenance token, root/shell, binary download, action
pins, permissions, protected bundle closure, and zero-deployment ordering.

---

## T21-R10-R7 independent code review — 2026-08-27

Initial verdict: **CHANGES_REQUIRED**

The apparent masked runtime bearer was a review-output redaction; raw source and the four-request
mapped-token fixture proved the resolver already sends the runtime bearer. Two real findings were
remediated:

1. remove the name-specific provenance assertion override so captured-header behavior cannot be
   replaced by a source-text check; and
2. paginate/slurp all workflow runs before coordinator correlation filtering so a matching run
   older than the newest 100 cannot be duplicated.

Final verdict: **APPROVED**

The rereview verified the real bearer source, causal request capture, paginated API failure
handling, a checked-in 101-run page-two reuse case, and unchanged binary, pin, root, completed-run,
authorization, Production, no-rebuild, and zero-deployment controls.
