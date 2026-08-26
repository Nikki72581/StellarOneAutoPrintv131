// ============================================================================
// StellarOne Auto-Print on Shipment Confirmation
// ----------------------------------------------------------------------------
// Automatically prints up to three documents via DeviceHub when a shipment
// is confirmed:
//
//   1. Gift Note       - prints if the user typed a message into the
//                        Gift Note UDF (UsrGiftNote) on the shipment.
//                        No "trigger item" is required - whether a Gift
//                        Note prints is driven entirely by whether the
//                        note text is populated, not by line content.
//
//                        IMPORTANT: UsrGiftNote itself (the field, its
//                        column, the manual "Print Gift Note" action, and
//                        the SO646010 report) is owned by the separate
//                        StellarOneEastForkCustomization customization project.
//                        This project does NOT declare or own that field - it
//                        reads it by name through the cache at runtime
//                        (Base.Document.Cache.GetValue), rather than
//                        declaring a typed DAC field for it. Declaring our
//                        own typed field for someone else's column requires
//                        an exact byte-for-byte attribute match to avoid a
//                        publish conflict, creates a hard publish-order
//                        dependency between the two projects, and risks
//                        silently overriding that project's UI configuration
//                        for the field (as [PXUIField(Enabled = false)] did
//                        here previously). The cache lookup avoids all of
//                        that - if the field doesn't exist yet (e.g.
//                        StellarOneEastForkCustomization isn't published),
//                        the read just returns null and this feature no-ops.
//   2. Carrier Label   - prints for every confirmed shipment
//   3. Shipment Conf.  - prints for every confirmed shipment
//
// Printer resolution order for all three features:
//   1. Current user's "Printers by Report" profile (SM203010 / NotificationSetupUserOverride)
//   2. Fallback printer configured on SO Preferences (SO101000)
//
// A master kill switch on SO Preferences disables all three features at once.
// Each feature is independently skipped if its report ID is not configured.
//
// Components:
//   SOSetupExt                  - 8 new fields on SO Preferences
//   SOShipmentExt               - 3 "already printed" guard flags on Shipment
//   SOShipmentEntryAutoPrintExt - main graph extension; all print logic here
//
// Deployment: standard customization project.
// Requires:   DeviceHub feature enabled (CS100000).
//             Printers visible on SM206510 (Printers).
//             Acumatica 2023R2 or later (SMPrintJobMaint.CreatePrintJob).
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
    // =========================================================================
    // 1. SO Preferences extension
    // =========================================================================

    public class SOSetupExt : PXCacheExtension<SOSetup>
    {
        // ----- Master toggle -------------------------------------------------

        #region UsrEnhancedPrintingEnabled
        public abstract class usrEnhancedPrintingEnabled : BqlBool.Field<usrEnhancedPrintingEnabled> { }

        [PXDBBool]
        [PXDefault(false, PersistingCheck = PXPersistingCheck.Nothing)]
        [PXUIField(DisplayName = "Enable Enhanced Auto-Printing")]
        public bool? UsrEnhancedPrintingEnabled { get; set; }
        #endregion

        // ----- Gift Note -------------------------------------------------------
        // NOTE: Gift Note auto-printing no longer depends on a configured
        // "trigger" inventory item. It now fires whenever the shipment's
        // UsrGiftNote UDF contains text. That field is owned by the separate
        // StellarOneEastForkCustomization project, not by this one (see note above).

        #region UsrGiftNoteReportID
        public abstract class usrGiftNoteReportID : BqlString.Field<usrGiftNoteReportID> { }

        [PXDBString(8, IsUnicode = false, InputMask = ">aa######")]
        [PXUIField(DisplayName = "Gift Note Report")]
        [PXSelector(
            typeof(Search<SiteMap.screenID, Where<SiteMap.url, Like<reportUrlPattern>>>),
            typeof(SiteMap.screenID),
            typeof(SiteMap.title),
            DescriptionField = typeof(SiteMap.title))]
        public string UsrGiftNoteReportID { get; set; }
        #endregion

        #region UsrGiftNotePrinterID
        public abstract class usrGiftNotePrinterID : BqlGuid.Field<usrGiftNotePrinterID> { }

        [PXPrinterSelector]
        [PXUIField(DisplayName = "Gift Note Printer (Fallback)")]
        public virtual Guid? UsrGiftNotePrinterID { get; set; }
        #endregion

        // ----- Carrier Label -------------------------------------------------

        #region UsrLabelPrinterID
        public abstract class usrLabelPrinterID : BqlGuid.Field<usrLabelPrinterID> { }

        [PXPrinterSelector]
        [PXUIField(DisplayName = "Label Printer (Fallback)")]
        public virtual Guid? UsrLabelPrinterID { get; set; }
        #endregion

        // ----- Shipment Confirmation -----------------------------------------

        #region UsrConfirmReportID
        public abstract class usrConfirmReportID : BqlString.Field<usrConfirmReportID> { }

        [PXDBString(8, IsUnicode = false, InputMask = ">aa######")]
        [PXUIField(DisplayName = "Confirmation Report")]
        [PXSelector(
            typeof(Search<SiteMap.screenID, Where<SiteMap.url, Like<reportUrlPattern>>>),
            typeof(SiteMap.screenID),
            typeof(SiteMap.title),
            DescriptionField = typeof(SiteMap.title))]
        public string UsrConfirmReportID { get; set; }
        #endregion

        #region UsrConfirmPrinterID
        public abstract class usrConfirmPrinterID : BqlGuid.Field<usrConfirmPrinterID> { }

        [PXPrinterSelector]
        [PXUIField(DisplayName = "Confirmation Printer (Fallback)")]
        public virtual Guid? UsrConfirmPrinterID { get; set; }
        #endregion

        // ----- Shared BQL constant -------------------------------------------

        public class reportUrlPattern : BqlString.Constant<reportUrlPattern>
        {
            public reportUrlPattern() : base("%.rpx%") { }
        }
    }

    // =========================================================================
    // 2. SOShipment extension
    // =========================================================================

    public class SOShipmentExt : PXCacheExtension<SOShipment>
    {
        // ----- Gift Note text (NOT owned by this project) ----------------------
        // UsrGiftNote, its DB column, the manual "Print Gift Note" action, and
        // the SO646010 report are all owned by the separate
        // StellarOneEastForkCustomization project (which also defines a
        // matching SOOrder.UsrGiftNote and syncs it down to the shipment).
        // We don't declare a typed field for it here - the graph extension
        // reads it by name through the cache instead (see HasGiftNoteFlag).
        // That avoids needing an exact byte-for-byte attribute match with the
        // other project's declaration, avoids a hard publish-order
        // dependency, and avoids the risk of this project's UI attributes
        // silently overriding that project's configuration for the field.

        #region UsrGiftNotePrinted
        public abstract class usrGiftNotePrinted : BqlBool.Field<usrGiftNotePrinted> { }

        [PXDBBool]
        [PXDefault(false, PersistingCheck = PXPersistingCheck.Nothing)]
        [PXUIField(DisplayName = "Gift Note Printed", Enabled = false, Visible = false)]
        public bool? UsrGiftNotePrinted { get; set; }
        #endregion

        #region UsrLabelPrinted
        public abstract class usrLabelPrinted : BqlBool.Field<usrLabelPrinted> { }

        [PXDBBool]
        [PXDefault(false, PersistingCheck = PXPersistingCheck.Nothing)]
        [PXUIField(DisplayName = "Label Printed", Enabled = false, Visible = false)]
        public bool? UsrLabelPrinted { get; set; }
        #endregion

        #region UsrConfirmPrinted
        public abstract class usrConfirmPrinted : BqlBool.Field<usrConfirmPrinted> { }

        [PXDBBool]
        [PXDefault(false, PersistingCheck = PXPersistingCheck.Nothing)]
        [PXUIField(DisplayName = "Confirmation Printed", Enabled = false, Visible = false)]
        public bool? UsrConfirmPrinted { get; set; }
        #endregion
    }

    // =========================================================================
    // 3. Main graph extension
    // =========================================================================

    public class SOShipmentEntryAutoPrintExt : PXGraphExtension<SOShipmentEntry>
    {
        public static bool IsActive() => true;

        protected virtual void _(Events.RowPersisted<SOShipment> e)
        {
            if (e.TranStatus != PXTranStatus.Open) return;
            if (e.Operation != PXDBOperation.Insert && e.Operation != PXDBOperation.Update) return;

            var shipment = e.Row;
            if (shipment == null) return;

            if (shipment.Confirmed != true)
            {
                // Correct Shipment persists the shipment with Confirmed = false
                // as part of its own internal Save.Press() (see
                // SOShipmentEntry.CorrectShipment). It sets Confirmed via a
                // direct property assignment inside MarkOpen() and then forces
                // the row dirty with Cache.MarkUpdated() rather than going
                // through the normal SetValueExt path, so RowUpdated never
                // fires for this change - this RowPersisted callback (which
                // fires for every dirty row regardless of how it became dirty)
                // is the only reliable place to catch it. Reset our guard
                // flags here so the next confirmation reprints everything.
                var shipExtOnCorrect = PXCache<SOShipment>.GetExtension<SOShipmentExt>(shipment);
                bool anyFlagSet = shipExtOnCorrect?.UsrGiftNotePrinted == true
                                || shipExtOnCorrect?.UsrLabelPrinted == true
                                || shipExtOnCorrect?.UsrConfirmPrinted == true;

                if (anyFlagSet)
                {
                    PXTrace.WriteInformation(
                        "AutoPrint: Resetting printed flags on {0} (shipment un-confirmed, e.g. Correct Shipment).",
                        shipment.ShipmentNbr);

                    SetPrintedFlag<SOShipmentExt.usrGiftNotePrinted>(shipment, false);
                    SetPrintedFlag<SOShipmentExt.usrLabelPrinted>(shipment, false);
                    SetPrintedFlag<SOShipmentExt.usrConfirmPrinted>(shipment, false);
                }

                return;
            }

            var setup = LoadSetupExt();
            if (setup?.UsrEnhancedPrintingEnabled != true) return;

            Guid? currentUserID = Base.Accessinfo?.UserID;
            var shipExt = PXCache<SOShipment>.GetExtension<SOShipmentExt>(shipment);
            var baseParams = BuildBaseParameters(shipment);

            TryPrintGiftNote(shipment, shipExt, setup, currentUserID, baseParams);
            TryPrintLabel(shipment, shipExt, setup, currentUserID, baseParams);
            TryPrintConfirmation(shipment, shipExt, setup, currentUserID, baseParams);
        }

        // ---------------------------------------------------------------------
        // Gift Note
        // ---------------------------------------------------------------------

        private void TryPrintGiftNote(
            SOShipment shipment, SOShipmentExt shipExt, SOSetupExt setup,
            Guid? userID, Dictionary<string, string> baseParams)
        {
            if (shipExt?.UsrGiftNotePrinted == true) return;
            if (string.IsNullOrWhiteSpace(setup.UsrGiftNoteReportID)) return;
            if (!HasGiftNoteFlag(shipment)) return;

            Guid? printerID = ResolveUserPrinter(userID, setup.UsrGiftNoteReportID)
                              ?? setup.UsrGiftNotePrinterID;

            if (!printerID.HasValue)
            {
                PXTrace.WriteWarning("AutoPrint: No printer for Gift Note on {0}.", shipment.ShipmentNbr);
                return;
            }

            try
            {
                QueuePrintJob(setup.UsrGiftNoteReportID, printerID.Value,
                    baseParams, "Gift Note - " + shipment.ShipmentNbr);

                MarkPrinted<SOShipmentExt.usrGiftNotePrinted>(shipment);
            }
            catch (Exception ex)
            {
                PXTrace.WriteError("AutoPrint: Gift Note FAILED for {0}: {1}", shipment.ShipmentNbr, ex.Message);
            }
        }

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

        // ---------------------------------------------------------------------
        // Carrier Label
        // ---------------------------------------------------------------------

        /// <summary>
        /// Carrier labels are stored as file attachments on SOPackageDetailEx.
        /// We send them to DeviceHub using the FILEID parameter pattern:
        /// pass the file's UID as "FILEID" with a null report ID, and
        /// CreatePrintJob handles delivery to the DeviceHub printer via polling.
        /// This works correctly across multiple DeviceHub instances — each job
        /// is routed to whichever DeviceHub instance owns the resolved printer.
        ///
        /// Credit: Brian Stevens (bstevens.net) for documenting the FILEID pattern.
        /// </summary>
        private void TryPrintLabel(
            SOShipment shipment, SOShipmentExt shipExt, SOSetupExt setup,
            Guid? userID, Dictionary<string, string> baseParams)
        {
            if (shipExt?.UsrLabelPrinted == true) return;

            Guid? printerID = ResolveUserLabelPrinter(userID, setup)
                              ?? setup.UsrLabelPrinterID;

            if (!printerID.HasValue)
            {
                PXTrace.WriteWarning("AutoPrint: No label printer configured for shipment {0}.", shipment.ShipmentNbr);
                return;
            }

            bool anyPrinted = false;

            try
            {
                var packages = SelectFrom<SOPackageDetailEx>
                    .Where<SOPackageDetailEx.shipmentNbr.IsEqual<@P.AsString>>
                    .View.Select(Base, shipment.ShipmentNbr);

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
        private Guid? ResolveUserLabelPrinter(Guid? userID, SOSetupExt setup)
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



        // ---------------------------------------------------------------------
        // Shipment Confirmation
        // ---------------------------------------------------------------------

        private void TryPrintConfirmation(
            SOShipment shipment, SOShipmentExt shipExt, SOSetupExt setup,
            Guid? userID, Dictionary<string, string> baseParams)
        {
            if (shipExt?.UsrConfirmPrinted == true) return;
            if (string.IsNullOrWhiteSpace(setup.UsrConfirmReportID)) return;

            Guid? printerID = ResolveUserPrinter(userID, setup.UsrConfirmReportID)
                              ?? setup.UsrConfirmPrinterID;

            if (!printerID.HasValue)
            {
                PXTrace.WriteWarning("AutoPrint: No printer for Confirmation on {0}.", shipment.ShipmentNbr);
                return;
            }

            try
            {
                QueuePrintJob(setup.UsrConfirmReportID, printerID.Value,
                    baseParams, "Shipment Confirmation - " + shipment.ShipmentNbr);

                MarkPrinted<SOShipmentExt.usrConfirmPrinted>(shipment);
            }
            catch (Exception ex)
            {
                PXTrace.WriteError("AutoPrint: Confirmation FAILED for {0}: {1}", shipment.ShipmentNbr, ex.Message);
            }
        }

        // ---------------------------------------------------------------------
        // Shared helpers
        // ---------------------------------------------------------------------

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
        /// SetupID → NotificationSetup.ReportID) — it is NOT stored in the DB —
        /// so we must do the join ourselves rather than filtering on it directly.
        ///
        /// Returns null if no user-level override exists for this report.
        /// </summary>
        private Guid? ResolveUserPrinter(Guid? userID, string reportID)
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

        private Dictionary<string, string> BuildBaseParameters(SOShipment shipment)
        {
            var p = new Dictionary<string, string>
            {
                { "ShipmentNbr", shipment.ShipmentNbr }
            };

            var os = SelectFrom<SOOrderShipment>
                .Where<SOOrderShipment.shipmentNbr.IsEqual<@P.AsString>>
                .View.SelectSingleBound(Base, null, shipment.ShipmentNbr)
                .TopFirst;

            if (os != null)
            {
                p["OrderType"] = os.OrderType;
                p["OrderNbr"]  = os.OrderNbr;
            }

            return p;
        }

        private void QueuePrintJob(
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

        private void MarkPrinted<TField>(SOShipment shipment)
            where TField : IBqlField
            => SetPrintedFlag<TField>(shipment, true);

        private void SetPrintedFlag<TField>(SOShipment shipment, bool value)
            where TField : IBqlField
        {
            Base.Document.Cache.SetValueExt<TField>(shipment, value);
            PXDatabase.Update<SOShipment>(
                new PXDataFieldAssign<TField>(value),
                new PXDataFieldRestrict<SOShipment.shipmentNbr>(shipment.ShipmentNbr));
        }
    }
}
