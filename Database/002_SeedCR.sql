USE JACO_Unified;
GO

-- Test accounts (auto-provisioned on first SSO login too, but pre-seeding lets us wire
-- up approvers/permissions before anyone has logged in yet). Matched by UserName only --
-- AppUsers.Id here is purely local and unrelated to Portal's own user id.
INSERT INTO dbo.AppUsers (UserName, DisplayName, Department, IsActive) VALUES
    ('admin', 'Administrator', NULL, 1),
    ('test.creator', 'Test Creator', 'IT', 1),
    ('Wayne', 'Wayne', 'Sales', 1),
    ('Dominic', 'Dominic', 'After Sales', 1),
    ('Ravi Kumar', 'Ravi Kumar', 'After Sales', 1),
    ('Rodney', 'Rodney', 'After Sales', 1);
GO

INSERT INTO dbo.ApprovalTypes (Code, Name, Description, Active) VALUES
    ('CR', 'Change Request', 'IT/business change requests.', 1);
GO

INSERT INTO dbo.WorkflowVersions (ApprovalTypeId, VersionNo, IsCurrent)
    SELECT Id, 1, 1 FROM dbo.ApprovalTypes WHERE Code = 'CR';
GO

DECLARE @CR INT = (SELECT Id FROM dbo.ApprovalTypes WHERE Code = 'CR');

INSERT INTO dbo.WorkflowFields (ApprovalTypeId, FieldKey, FieldLabel, DataType, DisplayOrder, IsVisible, IsReadOnly, IsRequired, IsSensitive, LookupType, Active) VALUES
    (@CR, 'department', 'Department', 'Dropdown', 10, 1, 0, 1, 0, 'Department', 1),
    (@CR, 'priority', 'Priority', 'Dropdown', 20, 1, 0, 1, 0, 'Priority', 1),
    (@CR, 'impact', 'Impact', 'Dropdown', 30, 1, 0, 1, 0, 'Impact', 1),
    (@CR, 'changeReason', 'Change Reason', 'Dropdown', 40, 1, 0, 0, 0, 'ChangeReason', 1),
    (@CR, 'requiredBy', 'Required By', 'Date', 50, 1, 0, 0, 0, NULL, 1),
    (@CR, 'sapReferenceId', 'SAP Reference ID', 'Text', 60, 1, 0, 0, 0, NULL, 1),
    (@CR, 'businessRequirements', 'Business Requirements', 'TextArea', 70, 1, 0, 1, 0, NULL, 1),
    (@CR, 'tangibleBenefits', 'Tangible Benefits', 'TextArea', 80, 1, 0, 0, 0, NULL, 1),
    (@CR, 'intangibleBenefits', 'Intangible Benefits', 'TextArea', 90, 1, 0, 0, 0, NULL, 1);
GO

INSERT INTO dbo.PicklistValues (LookupType, Value, DisplayText, SortOrder, Active) VALUES
    ('Department', 'IT', 'IT', 10, 1),
    ('Department', 'Sales', 'Sales', 20, 1),
    ('Department', 'After Sales', 'After Sales', 30, 1),
    ('Department', 'Finance', 'Finance', 40, 1),
    ('Priority', 'Low', 'Low', 10, 1),
    ('Priority', 'Medium', 'Medium', 20, 1),
    ('Priority', 'High', 'High', 30, 1),
    ('Impact', 'Low', 'Low', 10, 1),
    ('Impact', 'Medium', 'Medium', 20, 1),
    ('Impact', 'High', 'High', 30, 1),
    ('ChangeReason', 'BugFix', 'Bug Fix', 10, 1),
    ('ChangeReason', 'Enhancement', 'Enhancement', 20, 1),
    ('ChangeReason', 'Compliance', 'Compliance', 30, 1);
GO

-- One default routing rule with no criteria (matches every submission) so the very first
-- end-to-end test doesn't also depend on getting criteria matching right on day one.
-- Real, criteria-driven rules can be added/edited later through Rule Builder.
DECLARE @WV INT = (SELECT WV.Id FROM dbo.WorkflowVersions WV JOIN dbo.ApprovalTypes AT2 ON AT2.Id = WV.ApprovalTypeId WHERE AT2.Code = 'CR' AND WV.IsCurrent = 1);
INSERT INTO dbo.RoutingRules (WorkflowVersionId, RuleName, Priority, Active) VALUES (@WV, 'Default Route', 10, 1);
GO

DECLARE @Rule INT = (SELECT Id FROM dbo.RoutingRules WHERE RuleName = 'Default Route');
DECLARE @WV2 INT = (SELECT WorkflowVersionId FROM dbo.RoutingRules WHERE Id = @Rule);

INSERT INTO dbo.WorkflowSteps (WorkflowVersionId, RoutingRuleId, LevelNo, Mode) VALUES
    (@WV2, @Rule, 1, 'ANY_ONE'),
    (@WV2, @Rule, 2, 'ANY_ONE'),
    (@WV2, @Rule, 3, 'ANY_ONE');
GO

DECLARE @Rule2 INT = (SELECT Id FROM dbo.RoutingRules WHERE RuleName = 'Default Route');
INSERT INTO dbo.WorkflowStepApprovers (WorkflowStepId, UserId)
    SELECT s.Id, (SELECT Id FROM dbo.AppUsers WHERE UserName = 'Dominic') FROM dbo.WorkflowSteps s WHERE s.RoutingRuleId = @Rule2 AND s.LevelNo = 1
    UNION ALL
    SELECT s.Id, (SELECT Id FROM dbo.AppUsers WHERE UserName = 'Ravi Kumar') FROM dbo.WorkflowSteps s WHERE s.RoutingRuleId = @Rule2 AND s.LevelNo = 2
    UNION ALL
    SELECT s.Id, (SELECT Id FROM dbo.AppUsers WHERE UserName = 'Rodney') FROM dbo.WorkflowSteps s WHERE s.RoutingRuleId = @Rule2 AND s.LevelNo = 3;
GO

DECLARE @CR2 INT = (SELECT Id FROM dbo.ApprovalTypes WHERE Code = 'CR');
INSERT INTO dbo.UserWorkflowPermissions (UserId, ApprovalTypeId, CanCreate, CanView) VALUES
    ((SELECT Id FROM dbo.AppUsers WHERE UserName = 'test.creator'), @CR2, 1, 1),
    ((SELECT Id FROM dbo.AppUsers WHERE UserName = 'Wayne'), @CR2, 1, 0),
    ((SELECT Id FROM dbo.AppUsers WHERE UserName = 'Dominic'), @CR2, 1, 0),
    ((SELECT Id FROM dbo.AppUsers WHERE UserName = 'Ravi Kumar'), @CR2, 1, 0),
    ((SELECT Id FROM dbo.AppUsers WHERE UserName = 'Rodney'), @CR2, 1, 0);
GO

PRINT 'CR type seeded: fields, picklists, a default routing rule (Dominic -> Ravi Kumar -> Rodney), and permissions.';
GO
