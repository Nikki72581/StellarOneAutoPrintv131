// ============================================================================
// StellarOne AutoPrint - Self-service printer preferences (PrintNode)
// ----------------------------------------------------------------------------
// DeviceHub (legacy) users already get self-service printer selection today via
// Acumatica's own stock "Printing Settings" tab on their User Profile
// (NotificationSetupUserOverride - see ResolveUserPrinterDeviceHub /
// ResolveUserLabelPrinterDeviceHub in StellarOneAutoPrint.cs). PrintNode-migrated
// users had no equivalent - only IT could edit SOStellarPrintNodeUserMap, via the
// admin-only "StellarOne PrintNode Setup" screen (StellarOneAutoPrint_Setup.cs),
// which also exposes the PrintNode API key and other IT-only settings.
//
// This closes that gap: a smart panel off Shipment Entry's (SO302000) toolbar,
// scoped to just the CURRENT user's own three job-type rows in the SAME
// SOStellarPrintNodeUserMap table IT already manages for everyone. No new DAC,
// no new database objects - purely a narrower, self-service way to edit rows in
// a table that already does the right thing at print time (ResolvePrintNodePrinter
// in StellarOneAutoPrint.cs already prefers a user's own mapping over the admin
// fallback printer, and already treats a missing/blank mapping as "use the
// fallback" - so a self-service row with no printer picked is automatically the
// correct "not set" state).
//
// Separate PXGraphExtension from SOShipmentEntryAutoPrintExt (StellarOneAutoPrint.cs)
// on purpose - that one is print DISPATCH, this one is user PREFERENCES, different
// concerns, matching this project's one-file-per-concern convention.
//
// You still need to: add a px:PXSmartPanel to SO302000's customization layer via
// the Screen Editor, bound to the MyPrinterPrefs view, with the toolbar button for
// OpenMyPrinterPrefs wired to open it via its PopupPanel property. Same kind of
// manual screen-editor follow-up StellarOneAutoPrint_Setup.cs already calls out
// for the admin screen's creation.
//
// IMPORTANT: this cache is shared with the whole SOShipmentEntry graph, since
// it's a view on a PXGraphExtension<SOShipmentEntry>, not its own standalone
// graph. PXGraph.Persist() - what the stock Shipment "Save" button triggers -
// sweeps up dirty rows across EVERY cache on the graph, not just the shipment
// document's own. Any row left dirty here (e.g. an auto-provisioned row nobody
// explicitly saved, or an edited one) gets swept into that persist too, and if
// it collides with a row that already exists in the DB, Acumatica reports it as
// "Another process has added the 'SOStellarPrintNodeUserMap' record" when
// saving the SHIPMENT - nothing to do with an actual concurrent user. Confirmed
// this the hard way against a real instance. The fix: never let a row in this
// cache sit dirty at all - persist immediately, right where it's created or
// edited (EnsureAllJobTypesProvisioned, and the printerID FieldUpdated handler
// below), so there's nothing left over for an unrelated Persist() to trip on.
// This also means no separate "Save" button is needed in the panel - picking a
// printer saves it immediately; the panel only needs a Close button.
// ============================================================================

using System;
using System.Collections;
using PX.Data;
using PX.Data.BQL;
using PX.Data.BQL.Fluent;
using PX.Objects.SO;

namespace StellarOneAutoPrint
{
    public class SOShipmentEntryMyPrinterPrefsExt : PXGraphExtension<SOShipmentEntry>
    {
        public static bool IsActive() => true;

        public PXSelect<SOStellarPrintNodeUserMap,
            Where<SOStellarPrintNodeUserMap.userID, Equal<Current<AccessInfo.userID>>>> MyPrinterPrefs;

        // Graph-scoped field overrides - only affect how SOStellarPrintNodeUserMap
        // renders on THIS graph (Shipment Entry's panel), not on the admin Setup
        // screen (SOStellarPrintNodeSetupMaint), which still needs both fields
        // editable so IT can add/manage mappings for other users. PrintJobType is
        // locked read-only since EnsureAllJobTypesProvisioned already guarantees
        // all 3 rows exist - the user is only ever changing PrinterID. UserID is
        // hidden entirely since every row on this panel is always "me" (see the
        // RowInserting/RowUpdating guards below for the server-side enforcement of
        // that - this is just the matching UI treatment).
        [PXMergeAttributes(Method = MergeMethod.Merge)]
        [PXUIField(DisplayName = "Print Job Type", Enabled = false)]
        protected virtual void _(Events.CacheAttached<SOStellarPrintNodeUserMap.printJobType> e) { }

