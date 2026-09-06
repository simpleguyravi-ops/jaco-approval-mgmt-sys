USE JACO_Unified;
GO

-- Structured admin-override flag on a decision, previously only recoverable by
-- string-matching Comments for the "[Admin override" marker (see
-- RequestService.AdminOverrideMarker / DecideAsync). Drives PpfExecutor's new
-- "BypassedApprover" recipient mode: which requires knowing, cheaply and reliably, whether
-- the most recent decision on a request was an admin override, without parsing free text.
ALTER TABLE dbo.RequestActions ADD IsAdminOverride BIT NOT NULL DEFAULT 0;
GO

-- Backfill history so existing override decisions are consistent with the new column
-- rather than only being real from this point forward.
UPDATE dbo.RequestActions SET IsAdminOverride = 1 WHERE Comments LIKE '[Admin override%';
GO
