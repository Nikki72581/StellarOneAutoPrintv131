// ============================================================================
// StellarOne AutoPrint - PrintNode Transport Layer (v14 addition, rev 4)
// ----------------------------------------------------------------------------
// Adds PrintNode as the delivery mechanism in place of DeviceHub.
//
// Rev 4: SOSetupPrintNodeExt (the PXCacheExtension<SOSetup> that used to hold
// the API key + 3 fallback printer fields) is gone - those fields now live on
// the standalone SOStellarPrintNodeSetup DAC in StellarOneAutoPrint_Setup.cs,
// which backs its own dedicated screen instead of bolting onto SO101000. See
// that file's header for why. Everything else in this file (the user-map DAC,
// the printer sync table, the selector/restrictor, the API client) is
// unchanged and still shared by the new setup DAC.
//
// Rev 3 changes (post code-review):
//   - UsrPrintNodeApiKey now uses [PXRSACryptString] instead of plain
//     [PXDBString], so the key is encrypted at rest. Verified against this
//     build's actual PX.Data.dll via reflection - it's the same attribute the
//     platform itself uses for comparable secrets (PX.SM.Standalone.
//     EMailAccount.Password, CA.CABankFeed.AccessToken, GL.GLConsolSetup.
//     Password). An earlier pass guessed [PXDBEncryptedString] /
//     [PXTextEdit(Password = true)] - neither type exists in this codebase
//     (confirmed by reflecting over PX.Data.dll: zero PXDB*Encrypt*Attribute
//     types, zero TextEdit-named types), so that version would not compile.
//     On-screen masking is not a C# attribute at all - it's the Password="True"
//     markup attribute on the px:PXTextEdit control once this field is placed
//     on a screen via the Customization Project Screen Editor. Still restrict
//     the field via Access Rights by Screen as defense in depth - encryption
//     protects the DB at rest, not who can view/edit it on the UI. (This
//     field itself has since moved to SOStellarPrintNodeSetup - see rev 4
//     above and StellarOneAutoPrint_Setup.cs - but the masking/access-rights
//     guidance still applies wherever it ends up placed.)
//   - ExecuteAndReadBody now also wraps network-level WebExceptions (timeout,
//     DNS failure, connection refused - i.e. no HTTP response at all) in a
//     PXException instead of letting a bare WebException escape with a less
//     useful message.
//
// Rev 2 changes (fixing publish errors from the first pass):
//   - Removed [PXCompanyID] / CompanyID field entirely. Acumatica scopes
//     tables by CompanyID automatically based on the SQL column existing -
//     no dedicated DAC attribute is needed, and PXCompanyIDAttribute is not
//     a real type, which is why it failed to compile.
//   - Replaced HttpClient (System.Net.Http) with HttpWebRequest (System.Net).
//     HttpClient in this specific compile environment threw a "type has been
//     forwarded to a different assembly version" error, which means it needs
//     an explicit assembly reference added to the customization project
//     itself (a project-settings step, not a code fix). HttpWebRequest has
//     been part of the base class library since .NET 1.1 and needs no
//     additional reference in any Acumatica build - trading a slightly more
//     verbose API for zero configuration risk.
//   - Replaced System.Text.Json with System.Web.Script.Serialization
//     (JavaScriptSerializer). System.Text.Json is a relatively recent
//     addition to .NET and may hit the same "needs explicit reference" wall
//     HttpClient did in this environment. JavaScriptSerializer lives in
//     System.Web.Extensions, which is already loaded in any Acumatica
//     (ASP.NET) site, so this avoids repeating the same failure mode.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;
using PX.Data;
using PX.Data.BQL;
using PX.Data.BQL.Fluent;
using PX.Objects.CS;

namespace StellarOneAutoPrint
{
    // =========================================================================
    // Locally-synced mirror of the PrintNode account's printer list. Populated
    // by SOStellarPrintNodeSetupMaint.testPrintNodeConnection (see
    // StellarOneAutoPrint_Setup.cs) - this table exists purely so the printer ID fields
    // above, and SOStellarPrintNodeUserMap.PrinterID below, can have a real
    // PXSelector instead of requiring an admin to type in a raw PrintNode ID
    // from memory. It is a cache, not a source of truth - PrintNode's account
    // is the source of truth, refreshed on demand via the Test Connection
    // button.
    // =========================================================================

    [Serializable]
    [PXCacheName("StellarOne PrintNode Printer")]
    public class SOStellarPrintNodePrinter : PXBqlTable, IBqlTable
    {
        #region PrinterID
        public abstract class printerID : BqlInt.Field<printerID> { }

