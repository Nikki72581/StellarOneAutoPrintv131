// ============================================================================
// StellarOne AutoPrint - Print job audit log + reprint
// ----------------------------------------------------------------------------
// Every attempt to send a document to PrintNode (auto-print on shipment
// confirm, or a manual Reprint from this screen) writes one row here via
// PrintJobLogger.Write - success or failure. Separate screen/graph from
// SOStellarPrintNodeSetupMaint (StellarOneAutoPrint_Setup.cs) since this is a
// log + action, not configuration.
//
// PrintNodeDocumentRenderer holds the render/fetch logic shared between the
// original auto-print path (StellarOneAutoPrint.cs) and Reprint here, so a
// reprint regenerates the document the same way the original attempt did
// (fresh report render / fresh label-file fetch, not a stored copy of the
// original bytes).
// ============================================================================

using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using PX.Data;
using PX.Data.BQL;
using PX.Data.BQL.Fluent;
using PX.SM;
using PX.Objects.SO;
using PX.Reports;
using PX.Reports.Controls;

namespace StellarOneAutoPrint
{
    public static class PrintJobLogStatus
    {
        public const string Success = "SUCCESS";
        public const string Error = "ERROR";

        public class ListAttribute : PXStringListAttribute
        {
            public ListAttribute() : base(
                new[] { Success, Error },
                new[] { "Success", "Error" })
            { }
        }
    }

    [Serializable]
    [PXCacheName("StellarOne PrintNode Job Log")]
    public class SOStellarPrintJobLog : PXBqlTable, IBqlTable
    {
        #region LogID
        public abstract class logID : BqlInt.Field<logID> { }

        [PXDBIdentity(IsKey = true)]
        public virtual int? LogID { get; set; }
        #endregion

        #region ShipmentNbr
        public abstract class shipmentNbr : BqlString.Field<shipmentNbr> { }

        // Length assumed to match SOShipment.ShipmentNbr (15) - not verified
        // by reflection against this build's PX.Objects.dll the way other
        // fields in this project have been. Confirm before relying on it if
        // this build's shipment numbering ever exceeds 15 characters.
        [PXDBString(15, IsUnicode = true)]
        [PXUIField(DisplayName = "Shipment Nbr", Enabled = false)]
        [PXSelector(typeof(Search<SOShipment.shipmentNbr>))]
        public virtual string ShipmentNbr { get; set; }
        #endregion

        #region PackageLineNbr
        public abstract class packageLineNbr : BqlInt.Field<packageLineNbr> { }

        // Set only for CarrierLabel rows (one row per SOPackageDetailEx line) -
        // null for GiftNote/Confirmation, which are one document per shipment.
        [PXDBInt]
        [PXUIField(DisplayName = "Package Line Nbr", Enabled = false)]
        public virtual int? PackageLineNbr { get; set; }
        #endregion

        #region JobType
        public abstract class jobType : BqlString.Field<jobType> { }

        [PXDBString(20, IsFixed = true)]
        [PXUIField(DisplayName = "Job Type", Enabled = false)]
        [PrintJobTypes.List]
        public virtual string JobType { get; set; }
        #endregion

        #region Description
        public abstract class description : BqlString.Field<description> { }

        [PXDBString(100, IsUnicode = true)]
        [PXUIField(DisplayName = "Description", Enabled = false)]
        public virtual string Description { get; set; }
        #endregion

        #region PrinterID
        public abstract class printerID : BqlInt.Field<printerID> { }

        [PXDBInt]
        [PXUIField(DisplayName = "PrintNode Printer ID", Enabled = false)]
        [PrintNodePrinterSelector]
        public virtual int? PrinterID { get; set; }
        #endregion

        #region PrinterName
        public abstract class printerName : BqlString.Field<printerName> { }

        // Snapshot at print time, not a live join - the printer could be
        // renamed/deactivated later and this row should still read the way it
        // did when the job actually went out.
        [PXDBString(100, IsUnicode = true)]
        [PXUIField(DisplayName = "Printer Name", Enabled = false)]
        public virtual string PrinterName { get; set; }
        #endregion

        #region ComputerName
        public abstract class computerName : BqlString.Field<computerName> { }

        [PXDBString(100, IsUnicode = true)]
        [PXUIField(DisplayName = "Computer Name", Enabled = false)]
        public virtual string ComputerName { get; set; }
        #endregion

