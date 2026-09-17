# Window Identity Diagnostics

A small read-only console utility that lists matching top-level Windows windows and reports:

- process ID and process name
- executable path
- file description and product name
- window title and actual Win32 class name
- HWND, visibility, and window display affinity

Start Overlay, then run the utility without arguments. To inspect another application, pass any process, title, class, or path fragment:

```powershell
dotnet run --project .\WindowIdentityDiagnostics.csproj -- Overlay
```