        [PXDBInt(IsKey = true)]
        [PXUIField(DisplayName = "PrintNode Printer ID", Enabled = false)]
        public virtual int? PrinterID { get; set; }
        #endregion

        #region PrinterName
        public abstract class printerName : BqlString.Field<printerName> { }

        [PXDBString(100, IsUnicode = true)]
        [PXUIField(DisplayName = "Printer Name", Enabled = false)]
        public virtual string PrinterName { get; set; }
        #endregion

        #region ComputerName
        public abstract class computerName : BqlString.Field<computerName> { }

        // PrintNode nests this under the printer's "computer" object
        // (computer.name) - the name of the workstation running the PrintNode
        // client that this printer is attached to. See PrintNodeApiService.
        // GetPrinters() for the parse.
        [PXDBString(100, IsUnicode = true)]
        [PXUIField(DisplayName = "Computer Name", Enabled = false)]
        public virtual string ComputerName { get; set; }
        #endregion

        #region State
        public abstract class state : BqlString.Field<state> { }

        [PXDBString(20, IsUnicode = true)]
        [PXUIField(DisplayName = "State", Enabled = false)]
        public virtual string State { get; set; }
        #endregion

        #region IsActive
        public abstract class isActive : BqlBool.Field<isActive> { }

        // False = no longer returned by GetPrinters() as of the last sync (removed
        // from the PrintNode account, or offline-and-dropped). Rows are never
        // hard-deleted so already-assigned printer IDs keep resolving to a name
        // instead of going blank; PrintNodePrinterActiveRestrictor just blocks
        // NEW selections of an inactive row.
        [PXDBBool]
        [PXDefault(true)]
        [PXUIField(DisplayName = "Active", Enabled = false)]
        public virtual bool? IsActive { get; set; }
        #endregion

        #region LastSyncedDateTime
        public abstract class lastSyncedDateTime : BqlDateTime.Field<lastSyncedDateTime> { }

        [PXDBDateAndTime]
        [PXUIField(DisplayName = "Last Synced", Enabled = false)]
        public virtual DateTime? LastSyncedDateTime { get; set; }
        #endregion
    }

    /// <summary>
    /// Reusable selector for any int field that should point at a PrintNode
    /// printer ID: SOSetupPrintNodeExt's 3 fallback fields, and
    /// SOStellarPrintNodeUserMap.PrinterID. Deliberately does NOT filter to
    /// IsActive here - that's PrintNodePrinterActiveRestrictor's job, kept
    /// separate so a printer ID assigned before it went inactive still
    /// resolves to a name instead of showing blank.
    /// </summary>
    public class PrintNodePrinterSelectorAttribute : PXSelectorAttribute
    {
        public PrintNodePrinterSelectorAttribute() : base(
            typeof(Search<SOStellarPrintNodePrinter.printerID>),
            typeof(SOStellarPrintNodePrinter.printerID),
            typeof(SOStellarPrintNodePrinter.printerName),
            typeof(SOStellarPrintNodePrinter.computerName),
            typeof(SOStellarPrintNodePrinter.state))
        {
            DescriptionField = typeof(SOStellarPrintNodePrinter.printerName);
        }
    }

    /// <summary>
    /// Blocks picking an inactive (no longer synced) printer as a NEW value,
    /// without disturbing a field that already holds one.
    /// </summary>
    public class PrintNodePrinterActiveRestrictorAttribute : PXRestrictorAttribute
    {
        public PrintNodePrinterActiveRestrictorAttribute() : base(
            typeof(Where<SOStellarPrintNodePrinter.isActive, Equal<True>>),
            "Printer '{0}' is no longer visible to this PrintNode API key. Click Test PrintNode Connection to refresh the list.",
            typeof(SOStellarPrintNodePrinter.printerName))
        { }
    }

    // =========================================================================
    // Per-user printer override (replaces NotificationSetupUserOverride role)
    // =========================================================================

    /// <summary>
    /// One row per (UserID, PrintJobType). Mirrors the job the native
    /// "Printers by Report" grid (SM203010) played for DeviceHub, but points
    /// at a PrintNode printer ID instead of an SM206510 Printer record.
    /// No explicit CompanyID field is needed - Acumatica scopes this
    /// automatically off the CompanyID column created by the SQL script.
    /// </summary>
    [Serializable]
    [PXCacheName("StellarOne PrintNode User Printer Map")]
    public class SOStellarPrintNodeUserMap : PXBqlTable, IBqlTable
    {
        #region UserID
        public abstract class userID : BqlGuid.Field<userID> { }

