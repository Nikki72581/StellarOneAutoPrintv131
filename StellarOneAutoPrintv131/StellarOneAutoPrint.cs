// ============================================================================
// StellarOne Auto-Print on Shipment Confirmation (unified)
// ----------------------------------------------------------------------------
// Automatically prints up to three documents when a shipment is confirmed:
// Gift Note, Carrier Label, Shipment Confirmation. This single event handler
// now owns BOTH delivery transports:
//
//   - PrintNode  - the target state. Renders each report to PDF (or forwards
//                  raw carrier-label bytes) and submits it to PrintNode over
//                  HTTPS. See StellarOneAutoPrint_PrintNode_Transport.cs and
//                  StellarOneAutoPrint_Setup.cs.
//   - DeviceHub  - the legacy path, kept only for users not yet migrated to
//                  PrintNode. Ported unchanged from StellarOneAutoPrintv13.
//                  See StellarOneAutoPrint_DeviceHub.cs for its settings DAC.
//
// Which transport a given shipment uses is decided per user by the existing
// migration switch, IsUserMigratedToPrintNode(): true once an admin has given
// that user at least one active row in SOStellarPrintNodeUserMap. Until then,
// the shipment falls through to the DeviceHub branch, reproducing v13's
// original behavior exactly.
//
// History: v13 (StellarOneAutoPrintv13) and this project used to be two
// separate Acumatica Customization Projects, published side by side during
// the gradual per-user migration off DeviceHub - each compiling into the same
// merged assembly, which meant neither could declare SOShipmentExt or a
// same-named graph extension without a hard duplicate-class collision. That
// coexistence period is over: this project now folds v13's logic in directly
// (see git history / StellarOneAutoPrint_DeviceHub.cs and
// StellarOneAutoPrint_Shipment.cs for what moved), so there is exactly one
// Customization Project to edit, build, and publish going forward. v13's
// project should be unpublished once this merged version is verified on
// Sandbox - see README.md's deployment checklist for the required order
// (unpublish v13 before publishing this, to avoid the same duplicate-class
// collision during the cutover window).
//
// Why TranStatus.Completed, not Open (applies to both branches now): printing
// is an irreversible external side effect (hits DeviceHub/PrintNode and a
// physical printer). Firing on Open risked a scenario where a later rollback
// in the same Persist() call rolled back the printed-flag write while the
// physical print had already gone out, causing a duplicate print on the next
// successful confirm. Gating on Completed guarantees the flag write is
// durable first. (v13 originally fired on Open; this fix, already reviewed
// and live in the PrintNode branch, now also covers the DeviceHub branch
// since both live in the same handler.)
//
// That handler's body runs inside a PXConnectionScope + PXCommandScope (see
// the RowPersisted handler below) - Confirm Shipment drives it through
// PXProcessing<T>.ProcessRecords, which keeps an open reader on the graph's
// ambient connection for the whole batch, and every DB call this handler
// makes (directly or via RunPrintNode/RunDeviceHub/PrintJobLogger) would
// otherwise collide with that reader and throw "There is already an open
// DataReader associated with this Connection which must be closed first,"
// aborting the Confirm entirely. Isolating onto a separate connection fixes
// that, but that connection then has to wait out the locks ConfirmShipment's
// own (still-open) transaction holds on this shipment - PXCommandScope gives
// it enough time to do that instead of hitting the default 30s timeout.
//
// Two separate hooks on SOShipment, for two different reasons:
//   - RowPersisting resets the printed-flags when the shipment gets
//     un-confirmed (Correct Shipment). Plain SetValueExt, no raw SQL - this
//     fires before the row's UPDATE is generated, so the reset rides along
//     in that same UPDATE with tstamp handled natively.
//   - RowPersisted(Completed) fires the actual printing once a successful
//     confirm is durable (see above). Because that row's UPDATE has
//     already executed by this point, marking the printed-flags requires a
//     second, raw write (SetPrintedFlag) - which takes care to leave the
//     row clean afterward so it can't collide with a later persist in the
//     same session (e.g. a Correct Shipment click right after). See
//     SetPrintedFlag's doc comment for the "record has been changed by
//     another process" bug this fixed.
//
// Components:
//   SOShipmentExt               - 3 "already printed" guard flags on Shipment,
//                                  shared by both branches (StellarOneAutoPrint_Shipment.cs)
//   SOSetupExt                  - legacy DeviceHub settings, 8 fields on SO
//                                  Preferences (StellarOneAutoPrint_DeviceHub.cs)
//   SOStellarPrintNodeSetup     - PrintNode settings, standalone screen
//                                  (StellarOneAutoPrint_Setup.cs)
//   SOShipmentEntryAutoPrintExt - main graph extension; all print logic here
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading;
using PX.Data;
using PX.Data.BQL;
using PX.Data.BQL.Fluent;
using PX.SM;
using PX.Objects.CS;
using PX.Objects.SO;

namespace StellarOneAutoPrint
{
    public class SOShipmentEntryAutoPrintExt : PXGraphExtension<SOShipmentEntry>
    {
        public static bool IsActive() => true;

