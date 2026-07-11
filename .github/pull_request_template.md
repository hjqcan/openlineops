## Summary

- 

## Verification

- [ ] `dotnet format OpenLineOps.sln whitespace --no-restore --verify-no-changes --exclude lib/pythonscript --verbosity minimal`
- [ ] `dotnet format OpenLineOps.sln style --no-restore --verify-no-changes --exclude lib/pythonscript --severity warn --verbosity minimal`
- [ ] `dotnet build OpenLineOps.sln --no-restore`
- [ ] `dotnet test OpenLineOps.sln --no-build -m:1`
- [ ] Desktop checks, if Electron code changed
- [ ] Documentation updated, if behavior or setup changed

## Architecture Notes

- Bounded context touched:
- Public contracts changed:
- Persistence or plugin impact:
