USE JACO_Unified;
GO

-- Automatic Pending Approvals Digest -- one schedule PER Approval Type (e.g. CR every 2
-- days, Sales Discount every working day at 9am), independent of each other and of the
-- existing ad-hoc "send to one person" Digest screen, which stays manual. No row for a
-- type means it was never configured (equivalent to disabled) -- nothing sends until an
-- admin explicitly sets one up.

CREATE TABLE dbo.DigestSchedules (
    Id INT IDENTITY PRIMARY KEY,
    ApprovalTypeId INT NOT NULL,
    Enabled BIT NOT NULL DEFAULT 0,
    RecurrenceType NVARCHAR(20) NOT NULL DEFAULT 'EveryNDays',
    IntervalDays INT NOT NULL DEFAULT 1,
    StartTime TIME NOT NULL DEFAULT '09:00:00',
    MailTemplateId INT NULL,
    NextRunAtUtc DATETIME2 NULL,
    LastRunAtUtc DATETIME2 NULL,
    UpdatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedByUserName NVARCHAR(200) NULL
);
GO
CREATE UNIQUE INDEX IX_DigestSchedules_ApprovalTypeId ON dbo.DigestSchedules(ApprovalTypeId);
GO

-- One row per digest send (scheduled or a manual "Run Now"), so "how many emails went out"
-- is answerable without cross-referencing PPF Monitor (per-Request, doesn't apply here).
CREATE TABLE dbo.DigestRuns (
    Id BIGINT IDENTITY PRIMARY KEY,
    ApprovalTypeId INT NOT NULL,
    ApprovalTypeName NVARCHAR(200) NOT NULL,
    RunAtUtc DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    TriggeredBy NVARCHAR(20) NOT NULL DEFAULT 'Scheduled',
    TriggeredByUserName NVARCHAR(200) NULL,
    EligibleUserCount INT NOT NULL DEFAULT 0,
    RecipientCount INT NOT NULL DEFAULT 0,
    SentCount INT NOT NULL DEFAULT 0,
    FailedCount INT NOT NULL DEFAULT 0
);
GO
CREATE INDEX IX_DigestRuns_RunAtUtc ON dbo.DigestRuns(RunAtUtc DESC);
GO

-- Per-recipient detail, including the exact rendered subject/body sent.
CREATE TABLE dbo.DigestRunRecipients (
    Id BIGINT IDENTITY PRIMARY KEY,
    DigestRunId BIGINT NOT NULL,
    UserId INT NOT NULL,
    UserName NVARCHAR(200) NOT NULL,
    Email NVARCHAR(320) NULL,
    PendingCount INT NOT NULL,
    Subject NVARCHAR(500) NOT NULL,
    BodyHtml NVARCHAR(MAX) NOT NULL,
    Status NVARCHAR(20) NOT NULL,
    ErrorMessage NVARCHAR(1000) NULL
);
GO
CREATE INDEX IX_DigestRunRecipients_DigestRunId ON dbo.DigestRunRecipients(DigestRunId);
GO