        // Correct Shipment persists the shipment with Confirmed = false as
        // part of its own internal Save.Press() (see
        // SOShipmentEntry.CorrectShipment). It sets Confirmed via a direct
        // property assignment inside MarkOpen() and then forces the row
        // dirty with Cache.MarkUpdated() rather than going through the
        // normal SetValueExt path, so RowUpdated never fires for this
        // change. RowPersisting, like RowPersisted, fires for every dirty
        // row regardless of how it became dirty, so it still reliably
        // catches this - and unlike RowPersisted it fires BEFORE the row's
        // UPDATE is generated, so a plain SetValueExt here rides along in
        // that same UPDATE with tstamp handled natively by the framework.
        // (This used to run in RowPersisted via a raw PXDatabase.Update +
        // manual tstamp refresh; that left the row dirty with a stale
        // cached tstamp, which made Correct Shipment's own follow-up
        // Save.Press() fail with "record has been changed by another
        // process." See git history for the old approach.)
        protected virtual void _(Events.RowPersisting<SOShipment> e)
        {
            var shipment = e.Row;
            if (shipment == null) return;
            if (shipment.Confirmed == true) return;

            var shipExtOnCorrect = PXCache<SOShipment>.GetExtension<SOShipmentExt>(shipment);
            bool anyFlagSet = shipExtOnCorrect?.UsrGiftNotePrinted == true
                            || shipExtOnCorrect?.UsrLabelPrinted == true
                            || shipExtOnCorrect?.UsrConfirmPrinted == true;

            if (!anyFlagSet) return;

            PXTrace.WriteInformation(
                "AutoPrint: Resetting printed flags on {0} (shipment un-confirmed, e.g. Correct Shipment).",
                shipment.ShipmentNbr);

            Base.Document.Cache.SetValueExt<SOShipmentExt.usrGiftNotePrinted>(shipment, false);
            Base.Document.Cache.SetValueExt<SOShipmentExt.usrLabelPrinted>(shipment, false);
            Base.Document.Cache.SetValueExt<SOShipmentExt.usrConfirmPrinted>(shipment, false);
        }

        protected virtual void _(Events.RowPersisted<SOShipment> e)
        {
            if (e.TranStatus != PXTranStatus.Completed) return;
            if (e.Operation != PXDBOperation.Insert && e.Operation != PXDBOperation.Update) return;

            var shipment = e.Row;
            if (shipment == null) return;
            if (shipment.Confirmed != true) return;

            Guid? currentUserID = Base.Accessinfo?.UserID;

            // Confirm Shipment drives this handler through PXProcessing<T>.ProcessRecords
            // (true even for a single shipment - the Confirm Shipment action is mass-process
            // infrastructure regardless of how many rows are selected), which keeps an open
            // reader on the graph's ambient connection for the duration of the whole batch,
            // and TranStatus.Completed does NOT mean this shipment's outer transaction has
            // committed - ConfirmShipment(args) keeps going after that (invoicing, external
            // carrier calls, etc.) inside the same PXTransactionScope, still holding locks on
            // this shipment's rows. Every raw BQL/PXDatabase call this handler makes - printer
            // lookups, SetPrintedFlag/RefreshShipmentTstamp, PrintJobLogger's insert - runs
            // WithIsolatedConnection (below) for exactly that reason: an independent connection
            // (PXConnectionScope) so it can't collide with ProcessRecords' open reader, and a
            // generous command timeout (PXCommandScope) so it can wait out those locks instead
            // of hitting the default 30s timeout.
            //
            // Deliberately NOT wrapped here at the top level, and NOT wrapped in the full
            // WithIsolatedConnection (second physical connection) around the report rendering
            // or PrintNode/DeviceHub submission calls specifically: report rendering
            // (RenderReportToPdfBytes) issues its own internal queries, and swapping THOSE onto
            // a second connection caused a second incident - report rendering that used to
            // complete normally started hanging for 10+ minutes per document, unresponsive even
            // to Acumatica's own Thread.Abort watchdog. Report rendering instead runs under
            // WithExtendedTimeout (PXCommandScope only, no PXConnectionScope - see its doc
            // comment) so its queries stay on the ambient connection but get the same generous
            // timeout to wait out the lock. WithIsolatedConnection itself is applied narrowly,
            // only around the specific raw DB calls that actually had the reader/lock problem in
            // the first place - see each call site below.
            if (IsUserMigratedToPrintNode(currentUserID))
                RunPrintNode(shipment, currentUserID);
            else
                RunDeviceHub(shipment, currentUserID);
        }

        /// <summary>
        /// Runs a raw BQL/PXDatabase operation on an independent connection with a generous
        /// command timeout, so it can't collide with whatever open reader/lock Confirm Shipment's
        /// own still-in-progress transaction holds on this shipment's rows - see the RowPersisted
        /// handler's comment for the full story. Deliberately narrow: apply this ONLY to simple,
        /// known-fast raw calls (a SelectSingleBound, a PXDatabase.Update, PrintJobLogger's plain
        /// DAC insert) - never around report rendering or the PrintNode/DeviceHub submission
        /// calls, which aren't vulnerable to the original problem and, it turns out, behave badly
        /// under an extended timeout for unrelated reasons of their own.
        /// </summary>
        private static T WithIsolatedConnection<T>(Func<T> query)
        {
            using (new PXConnectionScope())
            using (new PXCommandScope(300))
            {
                return query();
            }
        }

        private static void WithIsolatedConnection(Action action)
        {
            using (new PXConnectionScope())
            using (new PXCommandScope(300))
            {
                action();
            }
        }

