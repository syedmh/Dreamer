# Definition of Done — T8M migration orchestration

- [ ] Owner `psql` scripts are the sole documented/supported production T8 Up and Down paths.
- [ ] One database/schema/T8 session advisory lock spans preflight, every commit, concurrent DDL,
      compensation, final history mutation, and disconnect.
- [ ] Every fixed object is schema-qualified and attested by namespace/object OID, canonical
      definition, and owner; temp shadows and hostile same-name objects fail before mutation.
- [ ] Schema and default ACL checks prove PUBLIC denial, direct app USAGE, no effective app CREATE
      or grant option, safe role posture, and no conflicting target/global defaults.
- [ ] Up records entry state, compensates only invocation-created objects, fully re-attests, and
      inserts history last; compensation failure is explicit and leaves history absent.
- [ ] Production Down is logical and non-destructive: only corrective history is deleted, while the
      unique index, validated chronology constraint, schema/default ACLs, and least privilege remain.
- [ ] EF Up/Down requires `tabruk.disposable_ef=on`; destructive EF Down additionally proves all
      application tables empty.
- [ ] Manifest verifies exactly seven artifacts, including the corrective designer and owner Down.
- [ ] Real PostgreSQL 18.6 executes the complete qualification, serialization, history-race, ACL,
      compensation, retry, Down/re-up, disposable-EF, manifest, and live-safety matrix with zero
      persistence skips.
- [ ] `InitialPostgresSchema.safety.md` documents only owner production commands, lock lifetime,
      semantic atomicity, stages/errors, compensation/manual repair, logical Down/re-up, ACL/default
      ACL prerequisites, and least privilege after Down.
- [ ] Independent test and security gates pass with zero unresolved Critical/High findings; code
      review is APPROVED; Engineering Judge is APPROVED.
- [ ] T9 remains blocked until every item above is evidenced.
