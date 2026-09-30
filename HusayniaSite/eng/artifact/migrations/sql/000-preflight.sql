-- T21 inert compatibility sentinel.
-- The migration wrapper MUST NOT execute repository-controlled SQL for preflight.
-- Invoke-MigrationBundle.ps1 uses its policy-pinned fixed read-only metadata query
-- and verifies that the authenticated principal has SELECT but no mutation rights.
THROW 51099, 'Repository-controlled migration preflight SQL execution is prohibited.', 1;
