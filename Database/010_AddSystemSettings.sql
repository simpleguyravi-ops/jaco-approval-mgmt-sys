USE JACO_Unified;
GO

-- Single-row environment flag an admin sets explicitly before go-live. Compliance
-- guardrails (currently: the Clear Log minimum-retention floor in CockpitController) only
-- apply when this is Production, so ongoing development/QA on this same database isn't
-- blocked by a policy meant for live data. Seeded as Test (0) -- an admin must deliberately
-- switch it to Production once the system is actually live.
CREATE TABLE dbo.SystemSettings (
    Id INT NOT NULL PRIMARY KEY,
    IsProduction BIT NOT NULL DEFAULT 0,
    UpdatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedByUserName NVARCHAR(200) NULL
);
GO
INSERT INTO dbo.SystemSettings (Id, IsProduction, UpdatedAt) VALUES (1, 0, SYSUTCDATETIME());
GO
