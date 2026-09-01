CREATE DATABASE JACO_Unified;
GO
USE JACO_Unified;
GO

-- ============================================================
-- Identity
-- ============================================================
CREATE TABLE dbo.AppUsers
(
    Id INT IDENTITY PRIMARY KEY,
    UserName NVARCHAR(100) NOT NULL,
    DisplayName NVARCHAR(150) NOT NULL,
    Department NVARCHAR(80) NULL,
    Branch NVARCHAR(80) NULL,
    Email NVARCHAR(200) NULL,
    IsActive BIT NOT NULL DEFAULT 1,
    PasswordHash NVARCHAR(200) NULL,
    PasswordSalt NVARCHAR(100) NULL,
    MustChangePassword BIT NOT NULL DEFAULT 0,
    IsAdmin BIT NOT NULL DEFAULT 0,
    FailedLoginCount INT NOT NULL DEFAULT 0,
    LockedUntil DATETIME2 NULL
);
CREATE UNIQUE INDEX UX_AppUsers_UserName ON dbo.AppUsers(UserName);

CREATE TABLE dbo.UserWorkflowPermissions
(
    Id BIGINT IDENTITY PRIMARY KEY,
    UserId INT NOT NULL,
    ApprovalTypeId INT NOT NULL,
    CanCreate BIT NOT NULL DEFAULT 0,
    CanView BIT NOT NULL DEFAULT 0
);
CREATE UNIQUE INDEX UX_UserWorkflowPermissions_User_Type ON dbo.UserWorkflowPermissions(UserId, ApprovalTypeId);

-- ============================================================
-- Approval Type + dynamic field catalog
-- ============================================================
CREATE TABLE dbo.ApprovalTypes
(
    Id INT IDENTITY PRIMARY KEY,
    Code NVARCHAR(40) NOT NULL,
    Name NVARCHAR(100) NOT NULL,
    Description NVARCHAR(400) NULL,
    Active BIT NOT NULL DEFAULT 1
);
CREATE UNIQUE INDEX UX_ApprovalTypes_Code ON dbo.ApprovalTypes(Code);

CREATE TABLE dbo.WorkflowVersions
(
    Id INT IDENTITY PRIMARY KEY,
    ApprovalTypeId INT NOT NULL,
    VersionNo INT NOT NULL,
    IsCurrent BIT NOT NULL DEFAULT 0
);
CREATE INDEX IX_WorkflowVersions_Type ON dbo.WorkflowVersions(ApprovalTypeId);

CREATE TABLE dbo.WorkflowFields
(
    Id INT IDENTITY PRIMARY KEY,
    ApprovalTypeId INT NULL,
    FieldKey NVARCHAR(80) NOT NULL,
    FieldLabel NVARCHAR(150) NOT NULL,
    DataType NVARCHAR(20) NOT NULL DEFAULT 'Text',
    DisplayOrder INT NOT NULL DEFAULT 10,
    IsVisible BIT NOT NULL DEFAULT 1,
    IsReadOnly BIT NOT NULL DEFAULT 0,
    IsRequired BIT NOT NULL DEFAULT 0,
    IsSensitive BIT NOT NULL DEFAULT 0,
    LookupType NVARCHAR(80) NULL,
    Active BIT NOT NULL DEFAULT 1
);
CREATE INDEX IX_WorkflowFields_Type_Key ON dbo.WorkflowFields(ApprovalTypeId, FieldKey);

CREATE TABLE dbo.PicklistValues
(
    Id INT IDENTITY PRIMARY KEY,
    LookupType NVARCHAR(80) NOT NULL,
    Value NVARCHAR(100) NOT NULL,
    DisplayText NVARCHAR(150) NOT NULL,
    SortOrder INT NOT NULL DEFAULT 10,
    Active BIT NOT NULL DEFAULT 1
);
CREATE INDEX IX_PicklistValues_Type_Value ON dbo.PicklistValues(LookupType, Value);

