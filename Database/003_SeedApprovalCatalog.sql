USE JACO_Unified;
GO

-- Environment-agnostic seed for a fresh Test/Production database: the Change Request type,
-- its field catalog, and picklist values -- everything the app needs to be usable at all.
-- Deliberately does NOT seed any users, routing rules, or approver chains the way
-- 002_SeedCR.sql does for local dev (that script wires a default route to fake dev
-- accounts like "Dominic"/"Wayne", which don't exist in a real environment). For a real
-- environment: run tools/SeedAdmin first, sign in, add real accounts under User Accounts,
-- then configure the real approver chain through Rule Builder -- that's the intended path,
-- not something to hardcode here.

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

PRINT 'Change Request catalog seeded: type, fields, picklists. No users or routing rules -- set those up through the app (User Accounts, Users & Roles, Rule Builder) once an admin can sign in.';
GO
