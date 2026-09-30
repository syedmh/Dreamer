# T4R Two-Root Isolation Test Result

Date: 2026-08-14

## TEST RESULT

### Commands

```text
dotnet test .\tests\HusayniaTabruk.Domain.Tests\HusayniaTabruk.Domain.Tests.csproj --no-restore --filter FullyQualifiedName~T4TwoRootIsolationRegressionTests --logger "console;verbosity=minimal"
dotnet test .\tests\HusayniaTabruk.Domain.Tests\HusayniaTabruk.Domain.Tests.csproj --no-restore --filter FullyQualifiedName~Accounts --logger "console;verbosity=minimal"
dotnet test .\HusayniaTabruk.sln --no-restore --logger "console;verbosity=minimal"
dotnet build .\HusayniaTabruk.sln --no-restore -c Release --nologo
dotnet format .\HusayniaTabruk.sln --no-restore --verify-no-changes --verbosity minimal
```

### Result

```text
Focused T4 two-root isolation: Passed 8, Failed 0, Skipped 0.
Accounts suite: Passed 55, Failed 0, Skipped 0.
Full solution: Domain 68 + Application 77 + API Contract 36 = Passed 181,
Failed 0, Skipped 0. IntegrationTests has no discoverable tests.
Release build: Build succeeded. 0 Warning(s), 0 Error(s).
Format verification: exit code 0.
```

Passed: **181**
Failed: **0**
Skipped: **0**

### Mutation evidence

An isolated copy at
`%TEMP%\HusayniaTabruk-T4-shared-shallow-mutant` replaced both aggregate constructor deep-copy
assignments with shared shallow-list assignments:

```text
this.memberships = memberships as List<Membership> ?? memberships.ToList();
this.roleChangeRequests =
    roleChangeRequests as List<RoleChangeRequest> ?? roleChangeRequests.ToList();
```

Executed:

```text
dotnet test .\tests\HusayniaTabruk.Domain.Tests\HusayniaTabruk.Domain.Tests.csproj --no-restore --filter FullyQualifiedName~T4TwoRootIsolationRegressionTests --logger "console;verbosity=minimal"
```

Real summary:

```text
Failed! - Failed: 8, Passed: 0, Skipped: 0, Total: 8
MUTANT_EXIT=1
```

The mutant was killed by bootstrap, disable, assign Food Incharge, revoke Food Incharge,
administrator grant proposal, administrator revoke proposal, grant approval, and revoke approval.
Approval also proved that the retained original request input was mutated by the mutant.

### Failures

None in current production code.

### Coverage of acceptance criteria

- Bootstrap against root A; root B, source inputs, snapshots, nested roles, event, and B.Version
  isolated -> `BootstrapAdministratorsMutatesOnlyRootA` PASS.
- Disable membership -> `DisableMembershipMutatesOnlyRootA` PASS.
- Assign Food Incharge -> `AssignFoodInchargeMutatesOnlyRootA` PASS.
- Revoke Food Incharge -> `RevokeFoodInchargeMutatesOnlyRootA` PASS.
- Propose administrator grant and revoke -> `ProposeAdministratorRoleChangeMutatesOnlyRootA`
  Grant/Revoke PASS.
- Approve administrator grant and revoke -> `ApproveAdministratorRoleChangeMutatesOnlyRootA`
  Grant/Revoke PASS.
- Original membership/request inputs unchanged -> asserted after every command; PASS.
- Previously returned root A/root B membership and request snapshots unchanged -> asserted after
  every command; PASS.
- Nested `ActiveRoles` snapshots unchanged -> asserted for source, root A, and root B; PASS.
- Command receipts/events retain expected IDs, action, roles, and timestamps -> PASS.
- Identically rehydrated root B and `B.Version` unchanged -> asserted after every command; PASS.
- Retained original request source remains pending/unapproved after root A approval -> PASS.
- Deliberate shallow-copy mutant fails -> 8/8 failures; PASS.

## Conclusion: PASS

T4R's prior two-root isolation GAP is closed. T5 is ready to proceed.
