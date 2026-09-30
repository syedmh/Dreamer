# Definition of Done

- [ ] PR validation restores the exact SDK/tools and locked dependencies with fail-closed NuGet audit.
- [ ] PR validation builds warnings-as-errors and runs discovered compiled tests without requiring unfinished future suites.
- [ ] Formatting/analyzers, secret scanning, dependency/SBOM, Bicep lint/static validation, and explicit future accessibility/contract required-file gates are present.
- [ ] A single versioned C6 artifact contains the web archive, migration hooks, contracts, SBOM, reports, release manifest, and SHA256SUMS.
- [ ] Two local builds from unchanged source produce the same application archive hash.
- [ ] Development, Staging, and manual-disabled Production reference the same immutable checksum and never rebuild.
- [ ] Stage isolation/configuration, nonproduction live-integration prohibition, migration preflight/apply, backup evidence, health/smoke/crawl/accessibility/visual/sandbox, rollback, and evidence retention gates fail closed.
- [ ] Tampering and an induced gate failure are rejected; stage-reference assertions pass.
- [ ] Production execution is impossible without explicit manual approval inputs and separate CTO deployment authorization; no deployment occurs.
- [ ] YAML/static validation, script dry runs, secret-scan configuration tests, independent test/security/review gates, and final judgment pass.

## T21-R10 addendum — four R9 High findings

- [ ] **Requirements gate:** `requirements.md` remains frozen to exactly the four R10 findings, 18 requirements, 20 acceptance criteria, two assumptions, and zero blocking open questions.
- [ ] **Developer/Test gate:** The exact checked-in binary C6 retrieval body uses no `gh api --output`, preserves arbitrary binary bytes, and fails before side effects on CLI/output/size/digest errors.
- [ ] **Security/Review gate:** Every `azure/login` and `actions/attest-build-provenance` reference has authoritative upstream existence evidence for one consistent nonzero 40-hex SHA; tags, branches, zero/nonexistent/inconsistent pins, extra producers, and broadened permissions fail.
- [ ] **Developer/Test gate:** Every PowerShell workflow body uses `pwsh`; checkout-root fixtures shaped `Dreamer/HusayniaSite` resolve all Husaynia scripts/policy/solution under the child root and reject the wrong root before artifact, attestation, auth, or mutation.
- [ ] **Test gate:** Lifecycle tests prove completed-success release -> completed-success preflight/prepared producers -> completed-success CTO/migration authorization producers as applicable -> separately dispatched StageOperations; same-run, future, failed, cancelled, missing, cross-stage, and cross-release provenance rejects.
- [ ] **Review gate:** Automatic Development and Staging remain distinct completed-run lifecycles; Production remains separate/manual-disabled; all runs bind one completed release C6 and no downstream run rebuilds it.
- [ ] **Security/Test gate:** Every R9 control named in `requirements.md` R10-15 remains equal or stricter, including the stable deployment-evidence-forbidden stop before OIDC/login/mutation/evidence/receipt.
- [ ] **Test gate:** Cached/local JSON parse, PowerShell parse, actionlint, focused R10 and R9 suites, `Test-PipelineDefinitions.ps1`, and `Invoke-T21Validation.ps1 -ContractOnly` report real command/exit/assertion counts with zero failures and no install or package restore.
- [ ] **Audit gate:** Official locked NuGet audit remains fail-closed; `NU1900`, audit disablement, source-ignore, waiver, skipped test, or success normalization is not accepted.
- [ ] **Evidence gate:** Counters report authentication=0, workflow dispatches=0, deployments=0, success receipts=0, database calls/mutations=0, cloud resource/secret mutations=0, installs=0, package restores=0, commit/push/history operations=0, and actual C6 builds=0.
- [ ] **Independent gates:** Test Engineer, Security Engineer, Code Reviewer, and Engineering Judge independently approve R10; implementation/self-validation evidence does not check this box.
