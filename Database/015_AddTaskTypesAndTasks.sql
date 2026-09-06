USE JACO_Unified;
GO

-- Task Types -- a peer of ApprovalTypes in the same WorkflowFields/PicklistValues catalog,
-- but for post-approval (or any-event-triggered) work items instead of request submissions.
CREATE TABLE dbo.TaskTypes (
    Id INT IDENTITY PRIMARY KEY,
    Code NVARCHAR(50) NOT NULL,
    Name NVARCHAR(200) NOT NULL,
    Active BIT NOT NULL DEFAULT 1
);
GO
CREATE UNIQUE INDEX IX_TaskTypes_Code ON dbo.TaskTypes(Code);
GO

-- WorkflowFields already has a nullable ApprovalTypeId meaning "generic field for every
-- Approval Type" -- this second nullable owner column lets the exact same field catalog
-- (FieldKey/DataType/Required/LookupType/...) also define a Task Type's fields, with no
-- second field-definition system. A field belongs to exactly one of ApprovalTypeId /
-- TaskTypeId (both null would mean "generic to everything", which the app never creates).
ALTER TABLE dbo.WorkflowFields ADD TaskTypeId INT NULL;
GO
CREATE INDEX IX_WorkflowFields_TaskTypeId ON dbo.WorkflowFields(TaskTypeId);
GO

-- One assigned work item, created by a PostProcessingRule whose ActionType is 'AssignTask'.
-- No FK constraints, matching every other table in this schema (app-managed relationships).
CREATE TABLE dbo.Tasks (
    Id BIGINT IDENTITY PRIMARY KEY,
    RequestId BIGINT NOT NULL,
    PostProcessingRuleId INT NOT NULL,
    TaskTypeId INT NOT NULL,
    Title NVARCHAR(300) NOT NULL,
    -- Assigned to exactly one of these -- a specific user, or a whole department queue where
    -- whoever completes it first claims it. Validated at Save time (PostProcessingRulesController),
    -- not by a DB constraint.
    AssignedToUserId INT NULL,
    AssignedToDepartment NVARCHAR(100) NULL,
    -- Values the assignee filled in for this Task Type's fields, keyed by FieldKey -- same
    -- flat JSON-object convention as Requests.DataJson.
    DataJson NVARCHAR(MAX) NULL,
    Status NVARCHAR(20) NOT NULL DEFAULT 'Open',
    CreatedAtUtc DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    DueAtUtc DATETIME2 NULL,
    -- Next time the TaskOverdue background scan should re-check this task -- null means no
    -- due date was set, or the task is already Done. Advances by a fixed daily cadence each
    -- time it fires, so an overdue task nudges repeatedly rather than firing once and going
    -- silent (see TaskOverdueSchedulerHostedService).
    NextOverdueCheckAtUtc DATETIME2 NULL,
    CompletedAtUtc DATETIME2 NULL,
    CompletedByUserId INT NULL
);
GO
CREATE INDEX IX_Tasks_AssignedToUserId ON dbo.Tasks(AssignedToUserId, Status);
GO
CREATE INDEX IX_Tasks_AssignedToDepartment ON dbo.Tasks(AssignedToDepartment, Status);
GO
CREATE INDEX IX_Tasks_NextOverdueCheckAtUtc ON dbo.Tasks(NextOverdueCheckAtUtc);
GO
CREATE INDEX IX_Tasks_RequestId ON dbo.Tasks(RequestId);
GO
