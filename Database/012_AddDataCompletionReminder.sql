USE JACO_Unified;
GO

-- Data Completion Reminder -- "these Approved requests are still missing field X" (e.g. a
-- Change Request approved without a SAP Reference ID or Tentative UAT Date yet), emailed to
-- a single fixed recipient (IT, Finance, ...) rather than personalized per-user like the
-- Pending Approvals Digest. Multiple rules per Approval Type are allowed on purpose (e.g.
-- separate reminders to separate teams for different fields), so no unique index here
-- unlike DigestSchedules.

CREATE TABLE dbo.DataCompletionReminderRules (
    Id INT IDENTITY PRIMARY KEY,
    ApprovalTypeId INT NOT NULL,
    FieldKeysJson NVARCHAR(1000) NOT NULL DEFAULT '[]',
    RecipientAddress NVARCHAR(500) NULL,
    MailTemplateId INT NULL,
    Enabled BIT NOT NULL DEFAULT 0,
    RecurrenceType NVARCHAR(20) NOT NULL DEFAULT 'EveryNDays',
    IntervalDays INT NOT NULL DEFAULT 7,
    StartTime TIME NOT NULL DEFAULT '09:00:00',
    NextRunAtUtc DATETIME2 NULL,
    LastRunAtUtc DATETIME2 NULL,
    UpdatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedByUserName NVARCHAR(200) NULL
);
GO
CREATE INDEX IX_DataCompletionReminderRules_ApprovalTypeId ON dbo.DataCompletionReminderRules(ApprovalTypeId);
GO

-- One row per send attempt (scheduled or a manual "Send Now"). Single recipient, so unlike
-- DigestRuns/DigestRunRecipients there's no separate per-recipient table.
CREATE TABLE dbo.DataCompletionReminderRuns (
    Id BIGINT IDENTITY PRIMARY KEY,
    RuleId INT NOT NULL,
    ApprovalTypeName NVARCHAR(200) NOT NULL,
    RunAtUtc DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    TriggeredBy NVARCHAR(20) NOT NULL DEFAULT 'Scheduled',
    TriggeredByUserName NVARCHAR(200) NULL,
    MatchingCount INT NOT NULL DEFAULT 0,
    RecipientAddress NVARCHAR(500) NULL,
    Subject NVARCHAR(500) NULL,
    BodyHtml NVARCHAR(MAX) NULL,
    Status NVARCHAR(20) NOT NULL,
    ErrorMessage NVARCHAR(1000) NULL
);
GO
CREATE INDEX IX_DataCompletionReminderRuns_RuleId ON dbo.DataCompletionReminderRuns(RuleId);
GO
CREATE INDEX IX_DataCompletionReminderRuns_RunAtUtc ON dbo.DataCompletionReminderRuns(RunAtUtc DESC);
GO