        [PXMergeAttributes(Method = MergeMethod.Merge)]
        [PXUIField(DisplayName = "User", Visible = false)]
        protected virtual void _(Events.CacheAttached<SOStellarPrintNodeUserMap.userID> e) { }

        public PXAction<SOShipment> OpenMyPrinterPrefs;
        [PXUIField(DisplayName = "My Printer Preferences")]
        [PXButton]
        protected virtual IEnumerable openMyPrinterPrefs(PXAdapter adapter)
        {
            EnsureAllJobTypesProvisioned();
            return adapter.Get();
        }

        /// <summary>
        /// Guarantees the panel always shows exactly one row per PrintJobTypes
        /// constant for the current user, inserting any missing ones and
        /// persisting each immediately (see class header for why) so the user
        /// never has to manually Add Row + pick a job type, and nothing is left
        /// dirty in this shared cache. A blank PrinterID on any row means "use
        /// the admin fallback printer" - see class header.
        ///
        /// The existence check is a direct, parameterized SQL lookup per job
        /// type (same pattern as ResolvePrintNodePrinter in
        /// StellarOneAutoPrint.cs), not an in-memory HashSet compare against
        /// MyPrinterPrefs.Select() results - PrintJobType is a fixed-length
        /// CHAR(20) column ([PXDBString(20, IsFixed = true)]), and comparing
        /// that in memory against the plain PrintJobTypes constants was
        /// unreliable (found the hard way: it kept reporting "not found" for a
        /// row that genuinely already existed, causing a duplicate-key
        /// violation on every click). A parameterized SQL comparison doesn't
        /// have that problem - SQL Server compares CHAR columns against a
        /// shorter parameter correctly regardless of padding.
        /// </summary>
        private void EnsureAllJobTypesProvisioned()
        {
            Guid? userID = Base.Accessinfo?.UserID;
            if (!userID.HasValue) return;

            foreach (string jobType in new[]
                     { PrintJobTypes.GiftNote, PrintJobTypes.CarrierLabel, PrintJobTypes.Confirmation })
            {
                var existingRow = SelectFrom<SOStellarPrintNodeUserMap>
                    .Where<SOStellarPrintNodeUserMap.userID.IsEqual<@P.AsGuid>
                        .And<SOStellarPrintNodeUserMap.printJobType.IsEqual<@P.AsString>>>
                    .View.SelectSingleBound(Base, null, userID.Value, jobType)
                    .TopFirst;

                if (existingRow != null) continue;

                var row = MyPrinterPrefs.Insert(new SOStellarPrintNodeUserMap
                {
                    UserID = userID,
                    PrintJobType = jobType
                });
                PersistRowImmediately(row, PXDBOperation.Insert);
            }
        }

        /// <summary>
        /// Persists this one row right away and marks it clean, so it can never
        /// be swept up by an unrelated full-graph Persist() later (e.g. the
        /// stock Shipment Save button) - see the "IMPORTANT" note in the class
        /// header for why that matters here specifically. Mirrors the existing
        /// per-cache Cache.Persist idiom already used by SyncPrinters in
        /// SOStellarPrintNodeSetupMaint (StellarOneAutoPrint_Setup.cs); the
        /// explicit SetStatus afterward is the same "belt and suspenders"
        /// precaution SetPrintedFlag uses in StellarOneAutoPrint.cs.
        /// </summary>
        private void PersistRowImmediately(SOStellarPrintNodeUserMap row, PXDBOperation operation)
        {
            MyPrinterPrefs.Cache.Persist(row, operation);
            MyPrinterPrefs.Cache.SetStatus(row, PXEntryStatus.Notchanged);
        }

        protected virtual void _(Events.FieldUpdated<SOStellarPrintNodeUserMap, SOStellarPrintNodeUserMap.printerID> e)
        {
            var row = (SOStellarPrintNodeUserMap)e.Row;
            if (row == null) return;

            PrintNodeUserMapHelper.ApplyPrinterLookup(Base, row);
            PersistRowImmediately(row, PXDBOperation.Update);
        }

        // Defense in depth: MyPrinterPrefs' Where clause only filters what gets
        // selected into this cache - it does not stop a crafted insert/update
        // request from supplying a different UserID (a real, editable DAC key
        // field, shared with the admin screen where IT edits other users' rows).
        protected virtual void _(Events.RowInserting<SOStellarPrintNodeUserMap> e)
        {
            if (e.Row == null) return;
            e.Row.UserID = Base.Accessinfo?.UserID;
        }

        protected virtual void _(Events.RowUpdating<SOStellarPrintNodeUserMap> e)
        {
            if (e.NewRow == null) return;
            if (e.NewRow.UserID != Base.Accessinfo?.UserID)
                throw new PXException("You can only edit your own printer preferences.");
        }
    }
}