        #region PrintNodeJobID
        public abstract class printNodeJobID : BqlLong.Field<printNodeJobID> { }

        // long, not int - see PrintNodeApiService.SubmitPrintJob's return type
        // for why (PrintNode job IDs are a single global counter, already past
        // Int32.MaxValue). Null when the attempt failed before PrintNode
        // accepted the job.
        [PXDBLong]
        [PXUIField(DisplayName = "PrintNode Job ID", Enabled = false)]
        public virtual long? PrintNodeJobID { get; set; }
        #endregion

        #region Status
        public abstract class status : BqlString.Field<status> { }

        [PXDBString(10, IsFixed = true)]
        [PXUIField(DisplayName = "Status", Enabled = false)]
        [PrintJobLogStatus.List]
        public virtual string Status { get; set; }
        #endregion

        #region ErrorMessage
        public abstract class errorMessage : BqlString.Field<errorMessage> { }

        [PXDBString(2000, IsUnicode = true)]
        [PXUIField(DisplayName = "Error Message", Enabled = false)]
        public virtual string ErrorMessage { get; set; }
        #endregion

        #region SourceLogID
        public abstract class sourceLogID : BqlInt.Field<sourceLogID> { }

        // Set on the new row a Reprint creates, pointing back at the log row
        // it was reprinted from - lets you trace a chain of reprints back to
        // the original auto-print attempt.
        [PXDBInt]
        [PXUIField(DisplayName = "Reprint Of", Enabled = false)]
        [PXSelector(typeof(Search<SOStellarPrintJobLog.logID>), DescriptionField = typeof(SOStellarPrintJobLog.description))]
        public virtual int? SourceLogID { get; set; }
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

        // DisplayMask lives on PXDBCreatedDateTimeAttribute itself, not on
        // PXUIField (PXUIField has no date/time format property at all -
        // verified by reflecting over this build's PX.Data.dll). Without it the
        // field had no explicit format and the grid rendered date-only; "g" is
        // the general short date+time pattern.
        [PXDBCreatedDateTime(DisplayMask = "g")]
        [PXUIField(DisplayName = "Date Created", Enabled = false)]
        public virtual DateTime? CreatedDateTime { get; set; }
        #endregion

        #region NoteID
        public abstract class noteID : BqlGuid.Field<noteID> { }

        // Backs the rendered PDF attachment PrintJobLogger.Write saves against
        // this row for GiftNote/Confirmation jobs - see PXNoteAttribute.AttachFile
        // there. Standard Acumatica file-attachment plumbing; no DisplayName
        // needed since the attachment is exposed via the Files indicator, not a
        // grid column.
        [PXNote]
        public virtual Guid? NoteID { get; set; }
        #endregion
    }

    /// <summary>
    /// Unbound dialog input for the Reprint smart panel - just the printer to
    /// send to, defaulted from the selected log row's original PrinterID (see
    /// SOStellarPrintJobLogMaint's FieldDefaulting handler) but changeable
    /// before confirming.
    /// </summary>
    [Serializable]
    public class ReprintFilter : PXBqlTable, IBqlTable
    {
        #region PrinterID
        public abstract class printerID : BqlInt.Field<printerID> { }

        [PXInt]
        [PXUIField(DisplayName = "Reprint To Printer")]
        [PrintNodePrinterSelector]
        [PrintNodePrinterActiveRestrictor]
        public virtual int? PrinterID { get; set; }
        #endregion
    }

    /// <summary>
    /// Unbound grid filter - scopes the Log view to a default date range so
    /// the grid's "select all" doesn't silently reach every row in the
    /// table. Without this, Log had no Where predicate at all, so selecting
    /// all matching records (a standard multi-page grid action) meant every
    /// row ever logged - see the 2026-08-20 incident where testing single/
    /// multi-row delete wiped the entire log.
    /// </summary>
    [Serializable]
    public class LogListFilter : PXBqlTable, IBqlTable
    {
        #region DateFrom
        public abstract class dateFrom : BqlDateTime.Field<dateFrom> { }

        [PXDate]
        [PXUIField(DisplayName = "Show Jobs Since")]
        public virtual DateTime? DateFrom { get; set; }
        #endregion
    }

