# T21 immutable validation and promotion pipelines

Objective: Execute frozen T21 exactly: implement PR validation and immutable Development to
Staging to manual Production promotion pipelines without deploying anything.

Classification: feature
Size: large
Scope owner: `HusayniaSite/pipelines/**`, `HusayniaSite/eng/**`,
`HusayniaSite/.config/dotnet-tools.json`

Authoritative inputs: modernization task-plan C6/T21, ADR-008, R-34..R-40, Azure release gates,
and `HusayniaSite/infra/README.md` plus its local-only validation scripts.

Hard constraints: no deployment, Azure authentication, shared-environment action, commit, push,
literal secret, production action, or modification outside the owned implementation paths.
