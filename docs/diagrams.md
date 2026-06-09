# focument — how it works (diagrams)

> **Baseline edition** — the event-sourced write side, a projected read model, and
> full version history. *No saga, no quota, no approval yet* — those arrive in the
> next step (the saga commit carries an extended version of this file).

These diagrams are derived from the actual `DocumentAggregate` and `Projection`
code. They render on GitHub and in VS Code's Mermaid preview, and can be imported
into Excalidraw (Insert → Mermaid) for a hand-drawn version.

The whole system is one idea: **commands are decided into events, events are the
source of truth, and a read model is projected from them.** A web request sends a
command, then reads its own write back off the projection.

---

## 1. Architecture / data flow

```mermaid
flowchart LR
    Browser["Browser (wwwroot)"]
    W["Endpoints (web)"]
    Doc["DocumentAggregate"]
    Journal[("Event journal")]
    Proj["Projection"]
    Read[("Read model")]

    Browser -->|"POST command"| W
    W -->|"command + cid"| Doc
    Doc -->|"persist CreatedOrUpdated"| Journal
    Journal -->|"replay past offset"| Proj
    Proj -->|"upsert row + version row"| Read
    Proj -.->|"notify cid"| W
    W -->|"queries"| Read
    W ==>|"await cid, then respond"| Browser
```

One aggregate, one event type. The web layer never reads aggregate state directly —
it sends a command, then reads its own write off the projected read model.

---

## 2. Creating or updating a document

The endpoint subscribes to the correlation id (`cid`) **before** sending the
command, then awaits the projected event — so the HTTP response only returns once
the read model reflects the write (read-your-writes).

```mermaid
sequenceDiagram
    autonumber
    actor U as Alice (browser)
    participant W as Endpoints
    participant D as DocumentAggregate
    participant P as Projection + read model

    U->>W: POST /api/document
    W->>P: SubscribeForFirst(cid)
    W->>D: CreateOrUpdate(doc) — cid
    D->>D: persist CreatedOrUpdated
    D-->>P: CreatedOrUpdated
    Note over P: insert new row, or update existing,<br/>and append a DocumentVersions row
    P-->>W: notify cid
    W-->>U: "Document saved!" or "Document updated!"
```

A single `CreatedOrUpdated` fact covers both create and update; the projection
decides which by whether a row already exists. With no saga in the way, this event
is terminal — it's notified immediately, so the call returns as soon as the read
model has caught up.

---

## 3. Version history & restore

Every `CreatedOrUpdated` appends a row to `DocumentVersions`, so the full history is
kept. **Restore never rewrites history** — it re-issues the chosen version's content
as a fresh write, producing a new version on top.

```mermaid
sequenceDiagram
    autonumber
    actor U as Alice
    participant W as Endpoints
    participant D as DocumentAggregate
    participant P as Projection + read model

    U->>W: POST /api/document/restore (Id, Version)
    W->>P: read that version from DocumentVersions
    W->>D: CreateOrUpdate(old content) — cid
    D->>D: persist CreatedOrUpdated
    D-->>P: CreatedOrUpdated
    Note over P: append a new version (history preserved)
    P-->>W: notify cid
    W-->>U: "Version restored!"
```

---

> **Persist vs Defer.** `CreatedOrUpdated` is *persisted* — it's the fact, journaled
> and projected. The one *deferred* event is the wrong-id `Error`: it's published to
> subscribers but never journaled, because nothing actually happened to a document.
