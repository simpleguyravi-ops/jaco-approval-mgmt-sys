USE JACO_Unified;
GO

-- Lets a rule's 2nd/3rd/... criteria row choose OR instead of always being ANDed with the
-- ones before it (Rule Builder previously had no way to express this at all). Meaningless
-- on a criteria row that's first in its rule (nothing precedes it to join) -- the evaluator
-- and UI both just ignore the value there. Defaulting every existing row to 'AND' preserves
-- current routing behavior for every rule already saved.
ALTER TABLE dbo.RoutingRuleCriteria ADD LogicalOperator NVARCHAR(5) NOT NULL DEFAULT 'AND';
GO
