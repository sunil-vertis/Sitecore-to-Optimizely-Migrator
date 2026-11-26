# Path note

The migrator application and Visual Studio solution live under:

```text
C:\Projects\Sitecore_to_Optimizely_Saas\src
```

| Item | Path |
|------|------|
| Solution | `src\SitecoreToOptiMigrator.sln` |
| WPF app | `src\Migrator.App\` |
| Config / mappings | `src\Config\` |
| Implementation guide | `docs\IMPLEMENTATION.md` |

Build / run from `src`:

```powershell
cd C:\Projects\Sitecore_to_Optimizely_Saas\src
dotnet build
dotnet run --project Migrator.App
```
