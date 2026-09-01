USE JACO_Unified;
GO

-- Reports (new top-level nav section, outside Administration) is reachable by any admin
-- plus anyone explicitly granted this flag via User Accounts -- a read-only compliance/
-- audit role that sees aggregate reporting and can drill into any request, but cannot
-- touch routing, fields, users, or PPF config the way a full Administrator can.
ALTER TABLE dbo.AppUsers ADD IsAuditor BIT NOT NULL DEFAULT 0;
GO