        /// <summary>
        /// PXCommandScope(300) alone - NOT paired with PXConnectionScope. Decompiling
        /// PX.Data confirms these two scopes are fully independent: PXCommandScope only
        /// stores a timeout override that PXDatabase.CreateCommand() applies to whatever
        /// connection is already ambient; it does not touch connection selection at all
        /// (that's PXConnectionScope's job, via PXConnectionScope.GetConnection()).
        ///
        /// Report rendering (RenderReportToPdfBytes, for Gift Note/Confirmation) has to
        /// stay on the ambient connection - wrapping it in a full WithIsolatedConnection
        /// (i.e. also swapping to a second physical connection) previously made rendering
        /// hang for 10+ minutes per document, unresponsive even to Acumatica's own
        /// Thread.Abort watchdog (see the RowPersisted handler's comment). But left fully
        /// unwrapped, the report's own internal data-source queries run with the SqlClient
        /// default 30s command timeout and were observed failing in production with
        /// "Execution Timeout Expired" while blocked behind Confirm Shipment's still-open
        /// outer transaction (SOStellarPrintJobLog, 2026-08-25, shipments 006653+ - same
        /// underlying lock-wait this whole isolation pattern exists to route around).
        /// This gives the ambient connection's commands the same generous 300s timeout to
        /// wait out those locks, without opening the second connection that caused the hang.
        /// </summary>
        private static T WithExtendedTimeout<T>(Func<T> query)
        {
            using (new PXCommandScope(300))
            {
                return query();
            }
        }

        // =====================================================================
        // PrintNode branch (target state)
        // =====================================================================

        private void RunPrintNode(SOShipment shipment, Guid? currentUserID)
        {
            var setup = LoadSetup();
            if (setup?.EnhancedPrintingEnabled != true) return;
            if (string.IsNullOrEmpty(setup?.PrintNodeApiKey)) return;

            var shipExt = PXCache<SOShipment>.GetExtension<SOShipmentExt>(shipment);
            var baseParams = BuildBaseParameters(shipment);

            var errors = new List<string>();
            void Collect(string error) { if (!string.IsNullOrEmpty(error)) errors.Add(error); }

            Collect(TryPrintGiftNote(shipment, shipExt, setup, currentUserID, baseParams));
            Collect(TryPrintLabel(shipment, shipExt, setup, currentUserID));
            Collect(TryPrintConfirmation(shipment, shipExt, setup, currentUserID, baseParams));

            if (errors.Count > 0)
            {
                // Non-blocking: the shipment confirm already committed (TranStatus.Completed).
                // This just surfaces a warning icon on the shipment so print failures aren't
                // only visible in the trace log.
                Base.Document.Cache.RaiseExceptionHandling<SOShipment.shipmentNbr>(
                    shipment, shipment.ShipmentNbr,
                    new PXSetPropertyException(shipment, string.Join(" ", errors), PXErrorLevel.Warning));
            }
        }

        /// <summary>Returns null on success or skip, or a user-facing error message on failure.</summary>
        private string TryPrintGiftNote(
            SOShipment shipment, SOShipmentExt shipExt, SOStellarPrintNodeSetup setup,
            Guid? userID, Dictionary<string, string> baseParams)
        {
            if (shipExt?.UsrGiftNotePrinted == true) return null;
            if (string.IsNullOrWhiteSpace(setup.GiftNoteReportID)) return null;
            if (!HasGiftNoteFlag(shipment)) return null;

            int? printerId = WithIsolatedConnection(() => ResolvePrintNodePrinter(userID, PrintJobTypes.GiftNote))
                             ?? setup.GiftNotePrinterID;
            string description = "Gift Note - " + shipment.ShipmentNbr;

            if (!printerId.HasValue)
            {
                string msg = "No PrintNode printer configured for Gift Note.";
                PXTrace.WriteWarning("AutoPrint: {0} ({1})", msg, shipment.ShipmentNbr);
                WithIsolatedConnection(() => PrintJobLogger.Write(Base, shipment.ShipmentNbr, null, PrintJobTypes.GiftNote, description,
                    null, PrintJobLogStatus.Error, msg, null));
                return msg;
            }

            try
            {
                // Stays on the ambient connection (not WithIsolatedConnection) - see
                // WithExtendedTimeout's doc comment for why - but still needs a longer
                // command timeout than the 30s default to wait out Confirm Shipment's
                // still-open outer transaction, same as the raw DB calls above and below.
                byte[] pdfBytes = WithExtendedTimeout(() =>
                    PrintNodeDocumentRenderer.RenderReportToPdfBytes(setup.GiftNoteReportID, baseParams, description));

                long jobId = new PrintNodeApiService(setup.PrintNodeApiKey)
                    .SubmitPrintJob(printerId.Value, pdfBytes, description);

                WithIsolatedConnection(() =>
                {
                    MarkPrinted<SOShipmentExt.usrGiftNotePrinted>(shipment);
                    PrintJobLogger.Write(Base, shipment.ShipmentNbr, null, PrintJobTypes.GiftNote, description,
                        printerId, PrintJobLogStatus.Success, null, jobId,
                        documentBytes: pdfBytes, documentFileName: description + ".pdf");
                });
                return null;
            }
            catch (Exception ex)
            {
                PXTrace.WriteError("AutoPrint: Gift Note FAILED for {0}: {1}", shipment.ShipmentNbr, ex.Message);
                WithIsolatedConnection(() => PrintJobLogger.Write(Base, shipment.ShipmentNbr, null, PrintJobTypes.GiftNote, description,
                    printerId, PrintJobLogStatus.Error, ex.Message, null));
                return "Gift Note printing failed: " + ex.Message;
            }
        }

