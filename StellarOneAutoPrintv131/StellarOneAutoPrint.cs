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
// All DB access in this handler runs on the plain ambient connection -
// deliberately no PXConnectionScope/PXCommandScope anywhere below. An earlier
// revision wrapped most of the raw BQL/PXDatabase calls here in an isolated
// second connection with a 300s command timeout, reasoning that
// PXProcessing<T>.ProcessRecords (which drives Confirm Shipment) keeps an
// open reader on the ambient connection for the whole batch and would
// otherwise collide with these calls. In production that isolation was the
// actual cause of a regression, not a fix for one: this handler runs
// SYNCHRONOUSLY, on the SAME thread as ConfirmShipment's still-open outer
// transaction. A query issued on a genuinely separate connection/session
// then has to wait for locks that transaction holds on this shipment's rows
// - locks that can only be released after THIS call returns. That's a
// self-deadlock, not a transient wait, and no timeout value fixes it, it only
// changes how long the eventual failure takes to surface. This was confirmed
// directly against SOStellarPrintJobLog: with the isolated connection +
// 300s command timeout, Gift Note/Carrier Label/Confirmation jobs for the
// same shipment failed in sequence with "Execution Timeout Expired" at gaps
// of ~300 and ~302 seconds - i.e. each one blocked for exactly the configured
// timeout before giving up, back to back, turning a single Confirm Shipment
// click into a 10+ minute hang that still ended in the same failures. Staying
// on the ambient connection avoids the self-deadlock entirely, because a SQL
// session never blocks on locks it already holds itself - which is exactly
// how this handler worked in the original, proven-working version this
// project was rebuilt from (no connection isolation anywhere in it either).
// The "open reader" collision the isolation was meant to prevent turned out
// to be real, but unrelated to ProcessRecords or connection isolation
// entirely: root-caused 2026-09-13 to an undisposed PXDataRecord returned
// by PXDatabase.SelectSingle in RefreshShipmentTstamp (see that method's
// comment for the full mechanism and fix). Re-adding connection isolation
// here would reintroduce the self-deadlock regression above for no benefit.
//
// Two separate hooks on SOShipment, for two different reasons:
//   - RowPersisting resets the printed-flags when the shipment gets
//     un-confirmed (Correct Shipment). Plain SetValueExt, no raw SQL - this
//     fires before the row's UPDATE is generated, so the reset rides along
//     in that same UPDATE with tstamp handled natively.
//   - RowPersisted(Completed) runs the actual printing once a successful
//     confirm is durable (see above). Because that row's UPDATE has
//     already executed by this point, marking the printed-flags requires a
//     second, raw write (SetPrintedFlag) - which takes care to leave the
//     row clean afterward so it can't collide with a later persist in the
//     same session (e.g. a Correct Shipment click right after). See
//     SetPrintedFlag's doc comment for the "record has been changed by
//     another process" bug this fixed.
//
// RowPersisted runs the print dispatch inline (a PXLongOperation-deferral
// detour was tried and reverted - see RowPersisted's own comment for why),
// but wraps the whole thing in a try/catch that never rethrows. That
// try/catch is load-bearing, not defensive boilerplate: RowPersisted fires
// synchronously inside Confirm Shipment's OWN Save.Press() call (confirmed
// by decompiling PX.Objects.DLL - ConfirmShipmentExtension.ConfirmShipment()
// calls base.Base.Save.Press() at its very end, which is what runs this
// handler), so an unhandled exception here doesn't just fail our printing -
// it propagates up and fails the WHOLE confirm. Confirmed live in
// production: a plain BQL select elsewhere in this call chain threw "There
// is already an open DataReader associated with this Connection which must
// be closed first" (root-caused and fixed 2026-09-13 - see
// RefreshShipmentTstamp's comment), and because that particular call wasn't
// inside any of the
// individual TryPrint*'s own try/catch, it escaped all the way up through
// Save.Press() and left the shipment stuck un-confirmed even though
// ShipPackages had already generated and saved the carrier label
// successfully. Printing must never be able to do that - it's a side effect
// of a successful confirm, not a precondition for one. (A separate,
// unrelated EasyPost/UPS carrier error was chased earlier and briefly
// blamed on this handler running inline; that was a dead end - decompiling
// ConfirmShipmentAction showed ShipPackages, the carrier call, runs BEFORE
// Save.Press() and therefore fully before this handler can ever fire, and
// unpublishing this Customization Project entirely reproduced the identical
// carrier error with zero of our code active. See RowPersisted's own
// comment and project memory for the full history if this needs
// re-deriving.)
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
using System.Linq;
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

            // NOTE: an earlier revision wrapped this call in
            // PXLongOperation.StartOperation on a throwaway graph, aiming to
            // move our PrintNode HTTP calls off the thread Confirm Shipment's
            // own carrier-service call runs on. That turned out to be a
            // no-op, confirmed by decompiling PX.Data.dll: RowPersisted fires
            // from inside ConfirmShipmentAction's own
            // PXLongOperation.StartOperation(this, ...) call, and
            // RuntimeLongOperationManager.StartOperationImpl detects the
            // ambient long-operation context regardless of which graph/key
            // you pass it and runs the delegate via RunSync - i.e. inline,
            // synchronously, right here, same as calling it directly. (The
            // EasyPost/UPS carrier error that motivated that change turned
            // out to be unrelated to this project entirely: decompiling
            // ConfirmShipmentAction showed it calls ShipPackages - the
            // carrier call - BEFORE ConfirmShipment/Save.Press, i.e. fully
            // before RowPersisted can ever fire; independently confirmed
            // live by unpublishing this Customization Project entirely and
            // reproducing the exact same carrier error with zero of our code
            // active.) Removed rather than left in place, since it added a
            // full extra PXGraph.CreateInstance<SOShipmentEntry>() on every
            // single confirm for no actual benefit.
            //
            // try/catch here is not optional: this fires synchronously
            // inside Confirm Shipment's own Save.Press() call (see
            // ConfirmShipmentAction, decompiled from PX.Objects.DLL - it
            // calls base.Base.Save.Press() at the very end of
            // ConfirmShipment(), which is what runs RowPersisted). Printing
            // is a side effect that must never be able to block or fail the
            // confirm itself - confirmed live in production: an unhandled
            // "There is already an open DataReader..." thrown from a plain
            // BQL select in this call chain (outside any of the individual
            // TryPrint*'s own try/catch - e.g. LoadSetup,
            // IsUserMigratedToPrintNode, ResolvePrintNodePrinter) propagated
            // all the way up through Save.Press() and left the shipment
            // stuck un-confirmed even though ShipPackages had already
            // generated and saved the carrier label successfully. Every
            // individual TryPrint* method already catches its own errors and
            // logs them to SOStellarPrintJobLog; this outer catch is the
            // backstop for everything AROUND those calls (setup/printer
            // lookups, the migration check) that isn't covered by an inner
            // try/catch of its own.
            // Retries the whole dispatch (not just one query) up to 3 times
            // on "already an open DataReader" specifically. Root-caused and
            // fixed 2026-09-13 (an undisposed PXDataRecord in
            // RefreshShipmentTstamp, leaking an open reader on the ambient
            // connection on every successful print - see that method's
            // comment) - this collision is not expected to recur, so this
            // retry is now a defensive backstop rather than a workaround for
            // a live issue. Left in place since it's cheap and safe: the
            // entire dispatch is idempotent end to end (every job type is
            // guarded by both its own printed-flag AND
            // PrintJobLogger.AlreadySucceeded), so re-running from the top
            // after a partial failure just skips whatever already went out
            // and picks up wherever it left off - it can't cause a duplicate
            // print.
            const int maxAttempts = 3;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    if (IsUserMigratedToPrintNode(currentUserID))
                        RunPrintNode(shipment, currentUserID);
                    else
                        RunDeviceHub(shipment, currentUserID);
                    break;
                }
                catch (InvalidOperationException ex) when (attempt < maxAttempts
                    && ex.Message.IndexOf("open DataReader", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    PXTrace.WriteWarning("AutoPrint: open-reader collision for {0}, retrying (attempt {1}/{2}).",
                        shipment.ShipmentNbr, attempt, maxAttempts);
                    System.Threading.Thread.Sleep(75 * attempt);
                }
                catch (Exception ex)
                {
                    PXTrace.WriteError("AutoPrint: unhandled failure for {0}, printing skipped: {1}", shipment.ShipmentNbr, ex.Message);
                    break;
                }
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
            // Secondary guard on top of the flag above - see AlreadySucceeded's
            // doc comment for why the flag alone isn't sufficient here.
            if (PrintJobLogger.AlreadySucceeded(Base, shipment.ShipmentNbr, PrintJobTypes.GiftNote, null)) return null;

            int? printerId = ResolvePrintNodePrinter(userID, PrintJobTypes.GiftNote) ?? setup.GiftNotePrinterID;
            string description = "Gift Note - " + shipment.ShipmentNbr;

            if (!printerId.HasValue)
            {
                string msg = "No PrintNode printer configured for Gift Note.";
                PXTrace.WriteWarning("AutoPrint: {0} ({1})", msg, shipment.ShipmentNbr);
                PrintJobLogger.Write(Base, shipment.ShipmentNbr, null, PrintJobTypes.GiftNote, description,
                    null, PrintJobLogStatus.Error, msg, null);
                return msg;
            }

            try
            {
                byte[] pdfBytes = PrintNodeDocumentRenderer.RenderReportToPdfBytes(setup.GiftNoteReportID, baseParams, description);

                long jobId = new PrintNodeApiService(setup.PrintNodeApiKey)
                    .SubmitPrintJob(printerId.Value, pdfBytes, description);

                MarkPrinted<SOShipmentExt.usrGiftNotePrinted>(shipment);
                PrintJobLogger.Write(Base, shipment.ShipmentNbr, null, PrintJobTypes.GiftNote, description,
                    printerId, PrintJobLogStatus.Success, null, jobId,
                    documentBytes: pdfBytes, documentFileName: description + ".pdf");
                return null;
            }
            catch (Exception ex)
            {
                PXTrace.WriteError("AutoPrint: Gift Note FAILED for {0}: {1}", shipment.ShipmentNbr, ex.Message);
                PrintJobLogger.Write(Base, shipment.ShipmentNbr, null, PrintJobTypes.GiftNote, description,
                    printerId, PrintJobLogStatus.Error, ex.Message, null);
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

            int? printerId = ResolvePrintNodePrinter(userID, PrintJobTypes.CarrierLabel) ?? setup.LabelPrinterID;

            if (!printerId.HasValue)
            {
                string msg = "No PrintNode label printer configured.";
                PXTrace.WriteWarning("AutoPrint: {0} (shipment {1})", msg, shipment.ShipmentNbr);
                PrintJobLogger.Write(Base, shipment.ShipmentNbr, null, PrintJobTypes.CarrierLabel,
                    "Carrier Label - " + shipment.ShipmentNbr, null, PrintJobLogStatus.Error, msg, null);
                return msg;
            }

            bool anyPrinted = false;
            var service = new PrintNodeApiService(setup.PrintNodeApiKey);

            try
            {
                // Materialized with ToList() before the loop starts, not
                // iterated lazily, since the loop body issues its own nested
                // queries per package on this same ambient connection (no
                // MultipleActiveResultSets). Defensive practice, not a fix
                // for a specific incident - the "There is already an open
                // DataReader" collisions once suspected here turned out to
                // have an unrelated, confirmed cause: an undisposed
                // PXDataRecord in RefreshShipmentTstamp, fixed 2026-09-13.
                var packages = SelectFrom<SOPackageDetailEx>
                    .Where<SOPackageDetailEx.shipmentNbr.IsEqual<@P.AsString>>
                    .View.Select(Base, shipment.ShipmentNbr)
                    .RowCast<SOPackageDetailEx>()
                    .ToList();

                foreach (SOPackageDetailEx pkg in packages)
                {
                    if (pkg.NoteID == null) continue;
                    // Secondary guard, per package - see AlreadySucceeded's doc
                    // comment for why the shipment-level flag alone isn't
                    // sufficient here (this is the exact case that produced 4
                    // duplicate PrintNode job IDs for one label in production).
                    if (PrintJobLogger.AlreadySucceeded(Base, shipment.ShipmentNbr, PrintJobTypes.CarrierLabel, pkg.LineNbr)) continue;

                    Guid[] fileIDs = PXNoteAttribute.GetFileNotes(Base.Packages.Cache, pkg);
                    if (fileIDs == null || fileIDs.Length == 0) continue;

                    UploadFileMaintenance uploader = PXGraph.CreateInstance<UploadFileMaintenance>();

                    // Send EVERY attachment matching "Label"*, not just the last
                    // one in attachment order. A package can legitimately carry
                    // more than one - confirmed live 2026-09-13 on an
                    // international shipment, where the carrier integration
                    // attached both the raw ZPL carrier label AND a separate
                    // customs/commercial-invoice PDF, both named with a "Label"
                    // prefix ("Label #....zpl" and "Label #...-#1 Customs.pdf").
                    // Keeping only the last match silently dropped the actual
                    // shipping label and sent only the customs PDF - logged as
                    // one clean SUCCESS row, so nothing looked wrong in
                    // SOStellarPrintJobLog even though the real label never
                    // printed and had to be done manually.
                    foreach (Guid fileID in fileIDs)
                    {
                        PX.SM.FileInfo fileInfo = uploader.GetFile(fileID);
                        if (fileInfo == null) continue;

                        string fileName = fileInfo.Name ?? string.Empty;
                        if (!fileName.StartsWith("Label", StringComparison.OrdinalIgnoreCase)) continue;

                        if (fileInfo.BinData == null || fileInfo.BinData.Length == 0)
                        {
                            PXTrace.WriteWarning("AutoPrint: Label file {0} had no data for shipment {1} pkg {2}.",
                                fileName, shipment.ShipmentNbr, pkg.LineNbr);
                            continue;
                        }

                        // Label format is carrier-dependent and can vary file to
                        // file within the same package (e.g. a raw ZPL label
                        // alongside a rendered PDF customs form), so the content
                        // type has to be detected per file rather than assumed
                        // for the whole shipment.
                        string contentType = PrintNodeDocumentRenderer.DetectPrintNodeContentType(fileInfo.BinData);
                        string description = string.Format("Carrier Label - {0} pkg {1} ({2})", shipment.ShipmentNbr, pkg.LineNbr, fileName);
                        PXTrace.WriteInformation("AutoPrint: Label file {0} for {1} pkg {2} detected as {3}.",
                            fileName, shipment.ShipmentNbr, pkg.LineNbr, contentType);
                        long jobId = service.SubmitPrintJob(printerId.Value, fileInfo.BinData, description, contentType,
                            setup.CarrierLabelPdfRotation, setup.CarrierLabelFitToPage);

                        PrintJobLogger.Write(Base, shipment.ShipmentNbr, pkg.LineNbr, PrintJobTypes.CarrierLabel, description,
                            printerId, PrintJobLogStatus.Success, null, jobId);

                        anyPrinted = true;
                    }
                }

                if (anyPrinted)
                    MarkPrinted<SOShipmentExt.usrLabelPrinted>(shipment);

                return null;
            }
            catch (Exception ex)
            {
                PXTrace.WriteError("AutoPrint: Label FAILED for {0}: {1}", shipment.ShipmentNbr, ex.Message);
                PrintJobLogger.Write(Base, shipment.ShipmentNbr, null, PrintJobTypes.CarrierLabel,
                    "Carrier Label - " + shipment.ShipmentNbr, printerId, PrintJobLogStatus.Error, ex.Message, null);
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
            // Secondary guard on top of the flag above - see AlreadySucceeded's
            // doc comment for why the flag alone isn't sufficient here.
            if (PrintJobLogger.AlreadySucceeded(Base, shipment.ShipmentNbr, PrintJobTypes.Confirmation, null)) return null;

            int? printerId = ResolvePrintNodePrinter(userID, PrintJobTypes.Confirmation) ?? setup.ConfirmPrinterID;
            string description = "Shipment Confirmation - " + shipment.ShipmentNbr;

            if (!printerId.HasValue)
            {
                string msg = "No PrintNode printer configured for Confirmation.";
                PXTrace.WriteWarning("AutoPrint: {0} ({1})", msg, shipment.ShipmentNbr);
                PrintJobLogger.Write(Base, shipment.ShipmentNbr, null, PrintJobTypes.Confirmation, description,
                    null, PrintJobLogStatus.Error, msg, null);
                return msg;
            }

            try
            {
                byte[] pdfBytes = PrintNodeDocumentRenderer.RenderReportToPdfBytes(setup.ConfirmReportID, baseParams, description);

                long jobId = new PrintNodeApiService(setup.PrintNodeApiKey)
                    .SubmitPrintJob(printerId.Value, pdfBytes, description);

                MarkPrinted<SOShipmentExt.usrConfirmPrinted>(shipment);
                PrintJobLogger.Write(Base, shipment.ShipmentNbr, null, PrintJobTypes.Confirmation, description,
                    printerId, PrintJobLogStatus.Success, null, jobId,
                    documentBytes: pdfBytes, documentFileName: description + ".pdf");
                return null;
            }
            catch (Exception ex)
            {
                PXTrace.WriteError("AutoPrint: Confirmation FAILED for {0}: {1}", shipment.ShipmentNbr, ex.Message);
                PrintJobLogger.Write(Base, shipment.ShipmentNbr, null, PrintJobTypes.Confirmation, description,
                    printerId, PrintJobLogStatus.Error, ex.Message, null);
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

            var mapping = SelectFrom<SOStellarPrintNodeUserMap>
                .Where<SOStellarPrintNodeUserMap.userID.IsEqual<@P.AsGuid>
                    .And<SOStellarPrintNodeUserMap.isActive.IsEqual<True>>>
                .View.SelectSingleBound(Base, null, userID.Value)
                .TopFirst;

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
        /// stock SOSetup singleton itself is selected. Uses SelectSingleBound
        /// for consistency with the other single-row lookups in this file
        /// (IsUserMigratedToPrintNode, ResolvePrintNodePrinter,
        /// BuildBaseParameters) - not because it affects reader lifetime.
        /// Decompiling PX.Data.dll confirmed SelectSingleBound and
        /// View.Select(...).TopFirst both call the same internal Select(...),
        /// which fully materializes into a List&lt;object&gt; before returning
        /// either way, so query shape was never actually relevant to the
        /// open-reader investigation this comment used to describe. The real
        /// cause was an undisposed PXDataRecord in RefreshShipmentTstamp -
        /// see that method's comment for the confirmed root cause and fix.
        /// </summary>
        private SOStellarPrintNodeSetup LoadSetup()
        {
            return SelectFrom<SOStellarPrintNodeSetup>.View.SelectSingleBound(Base, null).TopFirst;
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
                QueuePrintJobDeviceHub(setup.UsrGiftNoteReportID, printerID.Value,
                    baseParams, "Gift Note - " + shipment.ShipmentNbr);

                MarkPrinted<SOShipmentExt.usrGiftNotePrinted>(shipment);
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
                // See the identical comment in TryPrintLabel (PrintNode
                // branch) - ToList() here for the same defensive reason
                // (this SELECT's per-package nested queries share the same
                // ambient connection, no MultipleActiveResultSets).
                var packages = SelectFrom<SOPackageDetailEx>
                    .Where<SOPackageDetailEx.shipmentNbr.IsEqual<@P.AsString>>
                    .View.Select(Base, shipment.ShipmentNbr)
                    .RowCast<SOPackageDetailEx>()
                    .ToList();

                foreach (SOPackageDetailEx pkg in packages)
                {
                    if (pkg.NoteID == null) continue;

                    Guid[] fileIDs = PXNoteAttribute.GetFileNotes(Base.Packages.Cache, pkg);
                    if (fileIDs == null || fileIDs.Length == 0) continue;

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
                    Guid? latestFileID = null;

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
                    MarkPrinted<SOShipmentExt.usrLabelPrinted>(shipment);
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

            // Step 1: find the Carrier Labels NotificationSetup record.
            var labelSetup = SelectFrom<NotificationSetup>
                .Where<NotificationSetup.notificationCD.IsEqual<@P.AsString>>
                .View.SelectSingleBound(Base, null, "Carrier Labels")
                .TopFirst;

            if (labelSetup?.SetupID == null) return null;

            // Step 2: find this user's printer override for that mailing.
            var userOverride = SelectFrom<NotificationSetupUserOverride>
                .Where<NotificationSetupUserOverride.userID.IsEqual<@P.AsGuid>
                    .And<NotificationSetupUserOverride.setupID.IsEqual<@P.AsGuid>>
                    .And<NotificationSetupUserOverride.active.IsEqual<True>>>
                .View.SelectSingleBound(Base, null, userID.Value, labelSetup.SetupID)
                .TopFirst;

            return userOverride?.DefaultPrinterID;
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
                QueuePrintJobDeviceHub(setup.UsrConfirmReportID, printerID.Value,
                    baseParams, "Shipment Confirmation - " + shipment.ShipmentNbr);

                MarkPrinted<SOShipmentExt.usrConfirmPrinted>(shipment);
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

            // Step 1: find the NotificationSetup record for this report ID.
            var setup = SelectFrom<NotificationSetup>
                .Where<NotificationSetup.reportID.IsEqual<@P.AsString>>
                .View.SelectSingleBound(Base, null, reportID)
                .TopFirst;

            if (setup?.SetupID == null) return null;

            // Step 2: find the user's override for that mailing/setup.
            var userOverride = SelectFrom<NotificationSetupUserOverride>
                .Where<NotificationSetupUserOverride.userID.IsEqual<@P.AsGuid>
                    .And<NotificationSetupUserOverride.setupID.IsEqual<@P.AsGuid>>>
                .View.SelectSingleBound(Base, null, userID.Value, setup.SetupID)
                .TopFirst;

            return userOverride?.DefaultPrinterID;
        }

        private SOSetupExt LoadSetupExt()
        {
            var setup = PXSelect<SOSetup>.Select(Base);
            if (setup == null) return null;
            return PXCache<SOSetup>.GetExtension<SOSetupExt>((SOSetup)setup);
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

            var os = SelectFrom<SOOrderShipment>
                .Where<SOOrderShipment.shipmentNbr.IsEqual<@P.AsString>>
                .View.SelectSingleBound(Base, null, shipment.ShipmentNbr)
                .TopFirst;

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
        /// guarantees a fresh value from the DB - and it runs on the plain
        /// ambient connection like everything else in this handler, so it
        /// can't self-deadlock against the still-open outer transaction the
        /// way an isolated second connection would. SetPrintedFlag's
        /// follow-up SetStatus(Notchanged) call is the real backstop, though
        /// - this keeps the cached tstamp correct for the (rare) case
        /// something re-dirties the row again before the next full reselect.
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
            //
            // PXDataRecord wraps a live IDataReader/IDbCommand directly (unlike
            // SelectFrom<T>.View.Select/SelectSingleBound, which materialize
            // into a disconnected List<object> before returning) - confirmed
            // by decompiling PX.Data.dll: PXDataRecord : IDisposable, and
            // PXDatabase's own internal call sites wrap it in a using block.
            // This one didn't, which leaked an open reader on the ambient
            // connection on every single successful MarkPrinted<T> call -
            // confirmed live as the actual root cause of the "There is
            // already an open DataReader" collisions chased throughout this
            // whole investigation (see StellarOneAutoPrint_PrintJobLog.cs's
            // PrintJobLogger.Write for the ruled-out alternative theory).
            using (PXDataRecord rec = PXDatabase.SelectSingle<SOShipment>(
                new PXDataField<SOShipment.Tstamp>(),
                new PXDataFieldValue<SOShipment.shipmentNbr>(shipment.ShipmentNbr)))
            {
                // GetTimeStamp (not GetByteArray, which doesn't exist on
                // PXDataRecord) is the rowversion-specific accessor and
                // returns null for a DBNull column, same as the other Get*
                // accessors.
                byte[] freshTstamp = rec?.GetTimeStamp(0);
                if (freshTstamp != null)
                    shipment.tstamp = freshTstamp;
            }
        }
    }
}
