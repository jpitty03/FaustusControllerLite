# FaustusControllerLite Agent Instructions

## Project scope

This repository contains the FaustusControllerLite ExileCore plugin. The host application is the compiled ExileCore distribution three directory levels above this project. Treat host DLLs as external dependencies; do not modify compiled binaries or runtime configuration while working on source code.

The related test project is outside this repository at:

`../../Tests/FaustusControllerLite.Tests/FaustusControllerLite.Tests.csproj`

Runtime data is outside this repository at:

`../../../config/FaustusControllerLite/`

Do not edit runtime logs, bankroll files, audit files, calibration files, or generated plugin output unless the user explicitly requests operational-data work.

## Existing instructions

Read `CLAUDE.md` before making changes. It contains the project architecture, safety invariants, and detailed behavior requirements. If this file conflicts with a user request, ask for clarification before editing safety-critical behavior.

## Development rules

- Read the relevant implementation and tests before changing code.
- Prefer the smallest coherent change.
- Preserve existing public behavior and safety gates unless the task explicitly changes them.
- Do not perform unrelated refactors.
- Treat input automation, order placement, cancellation, collection, stash transfer, bankroll persistence, and workflow recovery as safety-critical.
- Never weaken a guard, remove a verification step, or broaden an input permission merely to make a test pass.
- Do not touch the compiled host application's DLLs or generated output.
- Do not commit secrets, credentials, runtime data, or generated build artifacts.

## Validation

Run from this project directory:

```text
dotnet build FaustusControllerLite.csproj --no-restore
```

Run the related tests from this project directory:

```text
dotnet run --project ../../Tests/FaustusControllerLite.Tests/FaustusControllerLite.Tests.csproj --no-restore
```

Both commands are expected to finish with zero warnings and zero errors. Report commands that could not be run and distinguish environment failures from product failures.

## Git safety

- Inspect `git status` before editing.
- Do not overwrite or discard existing user changes.
- Do not reset, force-push, or rewrite shared history.
- Keep changes scoped to the active task.
- Do not commit unless the user explicitly requests it.