        /// <summary>
        /// Carrier labels are already stored as file attachments on
        /// SOPackageDetailEx. We pull the actual bytes down ourselves and
        /// forward them to PrintNode.
        /// </summary>
        /// <summary>Returns null on success or skip, or a user-facing error message on failure.</summary>
        private string TryPrintLabel(
            SOShipment shipment, SOShipmentExt shipExt, SOStellarPrintNodeSetup setup, Guid? userID)
        {
            if (shipExt?.UsrLabelPrinted == true) return null;

            int? printerId = WithIsolatedConnection(() => ResolvePrintNodePrinter(userID, PrintJobTypes.CarrierLabel))
                             ?? setup.LabelPrinterID;

            if (!printerId.HasValue)
            {
                string msg = "No PrintNode label printer configured.";
                PXTrace.WriteWarning("AutoPrint: {0} (shipment {1})", msg, shipment.ShipmentNbr);
                WithIsolatedConnection(() => PrintJobLogger.Write(Base, shipment.ShipmentNbr, null, PrintJobTypes.CarrierLabel,
                    "Carrier Label - " + shipment.ShipmentNbr, null, PrintJobLogStatus.Error, msg, null));
                return msg;
            }

            bool anyPrinted = false;
            var service = new PrintNodeApiService(setup.PrintNodeApiKey);

            try
            {
                var packages = WithIsolatedConnection(() => SelectFrom<SOPackageDetailEx>
                    .Where<SOPackageDetailEx.shipmentNbr.IsEqual<@P.AsString>>
                    .View.Select(Base, shipment.ShipmentNbr));

                foreach (SOPackageDetailEx pkg in packages)
                {
                    if (pkg.NoteID == null) continue;

                    UploadFileMaintenance uploader = PXGraph.CreateInstance<UploadFileMaintenance>();
                    Guid[] fileIDs = null;
                    Guid? latestFileID = null;
                    PX.SM.FileInfo labelFile = null;

                    // File lookups are raw DB reads - isolate them, same reason as everything
                    // else in this handler. Rendering/submission below stays unwrapped.
                    // GetFileNotes (PXNoteAttribute -> NoteDoc.SelectMulti) is a BQL query too -
                    // it has to be inside this isolated scope along with the GetFile lookups
                    // below, not run ambient before it. Left ambient, it was the actual cause of
                    // the "Execution Timeout Expired" errors hitting Carrier Label jobs in
                    // production: it collided with the same still-open outer transaction this
                    // whole isolation pattern exists to route around. See
                    // SOStellarPrintJobLog history from 2026-08-25 (shipments 006653+).
                    WithIsolatedConnection(() =>
                    {
                        fileIDs = PXNoteAttribute.GetFileNotes(Base.Packages.Cache, pkg);
                        if (fileIDs == null || fileIDs.Length == 0) return;

                        foreach (Guid fileID in fileIDs)
                        {
                            PX.SM.FileInfo fileInfo = uploader.GetFile(fileID);
                            if (fileInfo == null) continue;

                            string fileName = fileInfo.Name ?? string.Empty;
                            if (!fileName.StartsWith("Label", StringComparison.OrdinalIgnoreCase)) continue;

                            latestFileID = fileID; // last match in attachment order = newest
                        }

                        if (latestFileID.HasValue)
                            labelFile = uploader.GetFile(latestFileID.Value);
                    });

                    if (!latestFileID.HasValue) continue;

                    if (labelFile?.BinData == null || labelFile.BinData.Length == 0)
                    {
                        PXTrace.WriteWarning("AutoPrint: Label file {0} had no data for shipment {1} pkg {2}.",
                            latestFileID, shipment.ShipmentNbr, pkg.LineNbr);
                        continue;
                    }

                    // Label format is carrier-dependent and can vary package to
                    // package within the same shipment (e.g. UPS returning ZPL
                    // for one package, FedEx returning PDF for another), so the
                    // content type has to be detected per file rather than
                    // assumed for the whole shipment.
                    string contentType = PrintNodeDocumentRenderer.DetectPrintNodeContentType(labelFile.BinData);
                    string description = string.Format("Carrier Label - {0} pkg {1}", shipment.ShipmentNbr, pkg.LineNbr);
                    PXTrace.WriteInformation("AutoPrint: Label for {0} pkg {1} detected as {2}.",
                        shipment.ShipmentNbr, pkg.LineNbr, contentType);
                    long jobId = service.SubmitPrintJob(printerId.Value, labelFile.BinData, description, contentType);

                    WithIsolatedConnection(() => PrintJobLogger.Write(Base, shipment.ShipmentNbr, pkg.LineNbr, PrintJobTypes.CarrierLabel, description,
                        printerId, PrintJobLogStatus.Success, null, jobId));

                    anyPrinted = true;
                }

                if (anyPrinted)
                    WithIsolatedConnection(() => MarkPrinted<SOShipmentExt.usrLabelPrinted>(shipment));

                return null;
            }
            catch (Exception ex)
            {
                PXTrace.WriteError("AutoPrint: Label FAILED for {0}: {1}", shipment.ShipmentNbr, ex.Message);
                WithIsolatedConnection(() => PrintJobLogger.Write(Base, shipment.ShipmentNbr, null, PrintJobTypes.CarrierLabel,
                    "Carrier Label - " + shipment.ShipmentNbr, printerId, PrintJobLogStatus.Error, ex.Message, null));
                return "Label printing failed: " + ex.Message;
            }
        }

