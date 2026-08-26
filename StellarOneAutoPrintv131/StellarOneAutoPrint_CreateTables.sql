-- ============================================================================
-- StellarOne AutoPrint - PrintNode integration tables
-- ----------------------------------------------------------------------------
-- Creates the 3 tables backing SOStellarPrintNodeSetup, SOStellarPrintNodePrinter,
-- and SOStellarPrintNodeUserMap (see StellarOneAutoPrint_Setup.cs and
-- StellarOneAutoPrint_PrintNode_Transport.cs). Acumatica's automatic schema
-- sync did not create these when the customization was published, so this
-- script creates them by hand to match the DAC attributes exactly.
--
-- Column types/nullability/defaults below were not guessed - they were
-- verified against this instance's live AcumaticaDB schema (sys.columns,
-- sys.default_constraints, sys.indexes on SOSetup, GLConsolSetup, CABankFeed,
-- CCProcTran, CCSynchronizeCard) so every column matches what Acumatica's own
-- schema generator produces for the same attribute combinations:
--   - CompanyID int NOT NULL DEFAULT ((0)), always the first column and always
--     part of the primary key - matches every multi-tenant table checked.
--   - A DAC with no [IsKey] fields (SOStellarPrintNodeSetup, mirroring the
--     real, keyless SOSetup DAC) gets CompanyID alone as its primary key -
--     confirmed directly against SOSetup's actual PK (SOSetup_PK on
--     CompanyID only).
--   - [PXDBString(IsFixed = true)] with no explicit IsUnicode maps to plain
--     char, not nchar (matches SOSetup.FreightAllocation char(1),
--     MinGrossProfitValidation char(1), etc.).
--   - [PXDBString(IsUnicode = true)] maps to nvarchar.
--   - [PXRSACryptString] does not derive a consistent size from its declared
--     length in the real schema (CABankFeed.AccessToken/FolderPassword use
--     nvarchar(MAX) for the same no-arg attribute that GLConsolSetup.Password
--     stores as nvarchar(2000)) - nvarchar(MAX) used here to remove the
--     ambiguity entirely, matching the CABankFeed precedent for an equivalent
--     "external API credential" field.
--   - [PXDBBool] + [PXDefault(false, PersistingCheck = PXPersistingCheck.Nothing)]
--     produces a NULLABLE bit column with no DB-level default - confirmed
--     directly against the leftover SOSetup.UsrEnhancedPrintingEnabled column,
--     which used this exact attribute combination.
--   - [PXDefault(true)] (normal enforcement, no PersistingCheck override)
--     produces a NOT NULL bit column with a DEFAULT ((1)) constraint.
--   - Audit fields (CreatedByID/CreatedByScreenID/CreatedDateTime/
--     LastModifiedByID/LastModifiedByScreenID/LastModifiedDateTime) are all
--     NOT NULL with no DB-level default - Acumatica's framework always
--     supplies them on insert via the PXDBCreatedByID-family attributes.
--   - [PXDBDateAndTime] maps to plain datetime, not datetime2 (matches
--     GLConsolSetup.LastConsDate, CABankFeed.RetrievalDate, etc.).
--
-- Safe to re-run: each CREATE TABLE is guarded so this script is a no-op for
-- any table that already exists.
-- ============================================================================

-- ----------------------------------------------------------------------------
-- SOStellarPrintNodeSetup - standalone settings singleton (StellarOneAutoPrint_Setup.cs)
-- ----------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'SOStellarPrintNodeSetup')
BEGIN
    CREATE TABLE dbo.SOStellarPrintNodeSetup
    (
        CompanyID                int              NOT NULL CONSTRAINT DF_SOStellarPrintNodeSetup_CompanyID DEFAULT ((0)),
        EnhancedPrintingEnabled  bit              NULL,
        GiftNoteReportID         varchar(8)       NULL,
        ConfirmReportID          varchar(8)       NULL,
        PrintNodeApiKey          nvarchar(MAX)    NULL,
        GiftNotePrinterID        int              NULL,
        LabelPrinterID           int              NULL,
        ConfirmPrinterID         int              NULL,
        tstamp                   timestamp        NOT NULL,
        CreatedByID              uniqueidentifier NOT NULL,
        CreatedByScreenID        char(8)          NOT NULL,
        CreatedDateTime          datetime         NOT NULL,
        LastModifiedByID         uniqueidentifier NOT NULL,
        LastModifiedByScreenID   char(8)          NOT NULL,
        LastModifiedDateTime     datetime         NOT NULL,

        CONSTRAINT PK_SOStellarPrintNodeSetup PRIMARY KEY CLUSTERED (CompanyID)
    );
END
GO

