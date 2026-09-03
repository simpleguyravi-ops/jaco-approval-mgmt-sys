USE JACO_Unified;
GO

-- Audit/compliance safety net for the four "Clear Old Entries" actions in the Operations
-- Cockpit (Routing/Audit/API Request/Digest logs). Each Clear action now writes a full
-- snapshot of every row it is about to delete here, in the same transaction as the delete,
-- before removing anything -- so an admin's wrong date or wrong log is recoverable from this
-- table (or its downloadable export) instead of requiring a full database point-in-time
-- restore. The live logs stay hard-delete on purpose (small/fast to query); this is the
-- recovery copy, not a second copy of the working table.
-- Table name is plural (LogArchives) to match the DbSet<LogArchive> property name -- EF
-- Core's default table-naming convention uses the DbSet property, not the entity class name,
-- and every other table in this schema (AuditLogs, DigestRuns, ApiClients, ...) already
-- follows that same convention.
CREATE TABLE dbo.LogArchives (
    Id BIGINT IDENTITY PRIMARY KEY,
    LogType NVARCHAR(50) NOT NULL,
    BeforeDate DATETIME2 NOT NULL,
    EntryCount INT NOT NULL,
    ContentJson NVARCHAR(MAX) NOT NULL,
    ClearedByUserName NVARCHAR(200) NULL,
    ClearedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
CREATE INDEX IX_LogArchives_LogType_ClearedAt ON dbo.LogArchives(LogType, ClearedAt DESC);
GO