        /// <summary>Returns null on success or skip, or a user-facing error message on failure.</summary>
        private string TryPrintConfirmation(
            SOShipment shipment, SOShipmentExt shipExt, SOStellarPrintNodeSetup setup,
            Guid? userID, Dictionary<string, string> baseParams)
        {
            if (shipExt?.UsrConfirmPrinted == true) return null;
            if (string.IsNullOrWhiteSpace(setup.ConfirmReportID)) return null;

            int? printerId = WithIsolatedConnection(() => ResolvePrintNodePrinter(userID, PrintJobTypes.Confirmation))
                             ?? setup.ConfirmPrinterID;
            string description = "Shipment Confirmation - " + shipment.ShipmentNbr;

            if (!printerId.HasValue)
            {
                string msg = "No PrintNode printer configured for Confirmation.";
                PXTrace.WriteWarning("AutoPrint: {0} ({1})", msg, shipment.ShipmentNbr);
                WithIsolatedConnection(() => PrintJobLogger.Write(Base, shipment.ShipmentNbr, null, PrintJobTypes.Confirmation, description,
                    null, PrintJobLogStatus.Error, msg, null));
                return msg;
            }

            try
            {
                // Stays on the ambient connection - see WithExtendedTimeout's doc comment -
                // but still needs a longer command timeout than the 30s default to wait out
                // Confirm Shipment's still-open outer transaction.
                byte[] pdfBytes = WithExtendedTimeout(() =>
                    PrintNodeDocumentRenderer.RenderReportToPdfBytes(setup.ConfirmReportID, baseParams, description));

                long jobId = new PrintNodeApiService(setup.PrintNodeApiKey)
                    .SubmitPrintJob(printerId.Value, pdfBytes, description);

                WithIsolatedConnection(() =>
                {
                    MarkPrinted<SOShipmentExt.usrConfirmPrinted>(shipment);
                    PrintJobLogger.Write(Base, shipment.ShipmentNbr, null, PrintJobTypes.Confirmation, description,
                        printerId, PrintJobLogStatus.Success, null, jobId,
                        documentBytes: pdfBytes, documentFileName: description + ".pdf");
                });
                return null;
            }
            catch (Exception ex)
            {
                PXTrace.WriteError("AutoPrint: Confirmation FAILED for {0}: {1}", shipment.ShipmentNbr, ex.Message);
                WithIsolatedConnection(() => PrintJobLogger.Write(Base, shipment.ShipmentNbr, null, PrintJobTypes.Confirmation, description,
                    printerId, PrintJobLogStatus.Error, ex.Message, null));
                return "Confirmation printing failed: " + ex.Message;
            }
        }

        /// <summary>
        /// True once an admin has given this user at least one active
        /// SOStellarPrintNodeUserMap row - i.e. actually set them up in
        /// PrintNode. This is the migration switch this handler checks to
        /// decide which branch (PrintNode vs legacy DeviceHub) owns a given
        /// shipment. Behavior note: this flips the moment the FIRST active
        /// mapping row is added for a user, not on some separate "go live"
        /// toggle - set up a user's mappings only when you're ready for their
        /// next shipment confirm to route through PrintNode instead of
        /// DeviceHub.
        /// </summary>
        private bool IsUserMigratedToPrintNode(Guid? userID)
        {
            if (!userID.HasValue) return false;

            var mapping = WithIsolatedConnection(() => SelectFrom<SOStellarPrintNodeUserMap>
                .Where<SOStellarPrintNodeUserMap.userID.IsEqual<@P.AsGuid>
                    .And<SOStellarPrintNodeUserMap.isActive.IsEqual<True>>>
                .View.SelectSingleBound(Base, null, userID.Value)
                .TopFirst);

            return mapping != null;
        }

        /// <summary>
        /// Looks up SOStellarPrintNodeUserMap for (userID, jobType). Returns
        /// null if no active mapping exists, so the caller can fall back to
        /// the StellarOne PrintNode Setup default.
        /// </summary>
        private int? ResolvePrintNodePrinter(Guid? userID, string jobType)
        {
            if (!userID.HasValue) return null;

            var mapping = SelectFrom<SOStellarPrintNodeUserMap>
                .Where<SOStellarPrintNodeUserMap.userID.IsEqual<@P.AsGuid>
                    .And<SOStellarPrintNodeUserMap.printJobType.IsEqual<@P.AsString>>
                    .And<SOStellarPrintNodeUserMap.isActive.IsEqual<True>>>
                .View.SelectSingleBound(Base, null, userID.Value, jobType)
                .TopFirst;

            return mapping?.PrinterID;
        }

        /// <summary>
        /// SOStellarPrintNodeSetup is a standalone, keyless singleton table
        /// (see StellarOneAutoPrint_Setup.cs) - a plain PXSelect.Select() with
        /// no Where clause returns the single implicit row, same as how the
        /// stock SOSetup singleton itself is selected.
        /// </summary>
        private SOStellarPrintNodeSetup LoadSetup()
        {
            return WithIsolatedConnection(() => SelectFrom<SOStellarPrintNodeSetup>.View.Select(Base).TopFirst);
        }

        // =====================================================================
        // DeviceHub branch (legacy - users not yet migrated to PrintNode)
        // ----------------------------------------------------------------------
        // Ported unchanged from StellarOneAutoPrintv13's SOShipmentEntryAutoPrintExt.
        // Method names below are suffixed "DeviceHub" only to avoid clashing
        // with the PrintNode methods above now living in the same class - the
        // logic itself is untouched.
        // =====================================================================

        private void RunDeviceHub(SOShipment shipment, Guid? currentUserID)
        {
            var setup = LoadSetupExt();
            if (setup?.UsrEnhancedPrintingEnabled != true) return;

            var shipExt = PXCache<SOShipment>.GetExtension<SOShipmentExt>(shipment);
            var baseParams = BuildBaseParameters(shipment);

            TryPrintGiftNoteDeviceHub(shipment, shipExt, setup, currentUserID, baseParams);
            TryPrintLabelDeviceHub(shipment, shipExt, setup, currentUserID, baseParams);
            TryPrintConfirmationDeviceHub(shipment, shipExt, setup, currentUserID, baseParams);
        }

