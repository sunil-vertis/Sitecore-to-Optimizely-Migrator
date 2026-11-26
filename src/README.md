# Sitecore → Optimizely CMS SaaS Migrator

Windows WPF app (.NET 8) that migrates content from a **Sitecore path** to an **Optimizely CMS SaaS container**, using a strict **1:1 content-type and field mapping**.

## Solution layout

| Project | Role |
|---------|------|
| `Migrator.App` | WPF UI — connections, paths, mapping, dry-run / migrate, report export |
| `Migrator.Core` | Mapping validation, field transformers, ID map (SQLite), migration engine |
| `Migrator.Sitecore` | Sitecore Item Service REST client |
| `Migrator.Optimizely` | Optimizely CMS SaaS OAuth + Content API client |
| `Config/mappings` | JSON type/field maps |

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Sitecore CM with **Item Service / Services Client** reachable over HTTPS
- Optimizely CMS SaaS **API key** (Client ID + Secret) with `api:admin` scope  
  See [Create API keys](https://docs.optimizely.com/cms-saas/docs/api-keys)

## Run

```powershell
cd C:\Projects\Sitecore_to_Optimizely_Saas\src
dotnet build
dotnet run --project Migrator.App
```

Or open `SitecoreToOptiMigrator.sln` in Visual Studio and press F5.

## Secrets (do not commit)

Credentials are **not** entered in the UI. They are loaded from JSON files under `Config/`. Those files are gitignored (`*.secrets.json`); only the `.sample.json` templates are committed.

Place the real files next to the samples (or anywhere under a parent `Config/` folder the app can discover when walking up from the EXE directory):

```text
src/Config/
  sitecore.secrets.sample.json      ← committed template
  sitecore.secrets.json             ← your real credentials (local only)
  optimizely.secrets.sample.json    ← committed template
  optimizely.secrets.json           ← your real credentials (local only)
  mappings/
    content-types.json              ← type/field mapping
```

### Sitecore secrets

1. Copy `Config/sitecore.secrets.sample.json` → `Config/sitecore.secrets.json`
2. Fill in username and password (Sitecore domain user, e.g. `sitecore\admin`)

```json
{
  "username": "sitecore\\admin",
  "password": "YOUR_PASSWORD"
}
```

Used for SSC session login (`POST /sitecore/api/ssc/auth/login`) when auth mode is **Username/password** or **API key + username/password**. The API key (if used) is still entered in the UI as the Item Service key GUID.

### Optimizely secrets

1. Copy `Config/optimizely.secrets.sample.json` → `Config/optimizely.secrets.json`
2. Fill in the CMS SaaS API key Client ID and Client Secret ([Create API keys](https://docs.optimizely.com/cms-saas/docs/api-keys))

```json
{
  "clientId": "YOUR_CLIENT_ID",
  "clientSecret": "YOUR_CLIENT_SECRET"
}
```

Used for OAuth2 `client_credentials` (scope typically `api:admin`). Never commit real secrets or paste them into the repo.

## Content-type mapping file

**Path:** `Config/mappings/content-types.json`

This file is the 1:1 bridge between Sitecore templates and Optimizely content types. The app validates it before every migration.

### Format

```json
{
  "version": 1,
  "defaults": {
    "locale": "en",
    "skipUnmappedTemplates": true,
    "ignoreFields": [
      "__Created",
      "__Created by",
      "__Updated",
      "__Updated by",
      "__Revision",
      "__Sortorder",
      "__Security",
      "__Owner",
      "__Base template",
      "__Standard values"
    ]
  },
  "types": [
    {
      "sitecoreTemplateId": "{6424A9A6-CDD5-4FFF-AB0C-F614456398DF}",
      "sitecoreTemplateName": "mvp_SitecoreProduct",
      "optimizelyContentType": "SitecoreProduct",
      "createAs": "component",
      "ignoreFields": [
        "AlternateImage",
        "SyncSourceId"
      ],
      "fields": [
        {
          "sitecore": "Snapshot",
          "optimizely": "snapshot",
          "kind": "richText"
        },
        {
          "sitecore": "KeyProductFeatures01",
          "optimizely": "keyProductFeatures01",
          "kind": "string"
        },
        {
          "sitecore": "MainImage",
          "optimizely": "mainImage",
          "kind": "link"
        }
      ]
    }
  ]
}
```

### Field reference

| Property | Meaning |
|----------|---------|
| `defaults.locale` | Default Opti locale for created versions |
| `defaults.skipUnmappedTemplates` | If `true`, Sitecore templates with no type entry are skipped; if `false`, they fail |
| `defaults.ignoreFields` | Global Sitecore fields to skip (usually system `__*` fields) |
| `types[].sitecoreTemplateId` | Sitecore template GUID (with or without braces) |
| `types[].sitecoreTemplateName` | Sitecore template name (fallback lookup if ID differs) |
| `types[].optimizelyContentType` | Optimizely CMS SaaS content type key |
| `types[].createAs` | `page` · `component` · `folder` · `media` |
| `types[].ignoreFields` | Per-template fields to skip (in addition to defaults) |
| `types[].fields[].sitecore` | Sitecore field name |
| `types[].fields[].optimizely` | Optimizely property key |
| `types[].fields[].kind` | How the value is transformed (see below) |

Supported `kind` values:

`string` · `richText` · `boolean` · `number` · `dateTime` · `link` · `contentReference` · `contentReferenceList` · `contentArea` · `media`

### Mapping rules

1. **One** Optimizely content type per Sitecore template  
2. **One** Optimizely property per Sitecore field (no duplicates either side)  
3. Every Sitecore field that has a value must be mapped or listed in `ignoreFields`  
4. Unknown field `kind` values fail validation  

Prefer **identical** Opti property keys to Sitecore field names when your frontend content types match Sitecore 1:1.

### Inherited Sitecore templates

If a Sitecore template inherits base templates, fields from those bases are **flattened onto the concrete template** and treated as its direct fields:

- Map them on the **derived** type entry (e.g. `Article`), not as a separate type for the base template.
- Do not create a 1:1 Opti type for shared base templates unless you also migrate items of that template.
- Standard Template is skipped during flatten (its `__*` fields stay in `ignoreFields`).
- Example: `Article` inherits `_PageBase` with `MetaTitle` → list `MetaTitle` under `Article.fields`.

## UI workflow

1. Ensure `sitecore.secrets.json` and `optimizely.secrets.json` exist under `Config/`  
2. Enter Sitecore base URL (+ API key if used) → **Test Sitecore**  
3. Confirm Opti API base / locale / scope → **Test Optimizely**  
4. Set **Sitecore path** (e.g. `/sitecore/content/Home/Articles`)  
5. Set **Opti container key** (parent content key in SaaS)  
6. Point at `Config/mappings/content-types.json` → **Validate mapping**  
7. Leave **Dry run** checked → **Start migration** (lists what would migrate)  
8. Uncheck dry run only when ready to write  
9. **Export report** (CSV) when finished  

ID map (for re-runs and reference rewrite) is stored at:

```text
%LocalAppData%\SitecoreToOptiMigrator\idmap.db
```

Logs:

```text
%LocalAppData%\SitecoreToOptiMigrator\logs\
```

## Migration passes

1. **Media** — upload binaries, record Sitecore ID → Opti key  
2. **Content** — create/update pages & components under the target container (parents before children)  
3. **Patch references** — rewrite links, multilists, content areas using the ID map  

## Notes / limitations (v1)

- Sitecore auth assumes Item Service + `sc_apikey` / bearer API key. Adjust `SitecoreItemServiceClient` if your CM uses a different auth scheme.  
- Optimizely media upload tries `POST /media`; if your instance differs, the client falls back to creating an `Image` content item (re-upload binaries as needed).  
- Rich text is stored as HTML (`{ "html": "..." }`). Align with your Opti rich-text property shape if the API expects TipTap JSON.  
- Publishing / workflow transitions are out of scope — content is created as versions via the REST API.  
- Personalization, device editors, and Sitecore layout details are not migrated.

## Extending

- Add field kinds in `Migrator.Core/Transforms` and register them in `FieldTransformerRegistry`.  
- Swap Sitecore transport (GraphQL) by implementing `ISitecoreClient`.  
- Generate mapping JSON from your Sitecore template export + Opti content-type registry for full 1:1 coverage.
