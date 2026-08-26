// ============================================================================
// StellarOne AutoPrint - Standalone Setup screen (replaces the SO101000 tab)
// ----------------------------------------------------------------------------
// Originally this configuration lived as PXCacheExtension fields bolted onto
// SOSetup/SOSetupMaint (SO101000). That meant fighting the stock screen's
// existing tab/panel layout in the Screen Editor for every field placement.
// Moved to a fully standalone DAC + graph + screen instead - own table, own
// blank layout to build up from scratch, no collision risk with SOSetup ever
// changing shape in a future Acumatica upgrade.
//
// SOStellarPrintNodeSetup deliberately has NO key fields, mirroring the real,
// verified shape of PX.Objects.SO.SOSetup itself (checked via reflection over
// this build's PX.Objects.DLL - SOSetup has zero [IsKey] fields anywhere).
// That's the actual Acumatica convention for a true one-row Setup table, not
// a shortcut - a keyless PXSelect<T> is what makes the "there's exactly one
// implicit row, auto-created as soon as you start entering data" behavior
// work without any extra plumbing.
//
// This single graph now owns everything that used to be split across
// SOSetupMaintPrintNodeExt (deleted): the general settings view, the
// per-user printer mapping grid, the synced printer list, Test Connection,
// and Refresh Printer Name.
//
// You still need to: create a new screen in the Customization Project
// pointing at SOStellarPrintNodeSetupMaint, pick a free Screen ID for your
// site, and lay out the fields/grids on it via the Screen Editor - starting
// from a blank layout, not one you have to work around.
// ============================================================================

using System;
using System.Collections;
using PX.Data;
using PX.Data.BQL;
using PX.Data.BQL.Fluent;
using PX.SM;

namespace StellarOneAutoPrint
{
    [Serializable]
    [PXCacheName("StellarOne PrintNode Setup")]
    public class SOStellarPrintNodeSetup : PXBqlTable, IBqlTable
    {
        #region EnhancedPrintingEnabled
        public abstract class enhancedPrintingEnabled : BqlBool.Field<enhancedPrintingEnabled> { }

        [PXDBBool]
        [PXDefault(false, PersistingCheck = PXPersistingCheck.Nothing)]
        [PXUIField(DisplayName = "Enable Enhanced Auto-Printing")]
        public virtual bool? EnhancedPrintingEnabled { get; set; }
        #endregion

        #region GiftNoteReportID
        public abstract class giftNoteReportID : BqlString.Field<giftNoteReportID> { }

        [PXDBString(8, IsUnicode = false, InputMask = ">aa######")]
        [PXUIField(DisplayName = "Gift Note Report")]
        [PXSelector(
            typeof(Search<SiteMap.screenID, Where<SiteMap.url, Like<reportUrlPattern>>>),
            typeof(SiteMap.screenID),
            typeof(SiteMap.title),
            DescriptionField = typeof(SiteMap.title))]
        public virtual string GiftNoteReportID { get; set; }
        #endregion

        #region ConfirmReportID
        public abstract class confirmReportID : BqlString.Field<confirmReportID> { }

        [PXDBString(8, IsUnicode = false, InputMask = ">aa######")]
        [PXUIField(DisplayName = "Confirmation Report")]
        [PXSelector(
            typeof(Search<SiteMap.screenID, Where<SiteMap.url, Like<reportUrlPattern>>>),
            typeof(SiteMap.screenID),
            typeof(SiteMap.title),
            DescriptionField = typeof(SiteMap.title))]
        public virtual string ConfirmReportID { get; set; }
        #endregion

        public class reportUrlPattern : BqlString.Constant<reportUrlPattern>
        {
            public reportUrlPattern() : base("%.rpx%") { }
        }

        #region PrintNodeApiKey
        public abstract class printNodeApiKey : BqlString.Field<printNodeApiKey> { }