-- ============================================================
-- Routing
-- ============================================================
CREATE TABLE dbo.RoutingRules
(
    Id INT IDENTITY PRIMARY KEY,
    WorkflowVersionId INT NOT NULL,
    RuleName NVARCHAR(150) NOT NULL,
    Priority INT NOT NULL DEFAULT 10,
    Active BIT NOT NULL DEFAULT 1
);
CREATE INDEX IX_RoutingRules_Version ON dbo.RoutingRules(WorkflowVersionId);

CREATE TABLE dbo.RoutingRuleCriteria
(
    Id INT IDENTITY PRIMARY KEY,
    RoutingRuleId INT NOT NULL,
    FieldKey NVARCHAR(80) NOT NULL,
    Operator NVARCHAR(20) NOT NULL,
    ComparisonValue NVARCHAR(400) NOT NULL,
    SortOrder INT NOT NULL DEFAULT 0
);
CREATE INDEX IX_RoutingRuleCriteria_Rule ON dbo.RoutingRuleCriteria(RoutingRuleId);

CREATE TABLE dbo.WorkflowSteps
(
    Id INT IDENTITY PRIMARY KEY,
    WorkflowVersionId INT NOT NULL,
    RoutingRuleId INT NOT NULL,
    LevelNo INT NOT NULL,
    Mode NVARCHAR(20) NOT NULL DEFAULT 'ANY_ONE',
    RequiredCount INT NULL
);
CREATE INDEX IX_WorkflowSteps_Rule_Level ON dbo.WorkflowSteps(RoutingRuleId, LevelNo);

CREATE TABLE dbo.WorkflowStepApprovers
(
    Id INT IDENTITY PRIMARY KEY,
    WorkflowStepId INT NOT NULL,
    UserId INT NOT NULL
);
CREATE INDEX IX_WorkflowStepApprovers_Step ON dbo.WorkflowStepApprovers(WorkflowStepId);