    public class SOStellarPrintJobLogMaint : PXGraph<SOStellarPrintJobLogMaint>
    {
        public PXFilter<LogListFilter> ListFilter;

        public PXSelect<SOStellarPrintJobLog,
            Where<SOStellarPrintJobLog.createdDateTime, GreaterEqual<Optional<LogListFilter.dateFrom>>>,
            OrderBy<Desc<SOStellarPrintJobLog.logID>>> Log;
        public PXSave<SOStellarPrintJobLog> Save;
        public PXCancel<SOStellarPrintJobLog> Cancel;

        // No PXDelete action on purpose: this is an audit trail, not editable
        // data, and the published screen (S1990020.aspx) already sets
        // <Mode AllowDelete="False" /> on the grid to reflect that. A PXDelete
        // action here previously produced a second, unrestricted "Delete" on
        // the main screen toolbar that bypassed the grid's own AllowDelete
        // setting entirely - selecting rows (including "select all") and
        // pressing it deleted them with no relation to that grid restriction,
        // which is how a 2026-08-20 test run of single/multi-row delete wiped
        // the whole log. Do not re-add PXDelete<SOStellarPrintJobLog> without
        // also reconciling it against the grid's AllowDelete setting.

        public PXFilter<ReprintFilter> Filter;

        protected virtual void _(Events.FieldDefaulting<LogListFilter, LogListFilter.dateFrom> e)
        {
            e.NewValue = DateTime.Now.Date.AddDays(-30);
            e.Cancel = true;
        }

        protected virtual void _(Events.FieldDefaulting<ReprintFilter, ReprintFilter.printerID> e)
        {
            e.NewValue = Log.Current?.PrinterID;
            e.Cancel = true;
        }

        public PXAction<SOStellarPrintJobLog> ReprintOK;
        [PXUIField(DisplayName = "OK")]
        [PXButton]
        protected virtual IEnumerable reprintOK(PXAdapter adapter)
        {
            var row = Log.Current;
            if (row?.ShipmentNbr == null || row.JobType == null)
                throw new PXException("Select a log row to reprint.");

            int? printerId = Filter.Current?.PrinterID ?? row.PrinterID;
            if (!printerId.HasValue)
                throw new PXException("Select a printer to reprint to.");

            var setup = SelectFrom<SOStellarPrintNodeSetup>.View.Select(this).TopFirst;
            if (string.IsNullOrEmpty(setup?.PrintNodeApiKey))
                throw new PXException("PrintNode API key is not configured on the StellarOne PrintNode Setup screen.");

            try
            {
                byte[] bytes;
                string contentType = "pdf_base64";
                bool isPdfReport = row.JobType == PrintJobTypes.GiftNote || row.JobType == PrintJobTypes.Confirmation;

                switch (row.JobType)
                {
                    case PrintJobTypes.GiftNote:
                        bytes = PrintNodeDocumentRenderer.RenderReportToPdfBytes(
                            setup.GiftNoteReportID, BuildReportParameters(row.ShipmentNbr), row.Description);
                        break;

                    case PrintJobTypes.Confirmation:
                        bytes = PrintNodeDocumentRenderer.RenderReportToPdfBytes(
                            setup.ConfirmReportID, BuildReportParameters(row.ShipmentNbr), row.Description);
                        break;

                    case PrintJobTypes.CarrierLabel:
                        if (!row.PackageLineNbr.HasValue)
                            throw new PXException("This label log row has no package reference to reprint.");
                        bytes = PrintNodeDocumentRenderer.GetLabelFileBytes(
                            this, row.ShipmentNbr, row.PackageLineNbr.Value, out contentType);
                        if (bytes == null)
                            throw new PXException("Could not find the original label file to reprint - it may have been removed.");
                        break;

                    default:
                        throw new PXException($"Unknown print job type '{row.JobType}'.");
                }

                long jobId = new PrintNodeApiService(setup.PrintNodeApiKey)
                    .SubmitPrintJob(printerId.Value, bytes, row.Description, contentType);

                PrintJobLogger.Write(this, row.ShipmentNbr, row.PackageLineNbr, row.JobType, row.Description,
                    printerId, PrintJobLogStatus.Success, null, jobId, row.LogID,
                    documentBytes: isPdfReport ? bytes : null, documentFileName: row.Description + ".pdf");
            }
            catch (Exception ex)
            {
                PrintJobLogger.Write(this, row.ShipmentNbr, row.PackageLineNbr, row.JobType, row.Description,
                    printerId, PrintJobLogStatus.Error, ex.Message, null, row.LogID);
                throw new PXException("Reprint failed: " + ex.Message);
            }

            return adapter.Get();
        }

