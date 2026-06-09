# focument_workshop

A small, teachable **CQRS + Event Sourcing** app built on
[FCQRS](https://github.com/onurgumus/FCQRS), written in C# with C# 15 `union`
types. It's a stripped-down fork of
[focument-csharp](https://github.com/OnurGumus/focument-csharp), made for a workshop.

It's a tiny document store: you create and edit documents, every change is an **event**, you can
browse a document's **version history** and restore an earlier version, and there's a per-user
**quota** (3 documents/minute) enforced by a **saga** that parks over-quota writes for a colleague to
approve.

## Prerequisites

- **.NET 11 SDK (preview)** — C# 15 `union` types need `LangVersion preview`. `global.json` pins the
  exact SDK band, so the build will tell you if yours is too old. Get it from
  [dotnet.microsoft.com/download/dotnet/11.0](https://dotnet.microsoft.com/download/dotnet/11.0).
- That's it — the read model is a local **SQLite** file, created on first run. No other services.

Check your SDK:

```bash
dotnet --version    # should be 11.0.1xx (preview)
```

## Set up and run

```bash
git clone https://github.com/OnurGumus/focument_workshop.git
cd focument_workshop
dotnet run --project src/Server
```

The first run restores packages, builds, and creates the SQLite file. When the console prints
`Now listening on: http://localhost:5000`, open that URL in a browser.

> Using a different port? Open whatever URL the console prints.

**No .NET 11 preview locally?** Open the repo in a container with the exact SDK already pinned —
zero local setup:

- **GitHub Codespaces:** [![Open in GitHub Codespaces](https://github.com/codespaces/badge.svg)](https://codespaces.new/OnurGumus/focument_workshop)
- **VS Code:** *Dev Containers: Reopen in Container* (uses `.devcontainer/`).

Then run as above — the forwarded port opens in your browser.

## Try it (the 2-minute tour)

1. **Create a document.** Enter a **username** (say `alice`), a title and some content, and click
   *Create*. It appears in **Documents**, marked *Approved* — `alice` is within quota.
2. **Hit the quota.** As `alice`, create **three** documents in under a minute, then a **fourth**.
   The fourth comes back *"Quota exceeded — sent for approval."* and shows up under
   **Needs approval — over quota**. (The quota is 3 per rolling minute.)
3. **Approve as a colleague.** Switch the username to `bob` and *Approve* alice's parked document.
   `alice` can't approve her own — only a different user can.
4. **Edit and time-travel.** Open a document, change its content, and save — the version bumps.
   Click **Version history** to see every version and **restore** an earlier one (restoring adds a
   *new* version with the old content; history is never rewritten).

Everything you do is an event in the log; the list you see is a read model projected from it.

## Watch it work

Keep the server console visible while you click. With the compact log formatter on, each line is
one step of the flow — so **the console is [sequence diagram #2](docs/diagrams.md) live**:

```
13:01:31 info DocumentAggregate  handle CreateOrUpdate
13:01:31 info QuotaSaga          saga handle CreateOrUpdateRequested in CheckingQuota
13:01:31 info UserAggregate      handle ConsumeQuota
13:01:31 info QuotaSaga          saga handle QuotaApproved in CheckingQuota
13:01:31 info DocumentAggregate  apply Approved
13:01:31 info Projection         projected … at offset 6
```

Create a 4th document over quota and the saga branches into `Holding` instead of `Approving`.

**It's all in the log.** Stop the server (Ctrl+C) and start it again — your documents are still
there, because state *is* the event journal: aggregates rebuild themselves by replaying their
events, and the projection resumes from its stored offset. Nothing survives in memory between
runs. (The saga's commands are idempotent and at-least-once, so even a crash mid-flow re-grants
the same quota slot instead of double-counting — see *Persist vs Defer* in the diagrams.)

## Start clean

The journal and read model live in one SQLite file. To wipe and start over:

```bash
rm -f src/Server/focument_workshop.db*
```

Set `FOCUMENT_DB_PATH` to use a different file/location:

```bash
FOCUMENT_DB_PATH=/tmp/focument.db dotnet run --project src/Server
```

## Run the tests

```bash
dotnet test
```

Pure domain tests (decide/fold, the quota window) plus a projection/saga integration test.

## How it's built (one screen)

The whole write side is declared in a single DI call — `builder.Services.AddFocument(...)` in
[`src/Server/App.cs`](src/Server/App.cs) — and FCQRS starts it with the host. The pieces:

- **Aggregates** (`src/Server/DocumentAggregate.cs`, `UserAggregate.cs`) — `HandleCommand` decides,
  `ApplyEvent` folds. Pure functions; no persistence code.
- **Saga** (`src/Server/QuotaSaga.cs`) — spans the two aggregates: a write asks the `User` for a
  quota slot, then tells the `Document` to approve or hold. The one piece that genuinely needs a saga.
- **Projection** (`src/Server/Projection.cs`) — folds events into the SQLite read tables.
- **Model** (`src/Model/`) — commands/events/state as C# 15 `union` types and validated value objects.
- **Web** (`src/Server/Program.cs`, `Endpoints.cs`, `wwwroot/`) — minimal Minimal-API host + a
  vanilla-JS UI. The endpoints use **read-your-writes**: subscribe to a correlation id, send the
  command, await — so the UI only refreshes once the read model has caught up.

## See what a saga costs

`master` is the complete app. The `baseline-no-saga` tag is the same app **before** the quota saga —
plain create/edit/version with no cross-aggregate rule. Diff them to see exactly what adding a saga
involved:

```bash
git diff baseline-no-saga master
```

## More

- **FCQRS** — the framework and its docs: [onurgumus.github.io/FCQRS](https://onurgumus.github.io/FCQRS/)
- **focument-csharp** — the full (non-workshop) C# app:
  [github.com/OnurGumus/focument-csharp](https://github.com/OnurGumus/focument-csharp)
- **focument_fsharp** — the same domain in idiomatic F#:
  [github.com/OnurGumus/focument_fsharp](https://github.com/OnurGumus/focument_fsharp)
