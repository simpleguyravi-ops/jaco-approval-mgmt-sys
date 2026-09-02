USE JACO_Unified;
GO

-- Per-field control over whether the external API (see ApprovalsApiController) accepts
-- a field on Create and returns it on Get -- independent of IsVisible, which only governs
-- the browser form. Defaults to 1 so every existing field's current API behavior is
-- unchanged until an admin deliberately excludes one under Criteria Fields.
ALTER TABLE dbo.WorkflowFields ADD IncludeInApi BIT NOT NULL DEFAULT 1;
GO
