# focument — how it works (diagrams)

> **Saga edition** — the full workshop: the event-sourced write side, the read
> model, and the quota saga + colleague approval. The pre-saga **baseline** commit
> (`baseline-no-saga`) carries a trimmed version of this file.

These diagrams are derived from the actual `DocumentAggregate`, `UserAggregate`,
`QuotaSaga` and `Projection` code. They render on GitHub and in VS Code's Mermaid
preview, and can be imported into Excalidraw (Insert → Mermaid) if you want a
hand-drawn version for slides.

The whole system is one idea: **commands are decided into events, events are the
source of truth, and a read model is projected from them**. A saga coordinates the
two aggregates when a write needs a quota check.

---

## 1. Architecture / data flow

```mermaid
flowchart LR
    Browser["Browser (wwwroot)"]
    W["Endpoints (web)"]
    Doc["DocumentAggregate"]
    User["UserAggregate"]
    Saga["QuotaSaga"]
    Journal[("Event journal")]
    Proj["Projection"]
    Read[("Read model")]

    Browser -->|"POST command"| W
    W -->|"command + cid"| Doc
    Doc -->|"persist events"| Journal
    User -->|"persist events"| Journal
    Doc -.->|"CreateOrUpdateRequested"| Saga
    Saga -.->|"ConsumeQuota"| User
    User -.->|"QuotaApproved / QuotaRejected"| Saga
    Saga -.->|"Approve / Hold"| Doc
    Journal -->|"replay past offset"| Proj
    Proj -->|"upsert rows"| Read
    Proj -.->|"notify cid"| W
    W -->|"queries"| Read
    W ==>|"await cid, then respond"| Browser
```

Solid arrows are the persisted command/event/projection path; dotted arrows are
the saga's message passing. The web layer never reads aggregate state directly —
it sends a command, then **reads its own write** off the projected read model
(see below).

---

## 2. Creating a document (the quota check)

The endpoint subscribes to the correlation id (`cid`) **before** sending the
command, then awaits the saga's terminal verdict. `CreateOrUpdateRequested` is
persisted (it starts the saga and writes a *pending* row) but is **not** notified —
so the HTTP call stays open until the document is actually Approved or Held.

```mermaid
sequenceDiagram
    autonumber
    actor U as Alice (browser)
    participant W as Endpoints
    participant D as DocumentAggregate
    participant S as QuotaSaga
    participant Q as UserAggregate
    participant P as Projection + read model

    U->>W: POST /api/document
    W->>P: SubscribeForFirst(cid)
    W->>D: CreateOrUpdate(doc, owner) — cid
    D->>D: persist CreateOrUpdateRequested
    D-->>P: CreateOrUpdateRequested
    Note over P: insert pending row — NOT notified
    D-->>S: CreateOrUpdateRequested starts the saga
    S->>Q: ConsumeQuota(docId)

    alt within quota (under 3 writes per minute)
        Q->>Q: persist QuotaApproved
        Q-->>S: QuotaApproved
        S->>D: Approve
        D->>D: persist Approved
        D-->>P: Approved
        P-->>W: notify cid
        W-->>U: "Document saved!"
    else over quota
        Q-->>S: QuotaRejected (deferred, not journaled)
        S->>D: Hold
        D->>D: persist HeldForApproval
        D-->>P: HeldForApproval
        P-->>W: notify cid
        W-->>U: "Quota exceeded — sent for approval."
    end
```

> **Persist vs Defer.** `QuotaApproved` is *persisted* — it changes the user's
> consumed-slots state, so it must be journaled and replayable. `QuotaRejected` is
> *deferred* — it changes no state, but is still delivered to the saga so it can
> react. Deferred events reach sagas and subscribers but never hit the journal, so
> the projection (which reads the journal) never sees them.

---

## 3. Colleague approval

A *held* document is finalised by a **different** user — you can't approve your own.
This is a fresh request, with its own read-your-writes await.

```mermaid
sequenceDiagram
    autonumber
    actor B as Bob (colleague)
    participant W as Endpoints
    participant D as DocumentAggregate
    participant P as Projection + read model

    B->>W: POST /api/document/approve (Id, Username)
    Note over W: reject if Username == doc.Owner
    W->>P: SubscribeForFirst(cid)
    W->>D: Approve — cid
    D->>D: persist Approved
    D-->>P: Approved
    P-->>W: notify cid
    W-->>B: "Approved!"
```

---

## 4. QuotaSaga state machine

One saga instance per created document. It starts from the Document's
`CreateOrUpdateRequested`, asks the User to consume a slot, and tells the Document
the verdict. Each state has one side effect (the command it issues on entry).

```mermaid
stateDiagram-v2
    [*] --> CheckingQuota: CreateOrUpdateRequested
    CheckingQuota --> Approving: QuotaApproved
    CheckingQuota --> Holding: QuotaRejected
    Approving --> Done: Approved
    Holding --> Done: HeldForApproval
    Done --> [*]

    note right of CheckingQuota
        side effect: ConsumeQuota → User
    end note
    note right of Approving
        side effect: Approve → Document
    end note
    note right of Holding
        side effect: Hold → Document
    end note
```

The side-effect commands carry the `docId`, so the saga's at-least-once retries are
safe: re-issuing `ConsumeQuota` re-grants the same slot (the User is idempotent per
document), and re-issuing `Approve`/`Hold` is a no-op once the Document is already in
that state.