        private Dictionary<string, string> BuildReportParameters(string shipmentNbr)
        {
            var p = new Dictionary<string, string> { { "ShipmentNbr", shipmentNbr } };

            var os = SelectFrom<SOOrderShipment>
                .Where<SOOrderShipment.shipmentNbr.IsEqual<@P.AsString>>
                .View.SelectSingleBound(this, null, shipmentNbr)
                .TopFirst;

            if (os != null)
            {
                p["OrderType"] = os.OrderType;
                p["OrderNbr"] = os.OrderNbr;
            }

            return p;
        }
    }

    /// <summary>
    /// DI-injection target for the report engine services - no views/actions of
    /// its own. [InjectDependency] on IReportLoaderService/IReportRenderer is the
    /// real, currently-supported replacement for the pre-2021R2 PXReportTools/
    /// ReportProcessor static-class pattern (that pattern is gone from this
    /// build entirely - confirmed absent via reflection across every DLL in
    /// this instance's Bin folder). This exact pairing of properties, with this
    /// exact attribute, is what PX.SM.AUReportProcess (Acumatica's own scheduled
    /// report-emailing graph) and PX.Data.Reports.ReportViewerMaint use - both
    /// found by searching this build's real PX.Data.dll for existing
    /// [InjectDependency] usage on these interface types, so this is a verified
    /// working pattern, not a guess.
    /// </summary>
    public class ReportPdfRenderGraph : PXGraph<ReportPdfRenderGraph>
    {
        [InjectDependency]
        protected IReportLoaderService ReportLoader { get; set; }

        [InjectDependency]
        protected IReportRenderer ReportRenderer { get; set; }

        public byte[] RenderReportToPdf(string reportID, Dictionary<string, string> parameters)
        {
            if (ReportLoader == null || ReportRenderer == null)
                throw new PXException($"Report engine services were not injected while rendering report {reportID}.");

            using (Report report = ReportLoader.LoadReport(reportID, null))
            {
                if (report == null)
                    throw new PXException($"Report {reportID} could not be loaded.");

                ReportLoader.InitDefaultReportParameters(report, parameters ?? new Dictionary<string, string>());

                string mimeType, extension;
                Encoding encoding;
                byte[] pdfBytes = ReportRenderer.Render("PDF", report, null, out mimeType, out extension, out encoding);

                if (pdfBytes == null || pdfBytes.Length == 0)
                    throw new PXException($"Rendered PDF for report {reportID} was empty.");

                return pdfBytes;
            }
        }
    }

    /// <summary>
    /// Report-render and label-fetch logic shared between the original
    /// auto-print path (StellarOneAutoPrint.cs) and Reprint
    /// (SOStellarPrintJobLogMaint above) - both regenerate the document fresh
    /// from current data rather than resending stored bytes.
    /// </summary>
    public static class PrintNodeDocumentRenderer
    {
        /// <summary>
        /// Renders reportID to PDF bytes via Acumatica's own in-process report
        /// engine (IReportLoaderService + IReportRenderer, DI-injected). Replaces
        /// an earlier attempt through PX.SM.SMPrintJobMaint.CreatePrintJob: that
        /// call always returned false here because (confirmed by decompiling this
        /// build's actual PX.Data.dll) it requires the DeviceHub feature enabled,
        /// a real SMPrinter row, and a live DeviceHub client agent to pick the job
        /// up - it only ever enqueues a job for that agent, it never renders
        /// anything itself. None of that exists in a PrintNode-only setup.
        /// </summary>
        public static byte[] RenderReportToPdfBytes(string reportID, Dictionary<string, string> parameters, string description)
        {
            var helper = PXGraph.CreateInstance<ReportPdfRenderGraph>();
            return helper.RenderReportToPdf(reportID, parameters);
        }

