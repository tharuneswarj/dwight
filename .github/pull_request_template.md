## What this changes and why

## Tests
- [ ] New behaviour or fix has a test that failed before and passes now
- [ ] `dotnet build Dwight.sln -p:CI=true` and `dotnet test Dwight.sln` pass; per-project counts:
- [ ] The CI gates in CONTRIBUTING.md pass

## Checks
- [ ] No API key, `.gh` or `.ghx` file, or private path is included
- [ ] User-visible text does not promise a future version
- [ ] Canvas changes run on the UI thread, in one undo record
- [ ] I have said whether I tested in Rhino or only in the suite