        // Encrypted at rest via PXRSACryptString - see the header of
        // StellarOneAutoPrint_PrintNode_Transport.cs for why this attribute
        // (not PXDBEncryptedString) is the one that actually exists/compiles
        // on this build. Mask on screen via Password="True" on the generated
        // px:PXTextEdit control - not a C# attribute. Also restrict this
        // field via Access Rights by Screen once the new screen has an ID.
        [PXRSACryptString(500)]
        [PXUIField(DisplayName = "PrintNode API Key")]
        public virtual string PrintNodeApiKey { get; set; }
        #endregion

        #region GiftNotePrinterID
        public abstract class giftNotePrinterID : BqlInt.Field<giftNotePrinterID> { }

        [PXDBInt]
        [PXUIField(DisplayName = "Gift Note Printer (Fallback)")]
        [PrintNodePrinterSelector]
        [PrintNodePrinterActiveRestrictor]
        public virtual int? GiftNotePrinterID { get; set; }
        #endregion

        #region LabelPrinterID
        public abstract class labelPrinterID : BqlInt.Field<labelPrinterID> { }

        [PXDBInt]
        [PXUIField(DisplayName = "Label Printer (Fallback)")]
        [PrintNodePrinterSelector]
        [PrintNodePrinterActiveRestrictor]
        public virtual int? LabelPrinterID { get; set; }
        #endregion

        #region ConfirmPrinterID
        public abstract class confirmPrinterID : BqlInt.Field<confirmPrinterID> { }

        [PXDBInt]
        [PXUIField(DisplayName = "Confirmation Printer (Fallback)")]
        [PrintNodePrinterSelector]
        [PrintNodePrinterActiveRestrictor]
        public virtual int? ConfirmPrinterID { get; set; }
        #endregion

        #region tstamp
        public abstract class tstamp : BqlByteArray.Field<tstamp> { }

        [PXDBTimestamp]
        public virtual byte[] Tstamp { get; set; }
        #endregion

        #region CreatedByID
        public abstract class createdByID : BqlGuid.Field<createdByID> { }
        [PXDBCreatedByID]
        public virtual Guid? CreatedByID { get; set; }
        #endregion

        #region CreatedByScreenID
        public abstract class createdByScreenID : BqlString.Field<createdByScreenID> { }
        [PXDBCreatedByScreenID]
        public virtual string CreatedByScreenID { get; set; }
        #endregion

        #region CreatedDateTime
        public abstract class createdDateTime : BqlDateTime.Field<createdDateTime> { }
        [PXDBCreatedDateTime]
        public virtual DateTime? CreatedDateTime { get; set; }
        #endregion

        #region LastModifiedByID
        public abstract class lastModifiedByID : BqlGuid.Field<lastModifiedByID> { }
        [PXDBLastModifiedByID]
        public virtual Guid? LastModifiedByID { get; set; }
        #endregion

        #region LastModifiedByScreenID
        public abstract class lastModifiedByScreenID : BqlString.Field<lastModifiedByScreenID> { }
        [PXDBLastModifiedByScreenID]
        public virtual string LastModifiedByScreenID { get; set; }
        #endregion

        #region LastModifiedDateTime
        public abstract class lastModifiedDateTime : BqlDateTime.Field<lastModifiedDateTime> { }
        [PXDBLastModifiedDateTime]
        public virtual DateTime? LastModifiedDateTime { get; set; }
        #endregion
    }

    public class SOStellarPrintNodeSetupMaint : PXGraph<SOStellarPrintNodeSetupMaint>
    {
        public PXSelect<SOStellarPrintNodeSetup> Setup;
        public PXSave<SOStellarPrintNodeSetup> Save;
        public PXCancel<SOStellarPrintNodeSetup> Cancel;

        public PXSelect<SOStellarPrintNodeUserMap> PrintNodeUserMappings;

        // Read-only reference grid, if you choose to place it - shows the
        // last-synced PrintNode printer list without a live API call.
        public PXSelect<SOStellarPrintNodePrinter> PrintNodePrinters;