-- ============================================================
-- THE unified request -- one row for the entire lifecycle
-- ============================================================
CREATE TABLE dbo.Requests
(
    Id BIGINT IDENTITY PRIMARY KEY,
    RequestNumber NVARCHAR(60) NOT NULL,
    ApprovalTypeId INT NOT NULL,
    WorkflowVersionId INT NULL,
    RoutingRuleId INT NULL,
    CreatorUserId INT NOT NULL,
    CreatorUserName NVARCHAR(100) NOT NULL,
    Subject NVARCHAR(300) NULL,
    Status NVARCHAR(30) NOT NULL DEFAULT 'Draft',
    CurrentLevelNo INT NULL,
    DataJson NVARCHAR(MAX) NOT NULL DEFAULT '{}',
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE UNIQUE INDEX UX_Requests_RequestNumber ON dbo.Requests(RequestNumber);
CREATE INDEX IX_Requests_Creator ON dbo.Requests(CreatorUserId, CreatedAt);
CREATE INDEX IX_Requests_Type_Status ON dbo.Requests(ApprovalTypeId, Status);

CREATE SEQUENCE dbo.RequestIdSequence
    AS BIGINT
    START WITH 1
    INCREMENT BY 1;

CREATE TABLE dbo.RequestAttachments
(
    Id BIGINT IDENTITY PRIMARY KEY,
    RequestId BIGINT NOT NULL,
    OriginalFileName NVARCHAR(300) NOT NULL,
    StoredFileName NVARCHAR(300) NOT NULL,
    ContentType NVARCHAR(150) NOT NULL,
    FileSize BIGINT NOT NULL,
    UploadedByUserId INT NOT NULL,
    UploadedByUserName NVARCHAR(100) NOT NULL,
    UploadedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_RequestAttachments_Request ON dbo.RequestAttachments(RequestId);

CREATE TABLE dbo.RequestActions
(
    Id BIGINT IDENTITY PRIMARY KEY,
    RequestId BIGINT NOT NULL,
    LevelNo INT NOT NULL,
    UserId INT NOT NULL,
    ActionCode NVARCHAR(30) NOT NULL,
    Comments NVARCHAR(MAX) NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_RequestActions_Request ON dbo.RequestActions(RequestId, LevelNo);

CREATE TABLE dbo.WorkflowParticipants
(
    Id BIGINT IDENTITY PRIMARY KEY,
    RequestId BIGINT NOT NULL,
    UserId INT NOT NULL,
    ParticipantType NVARCHAR(20) NOT NULL,
    FirstSeenAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    LastSeenAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE UNIQUE INDEX UX_WorkflowParticipants_Request_User ON dbo.WorkflowParticipants(RequestId, UserId);

CREATE TABLE dbo.ApproverReassignments
(
    Id BIGINT IDENTITY PRIMARY KEY,
    RequestId BIGINT NOT NULL,
    LevelNo INT NOT NULL,
    OldUserId INT NULL,
    NewUserId INT NOT NULL,
    Reason NVARCHAR(400) NOT NULL,
    ChangedByUserId INT NOT NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_ApproverReassignments_Request ON dbo.ApproverReassignments(RequestId);

CREATE TABLE dbo.AuditLogs
(
    Id BIGINT IDENTITY PRIMARY KEY,
    RequestId BIGINT NULL,
    UserId INT NULL,
    ActionCode NVARCHAR(40) NOT NULL,
    DetailsJson NVARCHAR(MAX) NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_AuditLogs_Request ON dbo.AuditLogs(RequestId, CreatedAt);

CREATE TABLE dbo.RoutingLog
(
    Id BIGINT IDENTITY PRIMARY KEY,
    RequestNumber NVARCHAR(60) NULL,
    ApprovalTypeId INT NOT NULL,
    OutcomeCode NVARCHAR(40) NOT NULL,
    Success BIT NOT NULL,
    MatchedRuleName NVARCHAR(150) NULL,
    Detail NVARCHAR(400) NULL,
    RoutingContextJson NVARCHAR(MAX) NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_RoutingLog_CreatedAt ON dbo.RoutingLog(CreatedAt);

-- ============================================================
-- Email / PPF
-- ============================================================
CREATE TABLE dbo.EmailSettings
(
    Id INT NOT NULL PRIMARY KEY,
    Enabled BIT NOT NULL DEFAULT 0,
    Host NVARCHAR(200) NOT NULL DEFAULT '',
    Port INT NOT NULL DEFAULT 587,
    UseTls BIT NOT NULL DEFAULT 1,
    [From] NVARCHAR(200) NOT NULL DEFAULT '',
    Username NVARCHAR(200) NULL,
    Password NVARCHAR(200) NULL,
    UpdatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedByUserName NVARCHAR(100) NULL
);

CREATE TABLE dbo.MailTemplates
(
    Id INT IDENTITY PRIMARY KEY,
    Name NVARCHAR(150) NOT NULL,
    Subject NVARCHAR(300) NOT NULL,
    BodyHtml NVARCHAR(MAX) NOT NULL,
    IsTableTemplate BIT NOT NULL DEFAULT 0,
    IsActive BIT NOT NULL DEFAULT 1,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE TABLE dbo.PostProcessingRules
(
    Id INT IDENTITY PRIMARY KEY,
    ApprovalTypeId INT NOT NULL,
    EventCode NVARCHAR(40) NOT NULL,
    ActionType NVARCHAR(20) NOT NULL,
    Target NVARCHAR(300) NULL,
    ActionConfigJson NVARCHAR(MAX) NULL,
    SequenceNo INT NOT NULL DEFAULT 10,
    Active BIT NOT NULL DEFAULT 1
);
CREATE INDEX IX_PostProcessingRules_Type_Event ON dbo.PostProcessingRules(ApprovalTypeId, EventCode);

CREATE TABLE dbo.PostProcessingExecutions
(
    Id BIGINT IDENTITY PRIMARY KEY,
    PostProcessingRuleId INT NOT NULL,
    RequestId BIGINT NOT NULL,
    AttemptNo INT NOT NULL,
    ActionType NVARCHAR(20) NOT NULL DEFAULT 'Email',
    Target NVARCHAR(300) NULL,
    Status NVARCHAR(20) NOT NULL DEFAULT 'Pending',
    ErrorMessage NVARCHAR(400) NULL,
    StartedAt DATETIME2 NULL,
    FinishedAt DATETIME2 NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_PostProcessingExecutions_Request ON dbo.PostProcessingExecutions(RequestId);

PRINT 'JACO_Unified database created successfully.';
GO