-- ----------------------------------------------------------------------------
-- SOStellarPrintNodePrinter - synced PrintNode printer list (StellarOneAutoPrint_PrintNode_Transport.cs)
-- ----------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'SOStellarPrintNodePrinter')
BEGIN
    CREATE TABLE dbo.SOStellarPrintNodePrinter
    (
        CompanyID          int              NOT NULL CONSTRAINT DF_SOStellarPrintNodePrinter_CompanyID DEFAULT ((0)),
        PrinterID          int              NOT NULL,
        PrinterName        nvarchar(100)    NULL,
        ComputerName       nvarchar(100)    NULL,
        State              nvarchar(20)     NULL,
        IsActive           bit              NOT NULL CONSTRAINT DF_SOStellarPrintNodePrinter_IsActive DEFAULT ((1)),
        LastSyncedDateTime datetime         NULL,

        CONSTRAINT PK_SOStellarPrintNodePrinter PRIMARY KEY CLUSTERED (CompanyID, PrinterID)
    );
END
GO

-- ComputerName added after initial release - table above may already exist
-- without it, so add the column separately for that case (matches
-- PrinterName's nullable nvarchar(100) shape; no default needed since
-- IsActive/LastSyncedDateTime already tolerate NULL on old rows until the
-- next Test PrintNode Connection sync backfills it).
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.SOStellarPrintNodePrinter') AND name = 'ComputerName'
)
BEGIN
    ALTER TABLE dbo.SOStellarPrintNodePrinter ADD ComputerName nvarchar(100) NULL;
END
GO

-- ----------------------------------------------------------------------------
-- SOStellarPrintNodeUserMap - per-user printer overrides (StellarOneAutoPrint_PrintNode_Transport.cs)
-- ----------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'SOStellarPrintNodeUserMap')
BEGIN
    CREATE TABLE dbo.SOStellarPrintNodeUserMap
    (
        CompanyID     int              NOT NULL CONSTRAINT DF_SOStellarPrintNodeUserMap_CompanyID DEFAULT ((0)),
        UserID        uniqueidentifier NOT NULL,
        PrintJobType  char(20)         NOT NULL,
        PrinterID     int              NULL,
        PrinterName   nvarchar(100)    NULL,
        ComputerName  nvarchar(100)    NULL,
        IsActive      bit              NOT NULL CONSTRAINT DF_SOStellarPrintNodeUserMap_IsActive DEFAULT ((1)),

        CONSTRAINT PK_SOStellarPrintNodeUserMap PRIMARY KEY CLUSTERED (CompanyID, UserID, PrintJobType)
    );
END
GO

-- ComputerName added after initial release - see the matching guard above for
-- SOStellarPrintNodePrinter for why this is a separate ALTER rather than just
-- part of the CREATE TABLE above.
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.SOStellarPrintNodeUserMap') AND name = 'ComputerName'
)
BEGIN
    ALTER TABLE dbo.SOStellarPrintNodeUserMap ADD ComputerName nvarchar(100) NULL;
END
GO

-- ----------------------------------------------------------------------------
-- SOStellarPrintJobLog - print/reprint audit log (StellarOneAutoPrint_PrintJobLog.cs)
-- ----------------------------------------------------------------------------
-- LogID is a plain int IDENTITY, not scoped per-CompanyID (matches the
-- convention other identity-keyed Acumatica tables use - gaps across tenants
-- are normal and fine here since this table is append-only).
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'SOStellarPrintJobLog')
BEGIN
    CREATE TABLE dbo.SOStellarPrintJobLog
    (
        CompanyID         int              NOT NULL CONSTRAINT DF_SOStellarPrintJobLog_CompanyID DEFAULT ((0)),
        LogID             int              NOT NULL IDENTITY(1,1),
        ShipmentNbr       nvarchar(15)     NULL,
        PackageLineNbr    int              NULL,
        JobType           char(20)         NULL,
        Description       nvarchar(100)    NULL,
        PrinterID         int              NULL,
        PrinterName       nvarchar(100)    NULL,
        ComputerName      nvarchar(100)    NULL,
        PrintNodeJobID    bigint           NULL,
        Status            char(10)         NULL,
        ErrorMessage      nvarchar(2000)   NULL,
        SourceLogID       int              NULL,
        CreatedByID       uniqueidentifier NOT NULL,
        CreatedByScreenID char(8)          NOT NULL,
        CreatedDateTime   datetime         NOT NULL,

        CONSTRAINT PK_SOStellarPrintJobLog PRIMARY KEY CLUSTERED (CompanyID, LogID)
    );
END
GO

-- NoteID added after initial release, for the GiftNote/Confirmation PDF
-- attachment PrintJobLogger.Write saves against each row (see the NoteID
-- region in StellarOneAutoPrint_PrintJobLog.cs and PXNoteAttribute.AttachFile
-- there). Nullable uniqueidentifier with no default - matches the shape
-- Acumatica's own schema generator produces for [PXNote] on every native
-- PXNote-backed table. Table above may already exist without it, so add the
-- column separately (same guarded pattern as ComputerName above).
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.SOStellarPrintJobLog') AND name = 'NoteID'
)
BEGIN
    ALTER TABLE dbo.SOStellarPrintJobLog ADD NoteID uniqueidentifier NULL;
END
GO