        [PXDBGuid(IsKey = true)]
        [PXDefault(typeof(AccessInfo.userID))]
        [PXUIField(DisplayName = "User")]
        [PXSelector(typeof(Search<PX.SM.Users.pKID>), SubstituteKey = typeof(PX.SM.Users.username))]
        public virtual Guid? UserID { get; set; }
        #endregion

        #region PrintJobType
        public abstract class printJobType : BqlString.Field<printJobType> { }

        [PXDBString(20, IsFixed = true, IsKey = true)]
        [PXDefault]
        [PXUIField(DisplayName = "Print Job Type")]
        [PrintJobTypes.List]
        public virtual string PrintJobType { get; set; }
        #endregion

        #region PrinterID
        public abstract class printerID : BqlInt.Field<printerID> { }

        [PXDBInt]
        [PXUIField(DisplayName = "PrintNode Printer ID")]
        [PrintNodePrinterSelector]
        [PrintNodePrinterActiveRestrictor]
        public virtual int? PrinterID { get; set; }
        #endregion

        #region PrinterName
        public abstract class printerName : BqlString.Field<printerName> { }

        // Denormalized display cache, refreshed on FieldUpdated from the local
        // SOStellarPrintNodePrinter sync table (see SOStellarPrintNodeSetupMaint
        // in StellarOneAutoPrint_Setup.cs) - no live PrintNode API call needed
        // just to render the grid.
        [PXDBString(100, IsUnicode = true)]
        [PXUIField(DisplayName = "Printer Name", Enabled = false)]
        public virtual string PrinterName { get; set; }
        #endregion

        #region ComputerName
        public abstract class computerName : BqlString.Field<computerName> { }

        // Denormalized display cache, refreshed alongside PrinterName - see
        // ApplyPrinterLookup in SOStellarPrintNodeSetupMaint.
        [PXDBString(100, IsUnicode = true)]
        [PXUIField(DisplayName = "Computer Name", Enabled = false)]
        public virtual string ComputerName { get; set; }
        #endregion

        #region IsActive
        public abstract class isActive : BqlBool.Field<isActive> { }

        [PXDBBool]
        [PXDefault(true)]
        [PXUIField(DisplayName = "Active")]
        public virtual bool? IsActive { get; set; }
        #endregion
    }

    public static class PrintJobTypes
    {
        public const string GiftNote = "GIFTNOTE";
        public const string CarrierLabel = "CARRIERLABEL";
        public const string Confirmation = "SHIPCONFIRM";

        public class ListAttribute : PXStringListAttribute
        {
            public ListAttribute() : base(
                new[] { GiftNote, CarrierLabel, Confirmation },
                new[] { "Gift Note", "Carrier Label", "Shipment Confirmation" })
            { }
        }
    }

    /// <summary>
    /// Refreshes a SOStellarPrintNodeUserMap row's denormalized PrinterName/
    /// ComputerName display cache from the local SOStellarPrintNodePrinter sync
    /// table - no live PrintNode API call. Relies on that table having been
    /// populated by Test PrintNode Connection at least once. Shared by the
    /// admin Setup screen (SOStellarPrintNodeSetupMaint, StellarOneAutoPrint_Setup.cs)
    /// and the self-service panel (SOShipmentEntryMyPrinterPrefsExt,
    /// StellarOneAutoPrint_MyPrinterPrefs.cs) - both edit the same DAC's
    /// PrinterID field and need the same lookup-on-change behavior. Graph is
    /// passed in explicitly since SelectFrom<T>.View.SelectSingleBound(...) can
    /// bind to any PXGraph instance ad hoc.
    /// </summary>
    public static class PrintNodeUserMapHelper
    {
        public const string PrinterNotFoundText = "(not found - click Test Connection to sync)";

        public static void ApplyPrinterLookup(PXGraph graph, SOStellarPrintNodeUserMap row)
        {
            if (row == null) return;

            if (row.PrinterID == null)
            {
                row.PrinterName = null;
                row.ComputerName = null;
                return;
            }

            var printer = SelectFrom<SOStellarPrintNodePrinter>
                .Where<SOStellarPrintNodePrinter.printerID.IsEqual<@P.AsInt>>
                .View.SelectSingleBound(graph, null, row.PrinterID.Value)
                .TopFirst;

            row.PrinterName = printer?.PrinterName ?? PrinterNotFoundText;
            row.ComputerName = printer?.ComputerName;
        }
    }

