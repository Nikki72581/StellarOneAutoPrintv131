// ============================================================================
// SOShipment extension - shared "already printed" guard flags
// ----------------------------------------------------------------------------
// Three boolean flags on SOShipment, one per document type, checked by both
// the PrintNode and DeviceHub branches of SOShipmentEntryAutoPrintExt
// (StellarOneAutoPrint.cs) so a shipment is never printed twice for the same
// document regardless of which transport handled it.
//
// Originally declared by StellarOneAutoPrintv13 (the DeviceHub project) as
// its own class; this project carried a local-only mirror of it
// (StellarOneAutoPrint_SharedWithV13.cs) for the period v13 and v131 ran as
// two separate published Customization Projects. Now that v13's logic has
// been folded into this project, this is the one real declaration - do not
// also declare SOShipmentExt in v13 if it is still published anywhere, or
// you will get a duplicate-class compile error the moment both are published
// together. These are the exact same DB columns v13 originally created; no
// new SQL is needed for them.
// ============================================================================

using PX.Data;
using PX.Data.BQL;
using PX.Objects.SO;

namespace StellarOneAutoPrint
{
    public class SOShipmentExt : PXCacheExtension<SOShipment>
    {
        #region UsrGiftNotePrinted
        public abstract class usrGiftNotePrinted : BqlBool.Field<usrGiftNotePrinted> { }

        [PXDBBool]
        [PXDefault(false, PersistingCheck = PXPersistingCheck.Nothing)]
        [PXUIField(DisplayName = "Gift Note Printed", Enabled = false)]
        public bool? UsrGiftNotePrinted { get; set; }
        #endregion

        #region UsrLabelPrinted
        public abstract class usrLabelPrinted : BqlBool.Field<usrLabelPrinted> { }

        [PXDBBool]
        [PXDefault(false, PersistingCheck = PXPersistingCheck.Nothing)]
        [PXUIField(DisplayName = "Label Printed", Enabled = false)]
        public bool? UsrLabelPrinted { get; set; }
        #endregion

        #region UsrConfirmPrinted
        public abstract class usrConfirmPrinted : BqlBool.Field<usrConfirmPrinted> { }

        [PXDBBool]
        [PXDefault(false, PersistingCheck = PXPersistingCheck.Nothing)]
        [PXUIField(DisplayName = "Confirmation Printed", Enabled = false)]
        public bool? UsrConfirmPrinted { get; set; }
        #endregion
    }
}
