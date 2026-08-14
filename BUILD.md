# Build & Publish — Dloizides.Jobs.EntityFrameworkCore

## Build + test

```bash
dotnet build Dloizides.Jobs.EntityFrameworkCore.sln -c Release       # net8.0;net10.0, 0 warnings
dotnet test tests/Dloizides.Jobs.EntityFrameworkCore.Tests/Dloizides.Jobs.EntityFrameworkCore.Tests.csproj -c Debug
```

The test suite runs on **SQLite (relational)** — the in-memory provider is deliberately unusable, because
it ignores `ExecuteUpdate` and the unique index and would false-green every concurrency guarantee.

## Publish to nuget.org

Depends on `Dloizides.Jobs` — publish **that first** and let it propagate, then:

```powershell
cd NuGetPackages/Dloizides.Jobs.EntityFrameworkCore
.\publish.ps1 -NoBump
# or
.\publish.ps1 -Bump patch
```

The API key auto-loads from `SaaS/.env.local`. Poll
`https://api.nuget.org/v3-flatcontainer/dloizides.jobs.entityframeworkcore/index.json` after pushing.
