USE JACO_Unified;
GO

-- Part of simplifying Post-Processing Rules into one general, "bulk"-capable rule concept:
-- every rule gets a human Name (the list was only ever scannable by Type+Event+Recipient
-- before this), and a rule can now apply to several Approval Types at once instead of
-- exactly one.

-- 1. Name -- nullable first so existing rows can be backfilled before the NOT NULL is
-- enforced, generated from what each rule already does so nothing is left blank.
ALTER TABLE dbo.PostProcessingRules ADD Name NVARCHAR(200) NULL;
GO

UPDATE ppr
SET Name = at.Name + ' - ' + ppr.EventCode + ' - ' + ISNULL(JSON_VALUE(ppr.ActionConfigJson, '$.toMode'), ppr.ActionType)
FROM dbo.PostProcessingRules ppr
JOIN dbo.ApprovalTypes at ON at.Id = ppr.ApprovalTypeId;
GO

ALTER TABLE dbo.PostProcessingRules ALTER COLUMN Name NVARCHAR(200) NOT NULL;
GO

-- 2. Multi Approval Type support -- one row per (rule, type) a rule applies to. Every
-- existing rule is migrated to keep exactly the one type it already had; the old single
-- ApprovalTypeId column is then redundant and dropped.
CREATE TABLE dbo.PostProcessingRuleApprovalTypes (
    PostProcessingRuleId INT NOT NULL,
    ApprovalTypeId INT NOT NULL,
    PRIMARY KEY (PostProcessingRuleId, ApprovalTypeId)
);
GO
CREATE INDEX IX_PostProcessingRuleApprovalTypes_ApprovalTypeId ON dbo.PostProcessingRuleApprovalTypes(ApprovalTypeId);
GO

INSERT INTO dbo.PostProcessingRuleApprovalTypes (PostProcessingRuleId, ApprovalTypeId)
SELECT Id, ApprovalTypeId FROM dbo.PostProcessingRules;
GO

-- The original (ApprovalTypeId, EventCode) index has to go before the column itself can --
-- replaced by an index on EventCode alone (small table; ApprovalTypeId lookups now go
-- through the join table's own index instead).
DROP INDEX IX_PostProcessingRules_Type_Event ON dbo.PostProcessingRules;
GO
CREATE INDEX IX_PostProcessingRules_EventCode ON dbo.PostProcessingRules(EventCode);
GO

ALTER TABLE dbo.PostProcessingRules DROP COLUMN ApprovalTypeId;
GO

SELECT Id, Name FROM dbo.PostProcessingRules;
SELECT * FROM dbo.PostProcessingRuleApprovalTypes;
GO
