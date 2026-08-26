// ============================================================================
// Legacy DeviceHub settings (SO Preferences / SO101000)
// ----------------------------------------------------------------------------
// Ported unchanged from StellarOneAutoPrintv13. Backs the DeviceHub branch of
// SOShipmentEntryAutoPrintExt (StellarOneAutoPrint.cs) - the path still used
// for any user who has not yet been migrated to PrintNode (see
// IsUserMigratedToPrintNode in StellarOneAutoPrint.cs). Once every user is
// migrated, this file and the DeviceHub branch that reads it can be retired
// together.
//
// These 8 fields already exist as real columns on SOSetup in the live
// database - created by v13's original publish - so no new SQL is needed for
// them (contrast with the 3 brand-new PrintNode tables, which do need
// StellarOneAutoPrint_CreateTables.sql).
// ============================================================================

using PX.Data;
using PX.Data.BQL;
using PX.Data.BQL.Fluent;
using PX.SM;
using PX.Objects.SO;

namespace StellarOneAutoPrint
{
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
        // Gift Note auto-printing fires whenever the shipment's UsrGiftNote UDF
        // contains text (no configured "trigger" item required). That field is
        // owned by the separate StellarOneEastForkCustomization project, not by
        // this one - read by name through the cache at runtime rather than a
        // typed DAC field (see HasGiftNoteFlag in StellarOneAutoPrint.cs).

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
        public virtual System.Guid? UsrGiftNotePrinterID { get; set; }
        #endregion

        // ----- Carrier Label -------------------------------------------------

        #region UsrLabelPrinterID
        public abstract class usrLabelPrinterID : BqlGuid.Field<usrLabelPrinterID> { }

        [PXPrinterSelector]
        [PXUIField(DisplayName = "Label Printer (Fallback)")]
        public virtual System.Guid? UsrLabelPrinterID { get; set; }
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
        public virtual System.Guid? UsrConfirmPrinterID { get; set; }
        #endregion

        // ----- Shared BQL constant -------------------------------------------

        public class reportUrlPattern : BqlString.Constant<reportUrlPattern>
        {
            public reportUrlPattern() : base("%.rpx%") { }
        }
    }
}