        public PXAction<SOStellarPrintNodeSetup> TestPrintNodeConnection;
        [PXUIField(DisplayName = "Test PrintNode Connection")]
        [PXButton]
        protected virtual IEnumerable testPrintNodeConnection(PXAdapter adapter)
        {
            var setup = Setup.Current;
            if (string.IsNullOrEmpty(setup?.PrintNodeApiKey))
                throw new PXException("Enter a PrintNode API key first.");

            var printers = new PrintNodeApiService(setup.PrintNodeApiKey).GetPrinters();
            int changed = SyncPrinters(printers);

            PXTrace.WriteInformation(
                "PrintNode connection OK - {0} printer(s) visible to this API key, {1} row(s) synced.",
                printers.Count, changed);

            return adapter.Get();
        }

        /// <summary>
        /// Upserts SOStellarPrintNodePrinter from a live GetPrinters() result and
        /// soft-deactivates (never deletes) local rows no longer returned by the
        /// API, so printer IDs already assigned elsewhere keep resolving to a
        /// name. Persists immediately, independent of whatever else is pending
        /// on the rest of this form, since this is a one-off sync action rather
        /// than a form field edit.
        /// </summary>
        private int SyncPrinters(System.Collections.Generic.List<PrintNodePrinter> printers)
        {
            int changed = 0;
            var seenIds = new System.Collections.Generic.HashSet<int>();

            foreach (var printer in printers)
            {
                seenIds.Add(printer.Id);

                var existing = SelectFrom<SOStellarPrintNodePrinter>
                    .Where<SOStellarPrintNodePrinter.printerID.IsEqual<@P.AsInt>>
                    .View.SelectSingleBound(this, null, printer.Id)
                    .TopFirst;

                var row = existing == null
                    ? new SOStellarPrintNodePrinter { PrinterID = printer.Id }
                    : (SOStellarPrintNodePrinter)PrintNodePrinters.Cache.CreateCopy(existing);

                row.PrinterName = printer.Name;
                row.ComputerName = printer.ComputerName;
                row.State = printer.State;
                row.IsActive = true;
                row.LastSyncedDateTime = DateTime.Now;

                row = existing == null ? PrintNodePrinters.Insert(row) : PrintNodePrinters.Update(row);
                PrintNodePrinters.Cache.Persist(row, existing == null ? PXDBOperation.Insert : PXDBOperation.Update);
                changed++;
            }

            foreach (SOStellarPrintNodePrinter row in PrintNodePrinters.Select())
            {
                if (row.IsActive == true && !seenIds.Contains(row.PrinterID ?? -1))
                {
                    var copy = (SOStellarPrintNodePrinter)PrintNodePrinters.Cache.CreateCopy(row);
                    copy.IsActive = false;
                    copy = PrintNodePrinters.Update(copy);
                    PrintNodePrinters.Cache.Persist(copy, PXDBOperation.Update);
                    changed++;
                }
            }

            return changed;
        }

        public PXAction<SOStellarPrintNodeUserMap> RefreshPrinterName;
        [PXUIField(DisplayName = "Refresh Printer Name")]
        [PXButton]
        protected virtual IEnumerable refreshPrinterName(PXAdapter adapter)
        {
            var row = PrintNodeUserMappings.Current;
            if (row?.PrinterID == null) return adapter.Get();

            PrintNodeUserMapHelper.ApplyPrinterLookup(this, row);
            PrintNodeUserMappings.Update(row);

            return adapter.Get();
        }

        protected virtual void _(Events.FieldUpdated<SOStellarPrintNodeUserMap, SOStellarPrintNodeUserMap.printerID> e)
        {
            var row = (SOStellarPrintNodeUserMap)e.Row;
            if (row?.PrinterID == null) return;

            PrintNodeUserMapHelper.ApplyPrinterLookup(this, row);
        }
    }
}