    // =========================================================================
    // PrintNode REST API wrapper (HttpWebRequest + JavaScriptSerializer -
    // no extra assembly references needed in this environment)
    // =========================================================================

    public class PrintNodeApiService
    {
        private readonly string _apiKey;
        private const string BaseUrl = "https://api.printnode.com";

        public PrintNodeApiService(string apiKey)
        {
            if (string.IsNullOrEmpty(apiKey))
                throw new PXException("PrintNode API key is not configured on SO Preferences.");
            _apiKey = apiKey;
        }

        private HttpWebRequest CreateRequest(string url, string method)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = method;
            request.Timeout = 20000;
            var authBytes = Encoding.ASCII.GetBytes($"{_apiKey}:");
            request.Headers["Authorization"] = "Basic " + Convert.ToBase64String(authBytes);
            return request;
        }

        private static string ReadResponseBody(HttpWebResponse response)
        {
            using (var stream = response.GetResponseStream())
            using (var reader = new StreamReader(stream ?? Stream.Null, Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        /// <summary>
        /// Executes the request and returns the response body regardless of
        /// status code, throwing a clear PXException on failure statuses.
        /// HttpWebRequest throws a WebException (not a normal return) for
        /// non-2xx statuses, so both paths are handled here.
        /// </summary>
        private string ExecuteAndReadBody(HttpWebRequest request, string context)
        {
            try
            {
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    return ReadResponseBody(response);
                }
            }
            catch (WebException wex) when (wex.Response is HttpWebResponse errorResponse)
            {
                string body = ReadResponseBody(errorResponse);
                throw new PXException($"{context} failed ({(int)errorResponse.StatusCode}): {body}");
            }
            catch (WebException wex)
            {
                // No HTTP response at all (timeout, DNS failure, connection refused, etc.)
                throw new PXException($"{context} failed: {wex.Message}", wex);
            }
        }

        public List<PrintNodePrinter> GetPrinters()
        {
            var request = CreateRequest($"{BaseUrl}/printers", "GET");
            string json = ExecuteAndReadBody(request, "PrintNode GET /printers");

            var serializer = new JavaScriptSerializer();
            var raw = serializer.Deserialize<List<Dictionary<string, object>>>(json);

            var result = new List<PrintNodePrinter>();
            foreach (var item in raw ?? new List<Dictionary<string, object>>())
            {
                string computerName = null;
                if (item.ContainsKey("computer") && item["computer"] is Dictionary<string, object> computer
                    && computer.ContainsKey("name"))
                {
                    computerName = computer["name"] as string;
                }

                result.Add(new PrintNodePrinter
                {
                    Id = Convert.ToInt32(item["id"]),
                    Name = item.ContainsKey("name") ? item["name"] as string : null,
                    State = item.ContainsKey("state") ? item["state"] as string : null,
                    ComputerName = computerName
                });
            }
            return result;
        }

        /// <summary>
        /// Submits raw bytes (already-rendered PDF, or raw ZPL/label data) to a
        /// PrintNode printer. Returns the PrintNode job ID.
        ///
        /// Job ID is a `long`, not `int`: PrintNode job IDs are a single
        /// auto-increment counter shared across every PrintNode account and
        /// have been past Int32.MaxValue (2,147,483,647) for a while now.
        /// Confirmed on a real sandbox print: `int.Parse` on the returned ID
        /// threw "Value was either too large or too small for an Int32."
        /// </summary>
        public long SubmitPrintJob(int printerId, byte[] content, string title, string contentType = "pdf_base64")
        {
            var payload = new Dictionary<string, object>
            {
                { "printerId", printerId },
                { "title", title },
                { "contentType", contentType },
                { "content", Convert.ToBase64String(content) },
                { "source", "Acumatica - StellarOneAutoPrint" }
            };

            var serializer = new JavaScriptSerializer();
            string json = serializer.Serialize(payload);
            byte[] bodyBytes = Encoding.UTF8.GetBytes(json);

            var request = CreateRequest($"{BaseUrl}/printjobs", "POST");
            request.ContentType = "application/json";
            request.ContentLength = bodyBytes.Length;

            using (var requestStream = request.GetRequestStream())
            {
                requestStream.Write(bodyBytes, 0, bodyBytes.Length);
            }

            string resultJson = ExecuteAndReadBody(request, "PrintNode POST /printjobs");
            return long.Parse(resultJson.Trim());
        }
    }

    public class PrintNodePrinter
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string State { get; set; }
        public string ComputerName { get; set; }
    }
}
