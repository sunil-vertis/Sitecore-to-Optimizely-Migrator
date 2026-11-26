# Sitecore → Optimizely Migrator — Implementation Guide

Detailed implementation documentation for **Migrator.App** (`SitecoreToOptiMigrator.exe`) and the libraries it orchestrates. Use this when extending mapping, debugging a failed run, or onboarding engineers to the tool.

---

## 1. Purpose

The migrator is a **Windows WPF (.NET 8)** desktop app that:

1. Reads a Sitecore content path (single item or subtree) via Sitecore Services Client (Item Service) or Layout Service.
2. Applies a strict **1:1 JSON mapping** of Sitecore templates → Optimizely CMS SaaS content types and fields.
3. Creates or updates Optimizely content under a chosen **container key**, rewriting media and content references via a persistent **ID map**.

It does **not** publish content, migrate Sitecore layout/personalization, or invent Opti types. Mapping must already match content types defined in the Opti frontend / CMS.

---

## 2. Solution architecture

```
SitecoreToOptiMigrator.sln
├── Migrator.App          ← WPF UI, DI wiring of clients, run orchestration
├── Migrator.Core         ← Engine, mapping, transformers, ID map, models
├── Migrator.Sitecore     ← Sitecore HTTP clients (Item Service / Layout Service)
├── Migrator.Optimizely   ← Opti OAuth + Content/Media REST API
└── Config/
    ├── mappings/content-types.json
    ├── sitecore.secrets.json      (gitignored; sample provided)
    └── optimizely.secrets.json    (gitignored; sample provided)
```

### Project responsibilities

| Project | Responsibility |
|---------|----------------|
| **Migrator.App** | `MainWindow` UI; loads secrets/mapping; builds `HttpClient` + clients; runs `MigrationEngine`; shows progress, summary, CSV export; Serilog startup |
| **Migrator.Core** | `MigrationEngine`, `MappingLoader` / `MappingValidator` / `MappingIndex`, `FieldTransformerRegistry`, `IdMapStore`, shared DTOs and abstractions |
| **Migrator.Sitecore** | `SitecoreItemServiceClient`, `SitecoreLayoutServiceClient`, `SitecoreHttpFactory`, `SitecoreSscLogin` |
| **Migrator.Optimizely** | `OptimizelyCmsClient` (token, create, update versions, media upload) |

### Dependency direction

```
Migrator.App
    → Migrator.Core
    → Migrator.Sitecore  → Migrator.Core
    → Migrator.Optimizely → Migrator.Core
```

Abstractions live in Core (`ISitecoreClient`, `IOptimizelyClient`). App never talks to Sitecore/Opti REST shapes directly except through those clients.

### Runtime package stack (App)

- `Microsoft.Extensions.DependencyInjection` / `Http` (available; App currently constructs clients manually)
- `Serilog.Sinks.Console` + `Serilog.Sinks.File`
- Transitively: `Microsoft.Data.Sqlite` (ID map in Core)

Target: `net8.0-windows`, `UseWPF=true`, assembly name `SitecoreToOptiMigrator`.

---

## 3. Configuration and secrets

### 3.1 Files copied next to the EXE

`Migrator.App.csproj` links and copies:

| Source | Output |
|--------|--------|
| `Config/mappings/**` | `Config/mappings/` |
| `Config/sitecore.secrets.json` (if present) | `Config/` |
| `Config/optimizely.secrets.json` (if present) | `Config/` |
| Sample secrets files | Always copied |

### 3.2 Sitecore credentials

**File:** `Config/sitecore.secrets.json`

```json
{
  "username": "sitecore\\admin",
  "password": "…"
}
```

Loaded by `SitecoreCredentialsLoader.Load(appRoot)`, which walks up to 8 parent directories from `AppContext.BaseDirectory` looking for `Config/sitecore.secrets.json`. Username/password are **not** typed in the UI; the UI only shows the resolved path.

### 3.3 Optimizely credentials

**File:** `Config/optimizely.secrets.json`

```json
{
  "clientId": "…",
  "clientSecret": "…"
}
```

