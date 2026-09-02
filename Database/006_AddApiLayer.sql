USE JACO_Unified;
GO

-- External API layer (SAP and any other outside system creating/reading approvals without
-- going through the browser UI) -- see ApprovalsApiController. Deliberately separate from
-- the existing per-user login: an ApiClient is a credential for a SYSTEM, not a person, and
-- carries no session/cookie.

CREATE TABLE dbo.ApiClients (
    Id INT IDENTITY PRIMARY KEY,
    Name NVARCHAR(200) NOT NULL,
    Description NVARCHAR(500) NULL,
    KeyHash NVARCHAR(100) NOT NULL,
    KeyPrefix NVARCHAR(32) NOT NULL,
    Active BIT NOT NULL DEFAULT 1,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CreatedByUserName NVARCHAR(200) NULL,
    LastUsedAt DATETIME2 NULL
);
GO
CREATE INDEX IX_ApiClients_KeyPrefix ON dbo.ApiClients(KeyPrefix);
GO

-- Single row (Id=1) -- master switch, independent of any individual client's Active flag.
CREATE TABLE dbo.ApiSettings (
    Id INT NOT NULL PRIMARY KEY,
    Enabled BIT NOT NULL DEFAULT 0,
    LogRequests BIT NOT NULL DEFAULT 1,
    UpdatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedByUserName NVARCHAR(200) NULL
);
GO
INSERT INTO dbo.ApiSettings (Id, Enabled, LogRequests, UpdatedAt) VALUES (1, 0, 1, SYSUTCDATETIME());
GO

-- Raw HTTP-level audit trail for every call, successful or not -- deliberately separate
-- from AuditLog (business actions like Submit/Approve, regardless of caller).
CREATE TABLE dbo.ApiRequestLog (
    Id BIGINT IDENTITY PRIMARY KEY,
    ApiClientId INT NULL,
    ClientName NVARCHAR(200) NULL,
    Method NVARCHAR(10) NOT NULL,
    Path NVARCHAR(500) NOT NULL,
    QueryString NVARCHAR(1000) NULL,
    RequestBody NVARCHAR(MAX) NULL,
    StatusCode INT NOT NULL,
    ResponseBody NVARCHAR(MAX) NULL,
    RemoteIp NVARCHAR(64) NULL,
    DurationMs INT NOT NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
CREATE INDEX IX_ApiRequestLog_CreatedAt ON dbo.ApiRequestLog(CreatedAt DESC);
GO

-- "Web" (browser UI) vs "Api" (external system) -- lets the EXISTING Request Audit Log
-- screen (Cockpit/AuditLog) distinguish a front-end action from a remote one, instead of
-- needing a second screen cross-referenced against this one.
ALTER TABLE dbo.AuditLogs ADD Source NVARCHAR(20) NOT NULL DEFAULT 'Web';
GO
