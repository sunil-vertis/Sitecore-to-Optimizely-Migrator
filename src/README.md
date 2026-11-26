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
cd C:\Projects\Sitecore_to_Optimizely_Saas
dotnet build
dotnet run --project Migrator.App
```

Or open `SitecoreToOptiMigrator.sln` in Visual Studio and press F5.

## Configure mapping (required)

Copy and edit the sample:

```text
Config/mappings/content-types.sample.json
→ Config/mappings/content-types.json
```

Rules enforced by the app:

1. **One** Optimizely content type per Sitecore template  
2. **One** Optimizely property per Sitecore field (no duplicates)  
3. Every Sitecore field that has a value must be mapped or listed in `ignoreFields`  
4. Unknown field `kind` values fail validation  

Supported `kind` values:

`string` · `richText` · `boolean` · `number` · `dateTime` · `link` · `contentReference` · `contentReferenceList` · `contentArea` · `media`

Prefer **identical property names** to Sitecore field names so content migration stays 1:1 with your Opti frontend types.

### Inherited Sitecore templates

If a Sitecore template inherits base templates, fields from those bases are **flattened onto the concrete template** and treated as its direct fields:

- Map them on the **derived** type entry (e.g. `Article`), not as a separate type for the base template.
- Do not create a 1:1 Opti type for shared base templates unless you also migrate items of that template.
- Standard Template is skipped during flatten (its `__*` fields stay in `ignoreFields`).
- Example: `Article` inherits `_PageBase` with `MetaTitle` → list `MetaTitle` under `Article.fields`.

## UI workflow

1. Enter Sitecore base URL + API key → **Test Sitecore**  
2. Enter Opti Client ID / Secret → **Test Optimizely**  
3. Set **Sitecore path** (e.g. `/sitecore/content/Home/Articles`)  
4. Set **Opti container key** (parent content key in SaaS)  
5. Load / validate mapping JSON  
6. Leave **Dry run** checked → **Start migration** (lists what would migrate)  
7. Uncheck dry run only when ready to write  
8. **Export report** (CSV) when finished  

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