Loaded by `OptimizelyCredentialsLoader` the same way. UI shows a truncated Client ID and source path. Client ID/Secret are never bound to text boxes.

### 3.4 Content-type mapping

**Primary file:** `Config/mappings/content-types.json`

Shape (`MappingConfig`):

```json
{
  "version": 1,
  "defaults": {
    "locale": "en",
    "skipUnmappedTemplates": true,
    "ignoreFields": [ "__Created", "__Updated", "…" ]
  },
  "types": [
    {
      "sitecoreTemplateId": "{GUID}",
      "sitecoreTemplateName": "mvp_SitecoreProduct",
      "optimizelyContentType": "SitecoreProduct",
      "createAs": "component",
      "ignoreFields": [ "…" ],
      "fields": [
        { "sitecore": "Snapshot", "optimizely": "snapshot", "kind": "richText" }
      ]
    }
  ]
}
```

- **`createAs`:** `page` | `component` | `folder` | `media`
- **`kind`:** must be a registered transformer (see §7)
- Prefer **identical** Sitecore field names and Opti property keys when the frontend content type matches Sitecore 1:1

Inherited Sitecore template fields are flattened onto the concrete item at migrate time; map those fields on the **derived** type entry, not as a separate base-template type (unless you also migrate items of that base).

---

## 4. Migrator.App — UI and control flow

### 4.1 Startup (`App.xaml.cs`)

On startup:

1. Creates `%LocalAppData%\SitecoreToOptiMigrator\logs\`
2. Configures Serilog (Debug+, console + rolling daily file, 14 days retained)
3. Logs “migrator starting”

On exit: flush Serilog.

### 4.2 Main window layout (`MainWindow.xaml`)

Left column:

- **Sitecore connection** — Base URL, API key, transport (Item Service / Layout Service), auth mode, database, language, ignore SSL, Test
- **Optimizely CMS SaaS** — API base, credentials status, locale, OAuth scope, Test
- **Paths** — Sitecore source path, Opti container key, optional path label, **Single item only** checkbox
- **Mapping** — path, Browse, Open, Validate
- **Run** — Dry run (default on), Continue on error, Start, Cancel, Export CSV

Right column: live log + results grid.

### 4.3 Client construction

`CreateSitecoreClient`:

1. Builds `HttpClient` via `SitecoreHttpFactory.Create` (5‑minute timeout, API key **query only** — not header — to avoid HTTP 400 on CM).
2. Selects `SitecoreItemServiceClient` or `SitecoreLayoutServiceClient` from transport combo.

Optimizely: plain `HttpClient` + `OptimizelyCmsClient(settings)`.

### 4.4 Migration button flow

1. Load mapping JSON → validate structure.
2. If dry run off → confirm dialog (writes to SaaS).
3. Create CTS, clear results, wire `MigrationEngine.Progress` to UI dispatcher (log, progress bar, grid rows).
4. ID map path: `%LocalAppData%\SitecoreToOptiMigrator\idmap.db`
5. `await engine.RunAsync(options, token)`
6. Show summary dialog + allow CSV export of `_lastRun.Items`

Cancel calls `_cts.Cancel()`.

### 4.5 Connection tests

- **Sitecore:** `ISitecoreClient.TestConnectionAsync` — session login if needed, then probes `/sitecore/content`, `/sitecore`, `/sitecore/content/Home`.
- **Optimizely:** `OptimizelyCmsClient.TestConnectionAsync` — acquires OAuth token only.

---

## 5. Sitecore integration (`Migrator.Sitecore`)

### 5.1 Auth modes (`SitecoreAuthMode`)

| Mode | Behavior |
|------|----------|
| `ApiKey` | `sc_apikey` on query string |
| `Basic` | SSC `POST sitecore/api/ssc/auth/login` with domain/username/password; cookie session |
| `ApiKeyAndBasic` | Login + API key on login URL |

**Important:** HTTP Basic alone is not enough on Identity Server CMs. Session login is required for Basic modes (`SitecoreSscLogin`).

SSL: optional handler that ignores certificate errors for local/self-signed CM.

### 5.2 Item Service client

Endpoints used:

| Operation | Relative URL pattern |
|-----------|----------------------|
| By path | `sitecore/api/ssc/item?path=…&database=…&language=…&includeStandardTemplateFields=true` |
| By ID | `sitecore/api/ssc/item/{guid}?…` |
| Children | `sitecore/api/ssc/item/{guid}/children?…` |
| Media | `-/media/{id}.ashx` or item `MediaUrl` |

**Do not use `fields=*`** on this CM — it returns empty `{}`. Omitting `fields` returns all fields.

### 5.3 Subtree walk (`GetSubtreeAsync`)

1. Load root by path **without** inheritance enrichment (fast).
2. BFS children via `GetChildrenAsync` (also unenriched).
3. Skip descending under leaf-like product templates (name contains `Product` but not `Folder`/`Category`) to avoid deep unmapped commerce children.
4. Progress callbacks for UI.

When an item is actually migrated, `MigrationEngine` re-fetches by ID with **inheritance flatten**.

### 5.4 Template inheritance flatten

`EnrichWithInheritedFieldsAsync` / `GetFlattenedTemplateAsync`:

- Walks `__Base template` recursively
- Skips Standard Template (`1930BBEB-7805-471A-A3BE-4858AC7CF696`)
- Caches flattened templates in-memory
- Merges field definitions onto the item: existing values win; missing inherited names get null placeholders so mapping can target them
- Marks `IsInherited` / `DeclaringTemplateName` on field DTOs

### 5.5 Layout Service transport

Optional alternate `ISitecoreClient` for JSS Layout Service. Prefer Item Service for field-complete migration of commerce/product trees.

---

## 6. Optimizely integration (`Migrator.Optimizely`)

### 6.1 OAuth

- Token URL from settings (default CMS SaaS token endpoint on `OptimizelyConnectionSettings.TokenUrl`)
- `grant_type=client_credentials`
- Scope typically `api:admin` (UI field `OptiScope`)
- Token cached until ~2 minutes before expiry

### 6.2 Create content

`POST content` with body:

```json
{
  "contentType": "SitecoreProduct",
  "container": "<parent-key>",
  "initialVersion": {
    "displayName": "…",
    "locale": "en",
    "properties": {
      "title": { "value": "…" }
    }
  }
}
```

Requires Bearer token and `Prefer: return=representation`. Response must include `key`.

### 6.3 Update content

`POST content/{key}/versions` with `displayName`, `locale`, and `properties` wrapped as `{ "value": … }`. Creates a new draft version; **publishing is out of scope**.

### 6.4 Media upload

`POST media` multipart (`file`, `container`, `displayName`). On failure, falls back to creating an `Image` content item with `altText` so migration can continue (operator may re-upload binaries later).

---

## 7. Field transformers (`Migrator.Core/Transforms`)

Registry: `FieldTransformerRegistry` — lookup by `kind` (case-insensitive).

| Kind | Output shape | Notes |
|------|--------------|-------|
| `string` | string | Raw Sitecore value |
| `boolean` | bool | `1`/`true`/`yes` → true |
| `number` | decimal | Invariant culture |
| `dateTime` | ISO-8601 string | Sitecore `yyyyMMddTHHmmssZ` etc. |
| `richText` | `{ "html": "…" }` | HTML stored as-is |
| `link` | `{ url?, text?, title?, target?, contentKey? }` | Parses general link XML **and** `<image mediaid="…">`. Never emits `contentKey: null` (Opti 400). Uploads unresolved media on demand |
| `contentReference` | Opti key string or null | First GUID → ID map |
| `contentReferenceList` | string[] of keys | All GUIDs resolved via ID map |
| `contentArea` | string[] of keys | GUIDs extracted from XML/JSON-ish payloads |
| `media` | Opti key | Resolve or upload |

`BuildPropertiesAsync` skips empty/missing Sitecore fields. Unresolved references become omitted (null) on pass B and are patched on pass C when the ID map is complete.

---

## 8. Migration engine (`MigrationEngine`)

### 8.1 Inputs (`MigrationOptions`)

- Sitecore + Optimizely connection settings  
- Paths: source path, container key, label, `SingleItemOnly`  
- Mapping config  
- ID map SQLite path  
- `DryRun` (default true), `ContinueOnError`, optional `MaxItems`

### 8.2 High-level algorithm

```
Validate mapping + paths
Open IdMapStore
Load items (single path OR subtree)
Order by path depth (parents before children)
Pass A — media items (IsMediaItem or createAs=media)
Pass B — remaining content (create/update under resolved container)
Pass C — re-build properties + UpdateContent for each migrated content item (skip if DryRun)
Success = Failed == 0
```

### 8.3 Pass A — media

For each media item:

1. Resolve type map (or skip if `skipUnmappedTemplates`)
2. Dry run → status `DryRun`
3. Else download binary → `UploadMediaAsync` → upsert ID map

### 8.4 Pass B — content

For each non-media item:

1. Resolve type map (template ID then name via `MappingIndex`)
2. Re-fetch by ID with inheritance flatten
3. Soft field-coverage warning (unmapped valued fields) — does **not** hard-fail
4. Dry run → `DryRun` status
5. Else:
   - `ResolveContainerKey`: nearest already-migrated path ancestor’s Opti key, else root container
   - `BuildPropertiesAsync`
   - If ID map has key → `UpdateContentAsync`; else `CreateContentAsync`
   - Upsert ID map

### 8.5 Pass C — reference patch

After all creates, rebuild properties (references now resolve) and POST a new version for each mapped content item. Failures respect `ContinueOnError`.

### 8.6 Container resolution

Because Item Service may not always expose `ParentId`, the engine finds Sitecore items whose path is a prefix of the current item’s path, picks the longest prefix already in the ID map, and uses that Opti key as `container`. Otherwise uses the UI container key.

### 8.7 Progress reporting

`Progress` event with message, current/total counters, and optional `MigrationItemResult` for the results grid.

---

## 9. ID map (`IdMapStore`)

**Location:** `%LocalAppData%\SitecoreToOptiMigrator\idmap.db`

SQLite table `IdMap`:

| Column | Purpose |
|--------|---------|
| `SitecoreItemId` | PK (GUID string) |
| `OptimizelyKey` | CMS SaaS content key |
| `ContentType` | Opti type at write time |
| `SourcePath` | Sitecore path |
| `TargetPath` | Optional label from UI |
| `UpdatedAt` | ISO timestamp |

Upsert on conflict enables **re-runs** (update existing Opti items) and **reference rewrite** across passes and sessions.

---

## 10. Mapping validation and index

### `MappingValidator.Validate`

Errors for:

- Empty type list  
- Missing Opti content type  
- Invalid `createAs`  
- Duplicate template IDs/names  
- Empty Sitecore/Opti field names  
- Duplicate Sitecore or Opti field mappings (must stay 1:1)  
- Unknown `kind`

Warnings for types with zero field mappings.

### `MappingIndex`

- Lookup by template GUID (normalized) then by template name  
- `IsIgnoredField` checks defaults + per-type ignore lists

Runtime field coverage (`ValidateFieldCoverage`) is separate: warns when valued Sitecore fields are neither mapped nor ignored, but migration continues.

---

## 11. End-to-end data flow

```
[UI] paths + dry-run + mapping path
        │
        ▼
