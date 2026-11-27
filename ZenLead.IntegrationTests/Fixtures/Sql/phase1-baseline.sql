-- Phase 1 sample data (T03, parking-lot F11 item), applied to a database migrated up to AddAiUsageLog — the last Phase 1 migration.
-- The schema itself comes from the real migrations (MigrationTests migrates to AddAiUsageLog first), so this script only adds rows
-- and can't drift from the Phase 1 schema. Phase 1 stored lead emails exactly as typed, so the workspace below holds a mixed-case
-- duplicate pair ('Ann@Example.test' / 'ANN@Example.test') that AddCompanyAndLeadProvenance must refuse to merge.

INSERT INTO Workspaces (Id, Name, CreatedAt)
VALUES ('11111111-1111-1111-1111-111111111111', N'Phase 1 workspace', '2026-10-06T09:00:00'),
       ('22222222-2222-2222-2222-222222222222', N'Second Phase 1 workspace', '2026-10-06T09:00:00');
GO

INSERT INTO Leads (Id, WorkspaceId, Name, Email, Title, Status, CreatedAt)
VALUES ('aaaaaaaa-0000-0000-0000-000000000001', '11111111-1111-1111-1111-111111111111', N'Ann Example', N'Ann@Example.test', N'CTO', 0, '2026-10-06T10:00:00'),
       ('aaaaaaaa-0000-0000-0000-000000000002', '11111111-1111-1111-1111-111111111111', N'Ann Again', N'ANN@Example.test', NULL, 0, '2026-10-06T10:05:00'),
       ('aaaaaaaa-0000-0000-0000-000000000003', '11111111-1111-1111-1111-111111111111', N'Bob Padded', N'  Bob@Example.test ', N'COO', 1, '2026-10-06T10:10:00'),
       -- the same address in another workspace is not a duplicate
       ('aaaaaaaa-0000-0000-0000-000000000004', '22222222-2222-2222-2222-222222222222', N'Ann Elsewhere', N'ann@example.test', NULL, 0, '2026-10-06T10:15:00');
GO