        /// <summary>
        /// Re-fetches the latest "Label"-prefixed file attachment for one
        /// package line - the same lookup StellarOneAutoPrint.cs's
        /// TryPrintLabel does per package while looping every package on a
        /// shipment, but targeted at one already-known package line for a
        /// reprint of just that label.
        /// </summary>
        public static byte[] GetLabelFileBytes(PXGraph graph, string shipmentNbr, int packageLineNbr, out string contentType)
        {
            contentType = null;

            var pkg = SelectFrom<SOPackageDetailEx>
                .Where<SOPackageDetailEx.shipmentNbr.IsEqual<@P.AsString>
                    .And<SOPackageDetailEx.lineNbr.IsEqual<@P.AsInt>>>
                .View.SelectSingleBound(graph, null, shipmentNbr, packageLineNbr)
                .TopFirst;

            if (pkg?.NoteID == null) return null;

            Guid[] fileIDs = PXNoteAttribute.GetFileNotes(graph.Caches[typeof(SOPackageDetailEx)], pkg);
            if (fileIDs == null || fileIDs.Length == 0) return null;

            var uploader = PXGraph.CreateInstance<UploadFileMaintenance>();
            Guid? latestFileID = null;

            foreach (Guid fileID in fileIDs)
            {
                PX.SM.FileInfo fileInfo = uploader.GetFile(fileID);
                if (fileInfo == null) continue;
                if (!(fileInfo.Name ?? string.Empty).StartsWith("Label", StringComparison.OrdinalIgnoreCase)) continue;
                latestFileID = fileID;
            }

            if (!latestFileID.HasValue) return null;

            PX.SM.FileInfo labelFile = uploader.GetFile(latestFileID.Value);
            if (labelFile?.BinData == null || labelFile.BinData.Length == 0) return null;

            contentType = DetectPrintNodeContentType(labelFile.BinData);
            return labelFile.BinData;
        }

        public static string DetectPrintNodeContentType(byte[] data)
        {
            bool isPdf = data != null && data.Length >= 4
                && data[0] == 0x25 && data[1] == 0x50 && data[2] == 0x44 && data[3] == 0x46; // %PDF
            return isPdf ? "pdf_base64" : "raw_base64";
        }
    }

    public static class PrintJobLogger
    {
        /// <summary>
        /// Writes one audit row. Never throws - a failure to log must not
        /// break the actual print flow it's logging, so failures here are
        /// traced instead. Failing to attach documentBytes is likewise
        /// non-fatal to the log row itself, traced separately.
        /// </summary>
        public static void Write(
            PXGraph graph, string shipmentNbr, int? packageLineNbr, string jobType, string description,
            int? printerId, string status, string errorMessage, long? printNodeJobId, int? sourceLogId = null,
            byte[] documentBytes = null, string documentFileName = null)
        {
            try
            {
                var logGraph = PXGraph.CreateInstance<SOStellarPrintJobLogMaint>();

                var row = new SOStellarPrintJobLog
                {
                    ShipmentNbr = shipmentNbr,
                    PackageLineNbr = packageLineNbr,
                    JobType = jobType,
                    Description = description,
                    PrinterID = printerId,
                    Status = status,
                    ErrorMessage = errorMessage,
                    PrintNodeJobID = printNodeJobId,
                    SourceLogID = sourceLogId
                };

                if (printerId.HasValue)
                {
                    var printer = SelectFrom<SOStellarPrintNodePrinter>
                        .Where<SOStellarPrintNodePrinter.printerID.IsEqual<@P.AsInt>>
                        .View.SelectSingleBound(logGraph, null, printerId.Value)
                        .TopFirst;

                    row.PrinterName = printer?.PrinterName;
                    row.ComputerName = printer?.ComputerName;
                }

                row = logGraph.Log.Insert(row);
                logGraph.Save.Press();

                if (documentBytes != null && documentBytes.Length > 0)
                {
                    try
                    {
                        var file = new PX.SM.FileInfo(documentFileName ?? (description + ".pdf"), null, documentBytes);
                        PXNoteAttribute.AttachFile(logGraph.Log.Cache, row, file);
                    }
                    catch (Exception attachEx)
                    {
                        PXTrace.WriteError("PrintJobLogger: failed to attach document for {0}: {1}", shipmentNbr, attachEx.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                PXTrace.WriteError("PrintJobLogger: failed to write log row for {0}: {1}", shipmentNbr, ex.Message);
            }
        }
    }
}