[MappingLoader] → MappingConfig → MappingValidator
        │
        ▼
[ISitecoreClient] GetSubtree / GetItemByPath
        │
        ▼
[MigrationEngine] order by depth
        │
        ├─ Pass A: DownloadMedia → UploadMedia → IdMap
        ├─ Pass B: GetItemById (enrich) → Transform fields → Create/Update → IdMap
        └─ Pass C: Transform again → Update versions (refs filled)
        │
        ▼
[MigrationRunResult] → UI grid + CSV export
```

Property payload to Opti (after transformers):

```
Sitecore "Title" = "Home"
  → FieldMapping kind=string, optimizely=title
  → properties["title"] = "Home"
  → API wraps as properties.title.value = "Home"
```

Link/image example:

```
Sitecore Image XML <image mediaid="{…}" src="…" />
  → LinkTransformer → { contentKey?, url?, text?, title? }
  → never { contentKey: null }
```

---

## 12. Operational workflows

### Recommended first run

1. Fill `sitecore.secrets.json` and `optimizely.secrets.json`
2. Confirm `content-types.json` includes the templates under your path
3. **Test Sitecore** / **Test Optimizely**
4. Set Sitecore path + Opti container key
5. **Validate mapping**
6. Keep **Dry run** checked → Start (expect `DryRun` counts, Created=0)
7. Uncheck dry run → confirm → Start
8. Export CSV; inspect Failed rows and Serilog files

### Single-item mode

Check **Single item only** to call `GetItemByPathAsync` once (no children). Useful for debugging one product/page without walking the tree.

### Re-run behavior

If the Sitecore GUID is already in `idmap.db`, Pass B **updates** instead of create. Clear or delete the DB only when you intentionally want new Opti keys.

---

## 13. Logging and diagnostics

| Sink | Location |
|------|----------|
| UI log list | In-memory, newest first, capped at 500 lines |
| Serilog file | `%LocalAppData%\SitecoreToOptiMigrator\logs\migrator-YYYYMMDD.log` |
| Console | Attached when running under a console host |

Engine and clients log warnings/errors (mapping warnings, media fallback, create/update failures). Connection tests surface HTTP status, request URL, and response snippets in the UI.

---

## 14. Known limitations (current design)

- No automatic publish / workflow complete  
- No Sitecore layout, personalization, or device editors  
- Rich text is HTML only (no TipTap JSON conversion)  
- Media upload endpoint may fall back to empty `Image` content stubs  
- Layout Service transport is secondary; Item Service is the primary path  
- Production CM may block SSC anonymous access or login — may require CM config (`ServicesClient` / local-only policy)  
- Leaf product subtree pruning may skip children under product items by design  

---

## 15. Extending the migrator

### New field kind

1. Implement `IFieldTransformer` with a unique `Kind`  
2. Register in `FieldTransformerRegistry`  
3. Document the Opti JSON shape  
4. Use the kind in `content-types.json`

### New Sitecore transport

Implement `ISitecoreClient` and branch in `MainWindow.CreateSitecoreClient`.

### New Opti write behavior

Extend `IOptimizelyClient` / `OptimizelyCmsClient` (e.g. publish API) and call from the engine after create/update if needed.

### Mapping generation

Generate `content-types.json` from Sitecore template export + Opti content-type registry so every valued field is either mapped or ignored.

---

## 16. Key source map (quick reference)

| Concern | File |
|---------|------|
| UI orchestration | `Migrator.App/MainWindow.xaml(.cs)` |
| Serilog bootstrap | `Migrator.App/App.xaml.cs` |
| Migration passes | `Migrator.Core/Migration/MigrationEngine.cs` |
| Options / progress | `Migrator.Core/Migration/MigrationOptions.cs` |
| Mapping load/validate/index | `Migrator.Core/Mapping/*` |
| Field transforms | `Migrator.Core/Transforms/FieldTransformers.cs` |
| ID map | `Migrator.Core/IdMap/IdMapStore.cs` |
| Secrets | `Migrator.Core/Config/*CredentialsLoader.cs` |
| Sitecore Item Service | `Migrator.Sitecore/SitecoreItemServiceClient.cs` |
| Sitecore login | `Migrator.Sitecore/SitecoreSscLogin.cs` |
| Opti REST | `Migrator.Optimizely/OptimizelyCmsClient.cs` |
| Mapping data | `Config/mappings/content-types.json` |

---

## 17. Build and run

```powershell
cd C:\Projects\Sitecore_to_Optimizely_Saas
dotnet build
dotnet run --project Migrator.App
```

Or open `SitecoreToOptiMigrator.sln` in Visual Studio and press F5.

High-level usage notes also live in the solution root `README.md`; this document is the **implementation-level** companion kept next to the App project.