        private void TryPrintGiftNoteDeviceHub(
            SOShipment shipment, SOShipmentExt shipExt, SOSetupExt setup,
            Guid? userID, Dictionary<string, string> baseParams)
        {
            if (shipExt?.UsrGiftNotePrinted == true) return;
            if (string.IsNullOrWhiteSpace(setup.UsrGiftNoteReportID)) return;
            if (!HasGiftNoteFlag(shipment)) return;

            Guid? printerID = ResolveUserPrinterDeviceHub(userID, setup.UsrGiftNoteReportID)
                              ?? setup.UsrGiftNotePrinterID;

            if (!printerID.HasValue)
            {
                PXTrace.WriteWarning("AutoPrint: No printer for Gift Note on {0}.", shipment.ShipmentNbr);
                return;
            }

            try
            {
                // Not wrapped - matches the PrintNode branch's treatment of report
                // rendering/submission calls; see the RowPersisted handler's comment.
                QueuePrintJobDeviceHub(setup.UsrGiftNoteReportID, printerID.Value,
                    baseParams, "Gift Note - " + shipment.ShipmentNbr);

                WithIsolatedConnection(() => MarkPrinted<SOShipmentExt.usrGiftNotePrinted>(shipment));
            }
            catch (Exception ex)
            {
                PXTrace.WriteError("AutoPrint: Gift Note FAILED for {0}: {1}", shipment.ShipmentNbr, ex.Message);
            }
        }

        /// <summary>
        /// Carrier labels are stored as file attachments on SOPackageDetailEx.
        /// We send them to DeviceHub using the FILEID parameter pattern: pass
        /// the file's UID as "FILEID" with a null report ID, and CreatePrintJob
        /// handles delivery to the DeviceHub printer via polling. This works
        /// correctly across multiple DeviceHub instances - each job is routed
        /// to whichever DeviceHub instance owns the resolved printer.
        ///
        /// Credit: Brian Stevens (bstevens.net) for documenting the FILEID pattern.
        /// </summary>
        private void TryPrintLabelDeviceHub(
            SOShipment shipment, SOShipmentExt shipExt, SOSetupExt setup,
            Guid? userID, Dictionary<string, string> baseParams)
        {
            if (shipExt?.UsrLabelPrinted == true) return;

            Guid? printerID = ResolveUserLabelPrinterDeviceHub(userID, setup)
                              ?? setup.UsrLabelPrinterID;

            if (!printerID.HasValue)
            {
                PXTrace.WriteWarning("AutoPrint: No label printer configured for shipment {0}.", shipment.ShipmentNbr);
                return;
            }

            bool anyPrinted = false;

            try
            {
                var packages = WithIsolatedConnection(() => SelectFrom<SOPackageDetailEx>
                    .Where<SOPackageDetailEx.shipmentNbr.IsEqual<@P.AsString>>
                    .View.Select(Base, shipment.ShipmentNbr));

                foreach (SOPackageDetailEx pkg in packages)
                {
                    if (pkg.NoteID == null) continue;

                    // A package can end up with more than one attached file
                    // whose name starts with "Label" - e.g. one generated at
                    // pack time and a second generated later (carrier label
                    // reprint, WMS re-pack, etc.) with the original never
                    // removed. Printing every match duplicates the physical
                    // label, so only one is sent.
                    //
                    // PX.SM.FileInfo doesn't expose a modified/created date in
                    // this Acumatica version, so we can't sort by recency
                    // directly. GetFileNotes returns attachments in attachment
                    // order (oldest first), so we take the LAST matching file
                    // in that order, which is the most recently attached one.
                    UploadFileMaintenance uploader = PXGraph.CreateInstance<UploadFileMaintenance>();
                    Guid[] fileIDs = null;
                    Guid? latestFileID = null;

                    // File lookups are raw DB reads - isolate them, same reason as
                    // everywhere else in this handler. GetFileNotes (PXNoteAttribute ->
                    // NoteDoc.SelectMulti) is a BQL query too - it has to be inside this
                    // isolated scope along with the GetFile lookups below, not run ambient
                    // before it. See the identical fix/comment in TryPrintLabel above.
                    WithIsolatedConnection(() =>
                    {
                        fileIDs = PXNoteAttribute.GetFileNotes(Base.Packages.Cache, pkg);
                        if (fileIDs == null || fileIDs.Length == 0) return;

                        foreach (Guid fileID in fileIDs)
                        {
                            PX.SM.FileInfo fileInfo = uploader.GetFile(fileID);
                            if (fileInfo == null) continue;

                            string fileName = fileInfo.Name ?? string.Empty;
                            if (!fileName.StartsWith("Label", StringComparison.OrdinalIgnoreCase)) continue;

                            // Each subsequent match overwrites the previous one,
                            // so after the loop this holds the last (newest) match.
                            latestFileID = fileID;
                        }
                    });

                    if (!latestFileID.HasValue) continue;

                    // Pass the file UID as "FILEID" with null report ID.
                    // DeviceHub fetches the file from Acumatica and sends
                    // the raw bytes to the printer.
                    var parameters = new Dictionary<string, string>
                    {
                        { "FILEID", latestFileID.Value.ToString() }
                    };

                    var printSettings = new PrintSettings
                    {
                        PrinterID             = printerID,
                        NumberOfCopies        = 1,
                        PrintWithDeviceHub    = true,
                        DefinePrinterManually = true
                    };

                    // Not wrapped - matches the PrintNode branch's treatment of submission
                    // calls; see the RowPersisted handler's comment.
                    PXGraph.CreateInstance<SMPrintJobMaint>()
                        .CreatePrintJob(
                            printSettings,
                            (string)null,
                            parameters,
                            string.Format("Carrier Label - {0} pkg {1}", shipment.ShipmentNbr, pkg.LineNbr),
                            CancellationToken.None);

                    anyPrinted = true;
                }

                if (anyPrinted)
                    WithIsolatedConnection(() => MarkPrinted<SOShipmentExt.usrLabelPrinted>(shipment));
            }
            catch (Exception ex)
            {
                PXTrace.WriteError("AutoPrint: Label FAILED for {0}: {1}", shipment.ShipmentNbr, ex.Message);
            }
        }

