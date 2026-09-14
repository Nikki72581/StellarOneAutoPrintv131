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
// Modern UI wiring lives entirely in SO302000_StellarOneMyPrinterPrefs.ts/.html
// (FrontendSources\screen\src\development\screens\SO\SO302000\extensions) - a
// plain qp-panel + a purely client-side qp-button (config.bind popupPanel, no
// PXActionState/server action). Confirmed 2026-09-14 by reading client-controls'
// actual qp-button.js: a button bound to a real PXActionState via state.bind can
// never also open a config.bind popupPanel - its click handler returns as soon
// as an action/commandName is present, before ever checking popupPanel. The
// provisioning that used to run in this button's own action handler
// (EnsureAllJobTypesProvisioned) now runs from MyPrinterPrefs' own view
// delegate below instead, so it fires whenever the panel's grid loads data
// regardless of how the popup was opened.
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

        // View delegate (Acumatica recognizes a method named exactly like the
        // DataView field as its custom Select), not a PXAction/RowSelecting on
        // SOStellarPrintNodeUserMap generally - this must only run when THIS
        // specific view is queried, not on every SOStellarPrintNodeUserMap
        // lookup in the graph (RunPrintNode's IsUserMigratedToPrintNode/
        // ResolvePrintNodePrinter also query that DAC, on every shipment
        // confirm - a DAC-level RowSelecting would have fired there too and
        // provisioned rows for the confirming user on every single confirm,
        // not just when they actually open this panel).
        //
        // Previously wired to a PXAction ("OpenMyPrinterPrefs") triggered by
        // the Modern UI button's own server action - removed 2026-09-14 after
        // confirming, by reading client-controls' actual qp-button.js, that a
        // qp-button bound to a real PXActionState can never also open a
        // config.bind popupPanel: its click handler returns as soon as
        // this.config.action is set (which state.bind always populates from
        // the action's commandName), before ever checking popupPanel. Moving
        // the provisioning here instead makes it run automatically whenever
        // the panel's grid loads its data, regardless of how the popup was
        // opened - the Modern UI button is now a plain client-side
        // config.bind="{popupPanel: 'MyPrinterPrefs'}" trigger with no server
        // action at all (see SO302000_StellarOneMyPrinterPrefs.ts/.html).
        protected virtual IEnumerable myPrinterPrefs()
        {
            EnsureAllJobTypesProvisioned();

            // NOT MyPrinterPrefs.Select()/View.Select() here - Acumatica
            // dispatches those through this very delegate (matched by name),
            // so calling either back out from inside it is infinite
            // self-recursion, not "the base select". Confirmed live:
            // "Insufficient stack to continue executing the program safely."
            // A fresh, unrelated SelectFrom query (same pattern already used
            // by IsUserMigratedToPrintNode/ResolvePrintNodePrinter in
            // StellarOneAutoPrint.cs) returns the same rows without routing
            // back through MyPrinterPrefs' own dispatch at all.
            return SelectFrom<SOStellarPrintNodeUserMap>
                .Where<SOStellarPrintNodeUserMap.userID.IsEqual<@P.AsGuid>>
                .View.Select(Base, Base.Accessinfo.UserID);
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
