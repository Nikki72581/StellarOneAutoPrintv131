# StellarOneAutoPrintv131

An Acumatica ERP customization project that automatically prints shipping documents (gift note, carrier label, shipment confirmation) the moment a shipment is confirmed in Sales Order Shipments (SO302000), routing the rendered output to a printer instead of a human clicking Print.

This project owns **both** delivery transports the customization has ever used:

- **PrintNode** — the target state. Renders each report to PDF (or forwards raw carrier-label bytes) and submits it to PrintNode over HTTPS. No local client required.
- **DeviceHub** — the legacy path, kept only for users who haven't been migrated to PrintNode yet. Requires a DeviceHub client running on the workstation.

Which transport handles a given shipment is decided **per user**, by the same migration switch described below. Until 2026-08-15 this logic was split across two separately-published Acumatica Customization Projects (`StellarOneAutoPrintv13` for DeviceHub, this project for PrintNode) that had to compile into one merged assembly side by side — that coexistence period is over. v13's logic has been folded into this project so there's exactly one Customization Project to edit, build, and publish. The old v13 local project is kept for reference under [`archive/StellarOneAutoPrintv13`](archive/StellarOneAutoPrintv13) but is no longer the source of truth for anything.

## How the print trigger works

The whole feature lives inside one event handler:

**`SOShipmentEntryAutoPrintExt`** ([StellarOneAutoPrint.cs](StellarOneAutoPrintv131/StellarOneAutoPrint.cs)), a `PXGraphExtension<SOShipmentEntry>` with two hooks on `SOShipment`:

- A **`RowPersisting<SOShipment>`** handler that **handles un-confirm**: if `Confirmed != true` (e.g. via Correct Shipment, which sets the flag by direct cache assignment rather than a normal field update — `RowPersisting` still reliably catches it since, unlike `RowUpdated`, it fires for every dirty row regardless of how it became dirty), it resets the three printed-flags via plain `SetValueExt` so the next confirm reprints everything. This applies regardless of which transport will end up handling the next confirm. Running this *before* the row's UPDATE is generated (rather than after, in `RowPersisted`) lets the reset ride along in that same UPDATE, with the framework handling the optimistic-concurrency `tstamp` natively — no raw SQL involved.
- A **`RowPersisted<SOShipment>`** handler that does the actual printing, which on every shipment save:
  1. **Bails out early** unless `TranStatus == Completed` (so printing only fires once the DB transaction is durable — see "Why `Completed`, not `Open`" below), the operation is an Insert/Update, and `Confirmed == true`.
  2. **Picks a transport per user** — `IsUserMigratedToPrintNode(userID)`: looks up an active row in `SOStellarPrintNodeUserMap` for the current user.
     - **Migrated** → `RunPrintNode`: checks `EnhancedPrintingEnabled` and a configured PrintNode API key on `SOStellarPrintNodeSetup`; bails if either is missing, then attempts the three print jobs via PrintNode.
     - **Not migrated** → `RunDeviceHub`: checks `UsrEnhancedPrintingEnabled` on `SOSetupExt` (legacy settings on SO Preferences); bails if missing, then attempts the three print jobs via DeviceHub — this reproduces v13's original behavior exactly.
  3. Both branches **attempt three independent print jobs**, each idempotent (skipped if its own "already printed" flag is set) and each swallowing its own exception rather than aborting the others:
     - **Gift Note** — renders the configured report, only if the shipment has gift-note text (`UsrGiftNote`, from a separate customization) and a report ID is configured.
     - **Carrier Label** — pulls already-rendered carrier label files off `SOPackageDetailEx` attachments (per package, filtered to filenames starting with `"Label"`). The PrintNode branch sniffs `%PDF` magic bytes to decide `pdf_base64` vs `raw_base64` (ZPL/EPL passthrough) and forwards the bytes directly; the DeviceHub branch passes the file's UID via the `FILEID` parameter pattern and lets DeviceHub fetch it.
     - **Shipment Confirmation** — renders the configured report.
  4. The PrintNode branch **surfaces failures as a non-blocking cache warning** on the shipment (`RaiseExceptionHandling` with `PXErrorLevel.Warning`) in addition to a `PXTrace` entry, and logs every attempt via `PrintJobLogger`. The DeviceHub branch keeps its original, simpler behavior — `PXTrace` only, no `PrintJobLogger` entry — unchanged from v13, since folding the two transports together was a structural merge, not a redesign of the legacy path. Marking a job's printed-flag (`MarkPrinted`/`SetPrintedFlag`) writes directly to the DB (see "Why `TranStatus.Completed`" below for why) and takes care to leave the row clean afterward — see the doc comment on `SetPrintedFlag` in the code for the "record has been changed by another process" bug this avoids.

### Printer resolution order (per job type)

**PrintNode branch:**
1. `SOStellarPrintNodeUserMap` row for `(current user, job type)`, if active.
2. Otherwise, the fallback printer ID field on the StellarOne PrintNode Setup screen (`GiftNotePrinterID` / `LabelPrinterID` / `ConfirmPrinterID`).
3. If neither resolves, the job is skipped and a warning is raised — nothing is silently dropped.

**DeviceHub branch (legacy):**
1. Current user's "Printers by Report" profile (SM203010 / `NotificationSetupUserOverride`) — for labels specifically, the "Carrier Labels" mailing override.
2. Otherwise, the fallback printer configured on SO Preferences (`SOSetupExt` fields).

### Why `TranStatus.Completed`, not `Open`

Printing is an irreversible external side effect (hits DeviceHub/PrintNode and a physical printer). Firing on `Open` risked a scenario where a later rollback in the same `Persist()` call rolled back the printed-flag write while the physical print had already gone out — causing a duplicate print on the next successful confirm. Gating on `Completed` guarantees the flag write is durable first. (v13 originally fired on `Open`; this fix was reviewed and made live for the PrintNode branch first, and now also covers the DeviceHub branch since both live in the same handler.)

## Data model

| Table / Extension | DAC | Purpose |
|---|---|---|
| `SOStellarPrintNodeSetup` | [`SOStellarPrintNodeSetup`](StellarOneAutoPrintv131/StellarOneAutoPrint_Setup.cs) | PrintNode settings — standalone keyless singleton (one implicit row per company). Enable flag, two report IDs, encrypted PrintNode API key, 3 fallback printer IDs. |
| `SOStellarPrintNodePrinter` | [`SOStellarPrintNodePrinter`](StellarOneAutoPrintv131/StellarOneAutoPrint_PrintNode_Transport.cs) | Local cache of the PrintNode account's printer list, refreshed by the **Test PrintNode Connection** button. Rows are soft-deactivated, never deleted. |
| `SOStellarPrintNodeUserMap` | [`SOStellarPrintNodeUserMap`](StellarOneAutoPrintv131/StellarOneAutoPrint_PrintNode_Transport.cs) | Per-`(UserID, PrintJobType)` printer override — **this is the per-user migration switch**: a user is "on PrintNode" the moment they have at least one active row here. |
| `SOSetupExt` (on `SOSetup`) | [`SOSetupExt`](StellarOneAutoPrintv131/StellarOneAutoPrint_DeviceHub.cs) | Legacy DeviceHub settings — 8 fields bolted onto SO Preferences (SO101000): enable flag, two report IDs, three fallback printer GUIDs. Columns already exist live (created by v13's original publish); this project now declares them for real. |
| `SOShipmentExt` (on `SOShipment`) | [`SOShipmentExt`](StellarOneAutoPrintv131/StellarOneAutoPrint_Shipment.cs) | 3 boolean printed-flags (gift note / label / confirmation), shared by both branches. Columns already exist live (created by v13's original publish). |

The 3 PrintNode tables above are created by hand via [`StellarOneAutoPrint_CreateTables.sql`](StellarOneAutoPrintv131/StellarOneAutoPrint_CreateTables.sql) because Acumatica's automatic schema sync didn't create them on publish. The script is idempotent (guarded `IF NOT EXISTS`). `SOSetupExt` and `SOShipmentExt` need **no new SQL** — they're `PXCacheExtension` bolt-ons whose columns already exist from v13's original publish, and Acumatica's normal publish-time schema sync handles cache extensions automatically.

## Setup screen (PrintNode)

[`SOStellarPrintNodeSetupMaint`](StellarOneAutoPrintv131/StellarOneAutoPrint_Setup.cs) is a standalone graph (not bolted onto the native SO Preferences screen SO101000) backing a new screen you create yourself in the Customization Project:

- **General settings view** over `SOStellarPrintNodeSetup` (enable flag, report IDs, API key, fallback printers).
- **`PrintNodeUserMappings`** grid over `SOStellarPrintNodeUserMap` — where an admin migrates one user at a time.
- **`PrintNodePrinters`** read-only grid over the synced printer cache.
- **Test PrintNode Connection** button — calls `PrintNodeApiService.GetPrinters()`, upserts the cache table, deactivates rows no longer returned.
- **Refresh Printer Name** button + a `FieldUpdated` handler on `PrinterID` — keep the denormalized `PrinterName` display column in sync without a live API call on every grid render.

The legacy DeviceHub settings (`SOSetupExt`) live on the native SO Preferences screen (SO101000), same as they always have — no separate screen needed for that side.

## PrintNode transport layer

[`StellarOneAutoPrint_PrintNode_Transport.cs`](StellarOneAutoPrintv131/StellarOneAutoPrint_PrintNode_Transport.cs) — `PrintNodeApiService`:

- Uses `HttpWebRequest` (not `HttpClient`) and `JavaScriptSerializer` (not `System.Text.Json`) deliberately — both modern alternatives hit "type forwarded to a different assembly version" / extra-reference errors in this build environment; the choices here need zero extra assembly references beyond `System.Web.Extensions` (already added in the `.csproj`).
- Auth: HTTP Basic, API key as username, empty password (PrintNode convention).
- `GetPrinters()` — `GET /printers`, parses out `id`/`name`/`state`/`computer.name`.
- `SubmitPrintJob(printerId, bytes, title, contentType)` — `POST /printjobs`, base64-encodes the content, defaults `contentType` to `pdf_base64`. Returns `long` (PrintNode job IDs are a single global auto-increment counter shared across every PrintNode account and have been past `Int32.MaxValue` for a while).
- `ExecuteAndReadBody` unwraps `WebException` in both directions: HTTP error responses (reads the body for a useful message) and network-level failures (timeout/DNS/connection refused, no response at all).

Report rendering (`RenderReportToPdfBytes` in `StellarOneAutoPrint.cs`) goes through `SMPrintJobMaint.CreatePrintJob(..., PrintWithDeviceHub: false, ...)` and then reads the rendered PDF back off the print job's file attachment, retrying `GetFileNotes`/`GetFile` a few times (300ms apart) in case rendering is genuinely asynchronous.

## Migration switch

`IsUserMigratedToPrintNode(userID)` in `StellarOneAutoPrint.cs` is the single decision point for which branch a shipment's confirm routes through: true once an admin has given that user at least one active row in `SOStellarPrintNodeUserMap` (via the PrintNode Setup screen's `PrintNodeUserMappings` grid). This flips the moment the first active mapping row is added — there's no separate "go live" toggle, so only add a user's mappings when you're ready for their next shipment confirm to route through PrintNode instead of DeviceHub. Once every user has been migrated, the DeviceHub branch (`RunDeviceHub` and the `...DeviceHub`-suffixed helper methods in `StellarOneAutoPrint.cs`, plus `StellarOneAutoPrint_DeviceHub.cs`) can be deleted along with `SOSetupExt`.

## Security notes

- `PrintNodeApiKey` uses `[PXRSACryptString]` (encrypted at rest) — the same attribute the platform itself uses for comparable secrets elsewhere (e.g. `CABankFeed.AccessToken`). On-screen masking (`Password="True"`) is a screen-markup concern, not a C# attribute, and still needs to be set once the field is placed via the Screen Editor. Field-level Access Rights by Screen is recommended as defense in depth.
- No `[PXCompanyID]` attribute exists in this codebase/Acumatica version — multi-tenancy is automatic based on the `CompanyID` column existing in the table, confirmed via the SQL script's header notes.

## Repo layout

```
StellarOneAutoPrintv131/
  StellarOneAutoPrint.cs                    Main RowPersisted trigger - both PrintNode and DeviceHub branches
  StellarOneAutoPrint_Shipment.cs           SOShipmentExt - shared printed-flag guard fields
  StellarOneAutoPrint_DeviceHub.cs          SOSetupExt - legacy DeviceHub settings on SO Preferences
  StellarOneAutoPrint_PrintNode_Transport.cs PrintNode DACs (printer cache, user map) + REST API client
  StellarOneAutoPrint_Setup.cs               Standalone PrintNode setup DAC + graph (screen, Test Connection, etc.)
  StellarOneAutoPrint_CreateTables.sql       Idempotent hand-written CREATE TABLE script for the 3 PrintNode tables
  StellarOneAutoPrintv131.csproj             net48 class library, references Acumatica's PX.* DLLs by HintPath
StellarOneAutoPrintv131.sln                  References a local AcumaticaERP1 website project for build/debug
archive/StellarOneAutoPrintv13/              Old v13 project, kept for reference only - not published, not built
```

This project is **not** deployed by building/publishing the `.csproj` directly — Acumatica customizations are packaged and published through the Customization Project Editor inside Acumatica itself. The `.csproj`/`.sln` here exist so the code can be edited and compiled locally (IntelliSense, catching hard compile errors) before being pasted into that editor.

## Deployment checklist (merging v13 into this project, in your Acumatica instance)

This local merge is done, but the two Customization Projects in Acumatica are still separate until you push this through. **Test all of this on Sandbox before Production.**

1. Run [`StellarOneAutoPrint_CreateTables.sql`](StellarOneAutoPrintv131/StellarOneAutoPrint_CreateTables.sql) if the 3 PrintNode tables aren't already present (safe to re-run; skip if this environment already has v131 published).
2. **Unpublish (or otherwise remove `SOSetupExt`/`SOShipmentExt` from) the standalone `StellarOneAutoPrintv13` Customization Project first.** Publishing this project's `SOSetupExt`/`SOShipmentExt` alongside v13's own copies of those same classes is a hard duplicate-class compile error — the same collision the old mirror-file setup used to avoid. Do this before step 3, not after.
3. Paste the updated code from `StellarOneAutoPrint.cs`, `StellarOneAutoPrint_Shipment.cs`, `StellarOneAutoPrint_DeviceHub.cs`, `StellarOneAutoPrint_PrintNode_Transport.cs`, and `StellarOneAutoPrint_Setup.cs` into this project's Customization Project Editor, replacing what's there now.
4. Publish. Verify on Sandbox:
   - A **non-migrated** user's shipment still prints via DeviceHub, same as before.
   - A **migrated** user's shipment still prints via PrintNode, same as before.
   - Correct Shipment still resets the printed-flags correctly for both.
5. Only once verified on Sandbox, repeat steps 2–4 on Production.
6. Once every user is migrated to PrintNode, delete the DeviceHub branch (`RunDeviceHub` + `...DeviceHub` helpers in `StellarOneAutoPrint.cs`) and `StellarOneAutoPrint_DeviceHub.cs`, and retire `SOSetupExt`'s fields from SO Preferences.