        /// <summary>
        /// Resolves the user's label printer by looking up the "Carrier Labels"
        /// mailing (NotificationCD = 'Carrier Labels', the row on SO101000 →
        /// Mailing & Printing with Report ID SO.64.50.00).
        ///
        /// Step 1: Find NotificationSetup where NotificationCD = 'Carrier Labels'
        /// Step 2: Find NotificationSetupUserOverride for this user + that SetupID
        ///
        /// This means each shipper only needs ONE row on their Printing Settings
        /// tab (SM203010): Mailing ID = 'Carrier Labels', Default Printer = their
        /// Zebra. No per-Ship-Via rows needed.
        /// </summary>
        private Guid? ResolveUserLabelPrinterDeviceHub(Guid? userID, SOSetupExt setup)
        {
            if (!userID.HasValue) return null;

            return WithIsolatedConnection(() =>
            {
                // Step 1: find the Carrier Labels NotificationSetup record.
                var labelSetup = SelectFrom<NotificationSetup>
                    .Where<NotificationSetup.notificationCD.IsEqual<@P.AsString>>
                    .View.SelectSingleBound(Base, null, "Carrier Labels")
                    .TopFirst;

                if (labelSetup?.SetupID == null) return (Guid?)null;

                // Step 2: find this user's printer override for that mailing.
                var userOverride = SelectFrom<NotificationSetupUserOverride>
                    .Where<NotificationSetupUserOverride.userID.IsEqual<@P.AsGuid>
                        .And<NotificationSetupUserOverride.setupID.IsEqual<@P.AsGuid>>
                        .And<NotificationSetupUserOverride.active.IsEqual<True>>>
                    .View.SelectSingleBound(Base, null, userID.Value, labelSetup.SetupID)
                    .TopFirst;

                return userOverride?.DefaultPrinterID;
            });
        }

        private void TryPrintConfirmationDeviceHub(
            SOShipment shipment, SOShipmentExt shipExt, SOSetupExt setup,
            Guid? userID, Dictionary<string, string> baseParams)
        {
            if (shipExt?.UsrConfirmPrinted == true) return;
            if (string.IsNullOrWhiteSpace(setup.UsrConfirmReportID)) return;

            Guid? printerID = ResolveUserPrinterDeviceHub(userID, setup.UsrConfirmReportID)
                              ?? setup.UsrConfirmPrinterID;

            if (!printerID.HasValue)
            {
                PXTrace.WriteWarning("AutoPrint: No printer for Confirmation on {0}.", shipment.ShipmentNbr);
                return;
            }

            try
            {
                // Not wrapped - matches the PrintNode branch's treatment of submission
                // calls; see the RowPersisted handler's comment.
                QueuePrintJobDeviceHub(setup.UsrConfirmReportID, printerID.Value,
                    baseParams, "Shipment Confirmation - " + shipment.ShipmentNbr);

                WithIsolatedConnection(() => MarkPrinted<SOShipmentExt.usrConfirmPrinted>(shipment));
            }
            catch (Exception ex)
            {
                PXTrace.WriteError("AutoPrint: Confirmation FAILED for {0}: {1}", shipment.ShipmentNbr, ex.Message);
            }
        }

        /// <summary>
        /// Resolves the printer for a specific user + report combination using
        /// the two-table pattern behind User Preferences SM203010 → Printing Settings
        /// → Printers by Report grid:
        ///
        ///   Step 1: Find NotificationSetup where ReportID matches the report we
        ///           are about to print. This gives us the SetupID (Mailing ID).
        ///
        ///   Step 2: Find NotificationSetupUserOverride where UserID = current user
        ///           AND SetupID = the SetupID from step 1. The DefaultPrinterID
        ///           on that row is the user's printer override for this report.
        ///
        /// ReportID on NotificationSetupUserOverride is a PXFormula (Selector on
        /// SetupID → NotificationSetup.ReportID) - it is NOT stored in the DB -
        /// so we must do the join ourselves rather than filtering on it directly.
        ///
        /// Returns null if no user-level override exists for this report.
        /// </summary>
        private Guid? ResolveUserPrinterDeviceHub(Guid? userID, string reportID)
        {
            if (!userID.HasValue || string.IsNullOrWhiteSpace(reportID))
                return null;

            return WithIsolatedConnection(() =>
            {
                // Step 1: find the NotificationSetup record for this report ID.
                var setup = SelectFrom<NotificationSetup>
                    .Where<NotificationSetup.reportID.IsEqual<@P.AsString>>
                    .View.SelectSingleBound(Base, null, reportID)
                    .TopFirst;

                if (setup?.SetupID == null) return (Guid?)null;

                // Step 2: find the user's override for that mailing/setup.
                var userOverride = SelectFrom<NotificationSetupUserOverride>
                    .Where<NotificationSetupUserOverride.userID.IsEqual<@P.AsGuid>
                        .And<NotificationSetupUserOverride.setupID.IsEqual<@P.AsGuid>>>
                    .View.SelectSingleBound(Base, null, userID.Value, setup.SetupID)
                    .TopFirst;

                return userOverride?.DefaultPrinterID;
            });
        }

        private SOSetupExt LoadSetupExt()
        {
            return WithIsolatedConnection(() =>
            {
                var setup = PXSelect<SOSetup>.Select(Base);
                if (setup == null) return null;
                return PXCache<SOSetup>.GetExtension<SOSetupExt>((SOSetup)setup);
            });
        }

        private void QueuePrintJobDeviceHub(
            string reportID, Guid printerID,
            Dictionary<string, string> parameters, string description)
        {
            var settings = new PrintSettings
            {
                PrintWithDeviceHub    = true,
                DefinePrinterManually = true,
                PrinterID             = printerID,
                NumberOfCopies        = 1
            };

            PXGraph.CreateInstance<SMPrintJobMaint>()
                .CreatePrintJob(settings, reportID, parameters, description, CancellationToken.None);
        }

