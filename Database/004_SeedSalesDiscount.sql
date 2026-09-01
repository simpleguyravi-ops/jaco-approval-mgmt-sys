USE JACO_Unified;
GO

-- Sales Discount as a second Approval Type in JAMS, on the exact same generic engine CR
-- already runs on -- Create/Edit/Details/routing/PPF are all fully data-driven, so this is
-- purely a catalog seed, not new app code. Field list, picklist values, and the Branch
-- concept are pulled from the existing standalone JACO-SalesDiscount app
-- (C:\JACO\JACO-SalesDiscount) so the data captured here matches what that app already
-- established, not a fresh guess.
--
-- Deliberately environment-agnostic like 003_SeedApprovalCatalog.sql: no users, no routing
-- rule, no approver chain. Real branches/approvers get set up through the app itself
-- (PicklistValues admin screen for Branch + its account email, Rule Builder for the
-- approval chain) once real accounts exist -- same reasoning as CR's clean seed.

ALTER TABLE dbo.PicklistValues ADD ExtraData NVARCHAR(200) NULL;
GO

INSERT INTO dbo.ApprovalTypes (Code, Name, Description, Active) VALUES
    ('SALES_DISCOUNT', 'Sales Discount', 'Vehicle sales discount requests requiring management approval.', 1);
GO

INSERT INTO dbo.WorkflowVersions (ApprovalTypeId, VersionNo, IsCurrent)
    SELECT Id, 1, 1 FROM dbo.ApprovalTypes WHERE Code = 'SALES_DISCOUNT';
GO

DECLARE @SD INT = (SELECT Id FROM dbo.ApprovalTypes WHERE Code = 'SALES_DISCOUNT');

-- No separate "Company" field -- it's 1:1 derived from Branch in the standalone app (never
-- typed by the user), so Branch alone carries what's needed for both display and routing
-- criteria here; nothing is lost by not tracking it as its own submitted value.
INSERT INTO dbo.WorkflowFields (ApprovalTypeId, FieldKey, FieldLabel, DataType, DisplayOrder, IsVisible, IsReadOnly, IsRequired, IsSensitive, LookupType, Active) VALUES
    (@SD, 'branch', 'Branch', 'Dropdown', 10, 1, 0, 1, 0, 'Branch', 1),
    (@SD, 'customerName', 'Customer Name', 'Text', 20, 1, 0, 1, 0, NULL, 1),
    (@SD, 'vehicleModel', 'Vehicle Model', 'Text', 30, 1, 0, 1, 0, NULL, 1),
    (@SD, 'modelYear', 'Model Year', 'Number', 40, 1, 0, 0, 0, NULL, 1),
    (@SD, 'salesChannel', 'Sales Channel', 'Dropdown', 50, 1, 0, 1, 0, 'SalesChannel', 1),
    (@SD, 'orderType', 'Order Type', 'Dropdown', 60, 1, 0, 1, 0, 'OrderType', 1),
    (@SD, 'discountReason', 'Discount Reason', 'Dropdown', 70, 1, 0, 0, 0, 'DiscountReason', 1),
    (@SD, 'specialOrder', 'Special Order', 'Dropdown', 80, 1, 0, 0, 0, 'SpecialOrder', 1),
    (@SD, 'commissionNumber', 'Commission No. / Baumuster', 'Text', 90, 1, 0, 0, 0, NULL, 1),
    (@SD, 'vin', 'Chassis No. / VIN', 'Text', 100, 1, 0, 0, 0, NULL, 1),
    (@SD, 'daysInStock', 'Days in Stock', 'Number', 110, 1, 0, 0, 0, NULL, 1),
    (@SD, 'daysReserved', 'Days Reserved', 'Number', 120, 1, 0, 0, 0, NULL, 1),
    (@SD, 'sellingPrice', 'Selling Price', 'Currency', 130, 1, 0, 0, 0, NULL, 1),
    (@SD, 'costPrice', 'Cost Price', 'Currency', 140, 1, 0, 0, 1, NULL, 1),
    (@SD, 'requestedDiscountPercent', 'Requested Discount %', 'Number', 150, 1, 0, 1, 0, NULL, 1),
    (@SD, 'requestedDiscountAmount', 'Requested Discount Amount', 'Currency', 160, 1, 0, 0, 0, NULL, 1),
    (@SD, 'customerFinalOffer', 'Customer Final Offer', 'Currency', 170, 1, 0, 0, 0, NULL, 1),
    (@SD, 'netMargin', 'Balance Net Margin %', 'Number', 180, 1, 0, 0, 1, NULL, 1),
    (@SD, 'discountNotes', 'Notes', 'TextArea', 190, 1, 0, 0, 0, NULL, 1);
GO

INSERT INTO dbo.PicklistValues (LookupType, Value, DisplayText, SortOrder, Active) VALUES
    ('DiscountReason', '01', '01 - Damage', 10, 1),
    ('DiscountReason', '02', '02 - Delay in delivery', 20, 1),
    ('DiscountReason', '03', '03 - Competition with other brands', 30, 1),
    ('DiscountReason', '04', '04 - For bank approval', 40, 1),
    ('DiscountReason', '05', '05 - Over-aged stock', 50, 1),
    ('DiscountReason', '06', '06 - Slow-moving engine', 60, 1),
    ('SalesChannel', 'Retail', 'Retail', 10, 1),
    ('SalesChannel', 'Leasing', 'Leasing', 20, 1),
    ('SalesChannel', 'Affinity', 'Affinity', 30, 1),
    ('SalesChannel', 'Government', 'Government', 40, 1),
    ('OrderType', 'SalesOrder', 'Sales Order', 10, 1),
    ('OrderType', 'FleetOrder', 'Fleet Order', 20, 1),
    -- Hardcoded Yes/No in the standalone app's own form; made a real, admin-editable
    -- lookup here for consistency with every other dropdown on this form.
    ('SpecialOrder', 'Yes', 'Yes', 10, 1),
    ('SpecialOrder', 'No', 'No', 20, 1);
GO

PRINT 'Sales Discount catalog seeded: type, 19 fields, picklists (DiscountReason/SalesChannel/OrderType/SpecialOrder). PicklistValues gained an ExtraData column for the Branch lookup''s account email. No Branch values, users, or routing rules -- add real branches under PicklistValues (LookupType=Branch, ExtraData=account email), then set up users and the approval chain through the app itself.';
GO
