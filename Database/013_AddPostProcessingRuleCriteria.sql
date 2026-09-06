USE JACO_Unified;
GO

-- Optional condition on a PostProcessingRule -- e.g. "only fire this rule if Branch =
-- Jeddah," so a different rule (same Approval Type + Event, different SequenceNo) can
-- target a different recipient for Riyadh. No rows for a rule means it always matches,
-- same as before this migration. Same shape as RoutingRuleCriteria (AND/OR precedence via
-- LogicalOperator), evaluated by the same RoutingService.EvaluateAll/GroupByPrecedence.
CREATE TABLE dbo.PostProcessingRuleCriteria (
    Id INT IDENTITY PRIMARY KEY,
    PostProcessingRuleId INT NOT NULL,
    FieldKey NVARCHAR(200) NOT NULL,
    Operator NVARCHAR(20) NOT NULL DEFAULT '=',
    ComparisonValue NVARCHAR(500) NOT NULL DEFAULT '',
    SortOrder INT NOT NULL DEFAULT 0,
    LogicalOperator NVARCHAR(5) NOT NULL DEFAULT 'AND'
);
GO
CREATE INDEX IX_PostProcessingRuleCriteria_RuleId ON dbo.PostProcessingRuleCriteria(PostProcessingRuleId);
GO