        // =====================================================================
        // Shared helpers (both branches)
        // =====================================================================

        /// <summary>
        /// Reads the UsrGiftNote text field on SOShipment by name through the
        /// cache, rather than a typed DAC field, since that field is declared
        /// and owned by the separate StellarOneEastForkCustomization project.
        /// Returns true if the value is non-blank. Returns false (safe no-op)
        /// if the field doesn't exist on the cache at all - GetValue throws
        /// for an unrecognized field name rather than returning null, so that
        /// case is caught explicitly rather than letting it bubble up and
        /// block shipment confirmation entirely.
        /// </summary>
        private bool HasGiftNoteFlag(SOShipment shipment)
        {
            try
            {
                object value = Base.Document.Cache.GetValue(shipment, "UsrGiftNote");
                return !string.IsNullOrWhiteSpace(value as string);
            }
            catch (Exception ex)
            {
                PXTrace.WriteWarning("AutoPrint: UsrGiftNote field not found on SOShipment cache ({0}). Is StellarOneEastForkCustomization published? Gift Note printing skipped.", ex.Message);
                return false;
            }
        }

        private Dictionary<string, string> BuildBaseParameters(SOShipment shipment)
        {
            var p = new Dictionary<string, string> { { "ShipmentNbr", shipment.ShipmentNbr } };

            var os = WithIsolatedConnection(() => SelectFrom<SOOrderShipment>
                .Where<SOOrderShipment.shipmentNbr.IsEqual<@P.AsString>>
                .View.SelectSingleBound(Base, null, shipment.ShipmentNbr)
                .TopFirst);

            if (os != null)
            {
                p["OrderType"] = os.OrderType;
                p["OrderNbr"] = os.OrderNbr;
            }

            return p;
        }

        private void MarkPrinted<TField>(SOShipment shipment)
            where TField : IBqlField
            => SetPrintedFlag<TField>(shipment, true);

        private void SetPrintedFlag<TField>(SOShipment shipment, bool value)
            where TField : IBqlField
        {
            PXDatabase.Update<SOShipment>(
                new PXDataFieldAssign<TField>(value),
                new PXDataFieldRestrict<SOShipment.shipmentNbr>(shipment.ShipmentNbr));

            // Plain SetValue (not SetValueExt) syncs the in-memory value
            // without marking the row dirty. Dirtying it here would queue it
            // up for a normal cache-driven UPDATE on the graph's next
            // Persist() in this session - which would use whatever tstamp is
            // cached below, racing against the raw write above.
            Base.Document.Cache.SetValue<TField>(shipment, value);

            RefreshShipmentTstamp(shipment);

            // Belt and suspenders on top of the tstamp refresh: explicitly
            // mark the row clean so nothing later in this session tries to
            // re-persist it at all. Persist() only processes dirty rows, so
            // a clean row can't hit a stale-tstamp conflict no matter what
            // RefreshShipmentTstamp actually managed to pull back.
            Base.Document.Cache.SetStatus(shipment, PXEntryStatus.Notchanged);
        }

        /// <summary>
        /// SetPrintedFlag writes directly to the DB via PXDatabase.Update,
        /// bypassing PXCache - and SOShipment carries a [PXDBTimestamp]
        /// rowversion column, so that raw write bumps its tstamp in the DB on
        /// every call. Left alone, the graph's cached copy of the shipment
        /// keeps the pre-write tstamp, so any later action in the same
        /// session that persists this shipment again (e.g. Correct Shipment)
        /// would see a mismatch against the DB and fail with "record has
        /// been changed by another process," even though the only "other
        /// process" was this handler a moment earlier.
        ///
        /// NOTE: a plain SelectFrom/BQL select here is NOT reliable for this -
        /// against the same graph (Base) that already has this exact row
        /// cached, it can hand back the identity-mapped cached object itself
        /// instead of a true re-read, making the refresh a no-op. PXDatabase
        /// bypasses PXCache entirely (raw ADO read), which is what actually
        /// guarantees a fresh value from the DB. SetPrintedFlag's follow-up
        /// SetStatus(Notchanged) call is the real backstop, though - this
        /// keeps the cached tstamp correct for the (rare) case something
        /// re-dirties the row again before the next full reselect.
        /// </summary>
        private void RefreshShipmentTstamp(SOShipment shipment)
        {
            // SOShipment's rowversion property is lowercase "tstamp" (the
            // reverse of this project's own SOStellarPrintNodeSetup.Tstamp
            // convention) - "Tstamp" below is the nested BQL field class,
            // not the property, and using lowercase there fails to compile.
            //
            // The WHERE-condition element has to be PXDataFieldValue<T>, not
            // PXDataFieldRestrict<T> - PXDataFieldRestrict derives from
            // PXDataFieldParam (the family used by PXDatabase.Update's
            // restrict/assign pairs), not PXDataField, so it isn't a legal
            // element of SelectSingle's PXDataField[] params array.
            PXDataRecord rec = PXDatabase.SelectSingle<SOShipment>(
                new PXDataField<SOShipment.Tstamp>(),
                new PXDataFieldValue<SOShipment.shipmentNbr>(shipment.ShipmentNbr));

            // GetTimeStamp (not GetByteArray, which doesn't exist on
            // PXDataRecord) is the rowversion-specific accessor and returns
            // null for a DBNull column, same as the other Get* accessors.
            byte[] freshTstamp = rec?.GetTimeStamp(0);
            if (freshTstamp != null)
                shipment.tstamp = freshTstamp;
        }
    }
}
