# Tech Spec: Capability Storage (Knowledge / Skills / Widgets)

> Status: Proposed. Written against the `next` worktree. Not yet implemented.
> Scope: persistence and device-sync only. No UI/UX behavior changes, no new crypto, no new relay surface.

## 0. Summary

`KnowledgeItem`, `Skill`, and `Widget` (the three "capability" types, per the existing
`CapabilityAudience` / "capability bundle" naming already in `Models.cs`) are today plain
`List<T>` fields on `MeshProfile`, serialized whole into the single-row `profile` table on
every save, with no per-item version and no tombstone. `Circle`/`Contact` already got a
partial fix (real version/tombstone tracking) but still live in the same blob. `MemoryItem`
got the *full* fix: its own table, its own load/save path, excluded from the blob entirely.

This spec moves Knowledge/Skill/Widget to the same treatment `MemoryItem` already has,
using the exact same primitives (`DeviceSyncOperation`, `DeviceSyncVersion`,
`sync_versions`/`sync_tombstones`, `device_envelope_outbox`, `MessageCrypto`). No new
security surface is introduced; the entire change is a persistence and sync-plumbing
change under types that already exist.

## 1. Current state (confirmed in code)

| Fact | Evidence |
|---|---|
| Knowledge/Skills/Widgets are plain `List<T>` on `MeshProfile` | `Domain/Models.cs:1287,1289,1291` |
| `Skill` has **no timestamp field at all** | `Domain/Models.cs:406-421` |
| Whole profile (minus conversations/ownChat/ownThreads/memories) is serialized to one JSON blob on every save | `Services/MeshDb.cs` `SaveProfile(SqliteCommand,MeshProfile)`, `node.Remove("conversations"/"ownChat"/"ownThreads"/"memories")` |
| `profile` is a single-row table | `CREATE TABLE IF NOT EXISTS profile(id INTEGER PRIMARY KEY CHECK(id=1), json TEXT NOT NULL)`, `MeshDb.cs:109` |
| `MemoryItem` already got the full treatment: dedicated `memories` table, dedicated load/upsert/delete, excluded from the blob | `MeshDb.cs:207-224` (schema), `LoadMemories()` (`MeshDb.cs:612`), `UpsertMemory`/`DeleteMemory` (`MeshDb.cs:648-660`), `TryApplyMemoryUpsert`/`TryApplyMemoryDelete` (`MeshDb.cs:663-710`) |
| `Circle`/`Contact` got a **partial** treatment: version/tombstone rows exist, but the authoritative data still round-trips through the same whole-blob `SaveProfile` inside `SaveProfileAndSyncState` | `MeshDb.cs:798-850`: `SaveProfileAndSyncState` calls `SaveProfile(profileCommand, profile)` (the whole blob) *and* writes `sync_versions`/`sync_tombstones` rows in the same transaction |
| Knowledge/Skill/Widget have **zero** sync identity today | `ProfileSyncState.Snapshot()` only projects Circles and Contacts (`ProfileSyncProjection(Circles, Contacts)`); `DeviceSyncKinds` has no knowledge/skill/widget constants; `TryGetDeleteIdentity`/`TryGetUpsertKey` only switch on Contact/Circle/Memory kinds (`MeshDb.cs:861-894`) |
| Circle rename/delete already cascades into Knowledge/Skill/Widget `Visibility` in memory | `ProfileSyncState.RewriteVisibilities` (`ProfileSyncState.cs`): `foreach (var item in profile.Knowledge) item.Visibility = Rewrite(...)`, same for `Skills`, `Widgets` |

Net effect: any Contact/Circle/Memory change rewrites the entire Knowledge/Skill/Widget
corpus to disk as a side effect (write amplification), and a Knowledge/Skill/Widget change
itself carries no version stamp, so it has no defined conflict-resolution behavior across
devices beyond whatever a later full blob write or full-snapshot resync happens to overwrite.

## 2. Precedent: copy Memory, not Circle/Contact

Circle/Contact are natural-key entities (name, handle) that can be renamed, which is why
`ProfileSyncState` carries dedicated projection/rename-lineage machinery
(`CircleEntityId`, `sync_circle_renames`, `DeviceSyncCircleRename`). Knowledge/Skill/Widget
already have a stable, immutable GUID `Id` (`Guid.NewGuid().ToString("n")` set once at
creation) and are never renamed by identity, only edited in place. They need none of that
machinery; they need exactly what `MemoryItem` has: a real table, keyed by `Id`, with its
own `TryApply*Upsert`/`TryApply*Delete` pair. This is also why `ProfileSyncProjection`
does not need Knowledge/Skill/Widget added to it (Memory isn't in there either, and
doesn't need to be).

## 3. Goals / non-goals

**Goals**
- O(1) persistence for a Knowledge/Skill/Widget change (single-row write), not O(profile size).
- Real per-item last-write-wins conflict resolution with tombstoned deletes, matching Memory exactly.
- Ride the existing encrypted transport unchanged: same `DeviceSyncOperation` shape, same `device_envelope_outbox`, same `MessageCrypto` (ECIES-P256-AESGCM per recipient device key). No new wire mechanism, no relay change.
- Zero behavior change for every existing reader of `profile.Knowledge`/`Skills`/`Widgets` (agent tool context, Community/service publishing, marketplace import, sandboxed service scoping). They keep reading the same in-memory `List<T>`, just hydrated from a different source.
- Because there are 0 external users, this is a clean, one-way cutover: no dual-write period, no wire back-compat shim.

**Non-goals**
- Not fixing the protocol 7 / iPhone-iPad connectivity gap itself. This removes one structural contributor (Knowledge/Skill/Widget having no sync identity), it does not touch `SaveProfileAndSyncState`'s current all-or-nothing "rollback whole batch on first stale version" behavior, which is a separate, already-flagged concern.
- Not changing SQLCipher, the master-key/secure-enclave model, or device signing/recovery keys.
- Not changing `CapabilityAudience`/visibility semantics or public-service sandboxing rules.

## 4. Data model changes

### 4.1 New tables (`Services/MeshDb.cs`, alongside the existing `memories` table)

```sql
CREATE TABLE IF NOT EXISTS knowledge_items(
    id TEXT PRIMARY KEY,
    title TEXT NOT NULL,
    content TEXT NOT NULL,
    visibility TEXT NOT NULL,
    source TEXT NOT NULL,
    source_ref TEXT,
    updated_at TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS ix_knowledge_updated ON knowledge_items(updated_at DESC, id);

CREATE TABLE IF NOT EXISTS skills(
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    description TEXT NOT NULL,
    instructions TEXT NOT NULL,
    visibility TEXT NOT NULL,
    enabled INTEGER NOT NULL DEFAULT 1,
    source_marketplace_id TEXT,
    source_skill_id TEXT,
    version TEXT,
    updated_at TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS ix_skills_updated ON skills(updated_at DESC, id);

CREATE TABLE IF NOT EXISTS widgets(
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    prompt TEXT NOT NULL,
    html TEXT NOT NULL,
    visibility TEXT NOT NULL,
    created_at TEXT NOT NULL,
    modified_at TEXT NOT NULL,
    previous_html TEXT,
    previous_prompt TEXT);
CREATE INDEX IF NOT EXISTS ix_widgets_modified ON widgets(modified_at DESC, id);
```

Bump `meta['schema_version']` from `'1'` to `'2'` (same `meta(k,v)` convention already used, `MeshDb.cs:238`).

### 4.2 Model change required

`Skill` currently has no timestamp. Add one (`Domain/Models.cs:406`):

```csharp
public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
```

`KnowledgeItem.UpdatedAt` and `Widget.CreatedAt`/`ModifiedAt` already exist and need no change.

## 5. Wire protocol additions (`Mesh.Shared/Contracts.cs`)

Six new `DeviceSyncKinds` constants, mirroring the existing naming:

```csharp
public const string KnowledgeUpsert = "knowledge.upsert";
public const string KnowledgeDelete = "knowledge.delete";
public const string SkillUpsert     = "skill.upsert";
public const string SkillDelete     = "skill.delete";
public const string WidgetUpsert    = "widget.upsert";
public const string WidgetDelete    = "widget.delete";
```

Three new flat DTOs, mirroring `DeviceSyncMemory`'s shape, used as the `Payload` string
inside `DeviceSyncOperation`:

```csharp
public sealed record DeviceSyncKnowledgeItem(
    string Id, string Title, string Content, string Visibility,
    string Source, string? SourceRef, DateTimeOffset UpdatedAt);

public sealed record DeviceSyncSkill(
    string Id, string Name, string Description, string Instructions,
    string Visibility, bool Enabled,
    string? SourceMarketplaceId, string? SourceSkillId, string? Version,
    DateTimeOffset UpdatedAt);

public sealed record DeviceSyncWidget(
    string Id, string Name, string Prompt, string Html, string Visibility,
    DateTimeOffset CreatedAt, DateTimeOffset ModifiedAt,
    string? PreviousHtml, string? PreviousPrompt);
```

No change to `DeviceSyncOperation`, `DeviceSyncBatch`, `DeviceSyncVersion`, or
`MessageCrypto`. These three DTOs simply become new legal shapes for the existing generic
`Payload` string field, exactly as `DeviceSyncMemory`/`DeviceSyncCircle`/`DeviceSyncContact`
already are. `Widget.Html` and `Skill.Instructions` travel inside this payload exactly like
`Memory.Content` does today, wrapped per-recipient-device by the same
`MessageCrypto.Encrypt` (ephemeral P-256 + ECDH-derived key-encryption-key wrapping a
random AES-256-GCM content key, per device public key already on file). No special-casing
needed; the relay still only ever sees ciphertext with an opaque `kind` tag it cannot
interpret.

`TryGetDeleteIdentity`/`TryGetUpsertKey` (`MeshDb.cs:861-894`) do **not** need new switch
arms. Those two helpers are only used by the generic, blob-rewriting
`SaveProfileAndSyncState` path (Circle/Contact). `MemoryItem`, and the new capability
types following it, use their own dedicated `TryApply*` methods that take the paired
kind as a plain parameter, exactly like `TryApplyMemoryUpsert(memory, versionKey, version,
deleteKind)` does today. `ProfileSyncState.Snapshot()`/`ProfileSyncProjection` also need no
change, for the same reason Memory isn't in there.

## 6. `MeshDb.cs` changes

### 6.1 Load / Save

`LoadProfile()`: after the blob deserializes, add alongside the existing `Memories` hydration:

```csharp
profile.Knowledge = LoadKnowledgeItems();
profile.Skills = LoadSkills();
profile.Widgets = LoadWidgets();
```

`SaveProfile(SqliteCommand, MeshProfile)`: add to the existing `node.Remove(...)` calls:

```csharp
node.Remove("knowledge");
node.Remove("skills");
node.Remove("widgets");
```

This is the same pattern already used for `conversations`/`ownChat`/`ownThreads`/`memories`,
deliberately not introducing `[JsonIgnore]` or any new convention.

### 6.2 New methods (one set per type, each a direct copy of the `MemoryItem` pattern)

Per type (`Knowledge`, `Skill`, `Widget`), add:
- `private List<T> Load{Type}s()`: `SELECT ... FROM {table} ORDER BY updated_at DESC, id` (mirrors `LoadMemories`, `MeshDb.cs:612`).
- `public void Upsert{Type}(T item)` / `public void Delete{Type}(string id)`: local-only, no version bookkeeping (mirrors `UpsertMemory(MemoryItem)`/`DeleteMemory(id)`, `MeshDb.cs:648-660`). Used for the one-time migration and any purely local/unsynced write.
- `internal bool TryApply{Type}Upsert(T item, string versionKey, string version, string deleteKind)` and `internal bool TryApply{Type}Delete(string id, string tombstoneKind, string version, string upsertKey)`: the real entry points. Each is a line-for-line copy of `TryApplyMemoryUpsert`/`TryApplyMemoryDelete` (`MeshDb.cs:663-710`): open a transaction, compute `Newest(GetSyncVersion(...), GetSyncTombstoneVersion(...))`, reject via `DeviceSyncVersion.IsNewer` if the incoming version is not strictly newer, otherwise upsert/delete the single row plus `UpsertSyncVersion`/`UpsertSyncTombstone`, commit. No blob touch, no `SaveProfile` call, ever.

Six new methods total (two per type), each roughly 20 lines, entirely mechanical, no new
algorithm to design.

## 7. Call-site migration

Today `Knowledge.razor`, `Skills.razor`, and `Widgets.razor` mutate `Profile.Knowledge`/
`Skills`/`Widgets` directly and call a plain `SaveProfile()` (one of the 10 raw
`SaveProfile(` call sites in `AppState.cs`, versus 6 `SaveProfileAndSyncState(` sites used
by Circle/Contact/Memory today).

Add six `AppState` methods (`UpsertKnowledgeItem`, `DeleteKnowledgeItem`, `UpsertSkill`,
`DeleteSkill`, `UpsertWidget`, `DeleteWidget`), each:

1. Mint `var version = DeviceSyncVersion.Create(DateTimeOffset.UtcNow, myDeviceId, Guid.NewGuid().ToString("n"));`
2. Call the matching `Db.TryApply{Type}Upsert/Delete(...)`.
3. Update the in-memory `Profile.Knowledge`/`Skills`/`Widgets` list to match (so the UI's bound collection stays correct without a full profile reload).
4. Broadcast a `DeviceSyncOperation` (`Kind` = the new constant, `EntityId` = item id, `Version` = the minted version, `Payload` = the serialized DTO) to every linked device, through the same per-linked-device operation broadcast that Circle/Contact/Memory changes already use today (queues into `device_envelope_outbox`, encrypted at send time via `MessageCrypto.Encrypt`, same as every other envelope kind).

Then update the three Razor pages to call these six methods instead of mutating the list
and calling `SaveProfile()` directly.

## 8. Circle-rename cascade (the one non-mechanical integration point)

`ProfileSyncState.RewriteVisibilities` still mutates `item.Visibility` on Knowledge/Skill/
Widget in memory when a circle is renamed or deleted (unchanged, `ProfileSyncState.cs`).
Today that mutation implicitly rides along inside whichever `SaveProfileAndSyncState` call
handles the circle rename, because the blob rewrite included it "for free." After this
change it will not, since these types no longer live in the blob. The circle rename/delete
call sites (`RenameCircleReferences`, `DeleteCircleReferences` callers) need to, after
calling `RewriteVisibilities`, loop the affected Knowledge/Skill/Widget items and call the
new `TryApply*Upsert` for each one (minting a fresh version, broadcasting it). This is more
correct than today's behavior, not less: today a circle rename's cascading visibility change
to these items has no version and no independent broadcast either; it only reaches a sibling
device via that device's next full blob overwrite or full-snapshot resync.

## 9. Security model retention (explicit mapping)

| Property | Status after this change |
|---|---|
| Whole-file-at-rest encryption (SQLCipher, 256-bit key from platform secure enclave) | Unchanged. New tables are rows in the same encrypted `.db` file. No change to `Open()`/key derivation. |
| Relay never sees plaintext | Unchanged. New operations ride the existing `device_envelope_outbox` -> `MessageCrypto.Encrypt` (ECIES-P256-AESGCM, per recipient device key) -> relay-sealed envelope path. The relay sees the same opaque `kind` tag (`device.sync.operation`) it already sees for Circle/Contact/Memory changes; nothing new is relay-visible. |
| Device signing/recovery private keys never exported | Untouched. Nothing in this plan touches key generation, storage, or signing. |
| Widget HTML / Skill instructions never enter diagnostics | Unchanged behavior (`RuntimeDiagnostics` records lifecycle/exception metadata only). Add an explicit non-regression assertion (see Test plan) since this is now carried inside a new payload shape. |
| `CapabilityAudience`/visibility-based access control | Unchanged semantics. `Visibility` remains a plain string column, parsed by the same `CapabilityAudience.Parse` the agent/service-scoping code already calls. A published-public item is still only reachable through the sandboxed service-agent path (`SystemCircles.PublicVisibility`); that check does not depend on where the row physically lives. |
| Public service sandboxing, marketplace import, agent tool context | Zero code changes required. All of it reads `profile.Knowledge`/`Skills`/`Widgets` as an in-memory `List<T>`, which is still populated the same way after `LoadProfile()`, just sourced from dedicated tables instead of the blob. |

## 10. Scalability analysis

**Before**: any profile write (including unrelated ones, e.g. a Contact's `Muted` flag)
re-serializes and rewrites every Knowledge/Skill/Widget's full content (HTML, instructions,
body text) in the same transaction. Cost is O(total capability corpus size) per write,
regardless of what changed.

**After**: a Knowledge/Skill/Widget change is a single-row `INSERT ... ON CONFLICT UPDATE`
or `DELETE`, O(size of that one item). A Contact/Circle/Memory change (or any other blob
field) no longer touches capability content at all, since it is excluded from the blob.

Secondary benefits: smaller `profile` row means less WAL/journal churn on unrelated writes;
`knowledge_items.content`/`skills.instructions`/`widgets.html` become directly queryable
(`LIKE`, or FTS5 later) without deserializing the whole blob first, useful for a future
"search my knowledge base" feature.

## 11. Migration plan (one-time, 0-user cutover)

Since there are no external users, no dual-write period or wire back-compat shim is needed.
There is exactly one real dataset to migrate: your own local database file(s). Gate a
one-time step on `meta['schema_version']`, following the same idempotent-migration
convention already used for `chat_lines`/`own_chat` (`AddColumnIfMissing`, referenced at
the top of `MeshDb.cs`):

1. Run `CreateSchema()` (idempotent `CREATE TABLE IF NOT EXISTS`, safe to run every boot).
2. If `meta['schema_version']` is `'1'`: read the raw `profile.json` text directly (via
   `JsonNode`, not through the `MeshProfile` type), extract the `knowledge`/`skills`/
   `widgets` arrays if present, deserialize each element to `KnowledgeItem`/`Skill`/
   `Widget`, insert each as a row via the plain local `Upsert{Type}` (no version needed for
   a same-device migration of pre-existing local data; it is not a cross-device conflict),
   backfilling `Skill.UpdatedAt` with `DateTimeOffset.UtcNow` since it did not exist before.
3. Set `meta['schema_version'] = '2'`.
4. On the next `SaveProfile()`, the arrays are already gone from the blob (step 6.1), so
   nothing is written twice.

No data loss, runs once per database file, no flag or config needed.

## 12. Test plan

Existing tests to extend (real files in `tests/Mesh.App.Tests/`):
- `MemoryDbTests.cs`: use as the direct template for three new files, `KnowledgeDbTests.cs`,
  `SkillDbTests.cs`, `WidgetDbTests.cs` (or one combined `CapabilityDbTests.cs`). Cover:
  round-trip upsert then `LoadProfile()`; delete-after-upsert with an older version is
  rejected (tombstone does not regress); a delayed, older-versioned upsert replay after a
  newer delete is rejected (idempotent replay safety); two out-of-order upserts resolve
  deterministically via `DeviceSyncVersion.IsNewer`.
- `DeviceSyncProfileProtocolTests.cs`: add coverage for the three new `DeviceSyncKinds`
  pairs and the three new DTOs' JSON round-trip.
- `DeviceSyncProfileStateTests.cs`: confirm `RewriteVisibilities`/`Visibilities` behavior
  is unchanged (it operates on the in-memory list, which is unaffected).
- `RuntimeDiagnosticsTests.cs`: add an explicit assertion that no diagnostic entry ever
  contains a `content`/`instructions`/`html` field value, covering the new payload shapes.

New migration test: seed a pre-migration blob-only profile (arrays inside `profile.json`,
empty new tables), open the DB, assert rows appear in all three new tables, assert the
blob no longer contains those arrays after the next save, assert `schema_version = '2'`.

Manual end-to-end (your own linked devices, once built): add/edit/delete one Knowledge
item, one Skill, one Widget on one device; confirm the change lands on your other devices
through the same background-sync loop already carrying Contact/Memory changes today, no
new code path to separately validate.

## 13. Explicitly out of scope, flagged for awareness only

- `SaveProfileAndSyncState` currently rolls back the **entire batch** if any single
  operation in it is stale (`MeshDb.cs:798-850`, first failing version triggers
  `transaction.Rollback(); return false;` for the whole call). This is unrelated to this
  spec (Knowledge/Skill/Widget bypass this function entirely under this design), but is a
  plausible contributor to the asymmetric device-sync gaps already reported separately.
  Worth its own investigation, not folded into this change.
- Recommend verifying (and if missing, adding) an explicit "unrecognized `DeviceSyncOperation.Kind`"
  branch in the inbound dispatch path that still acknowledges/clears the sender's outbox
  entry but logs it as received-but-unrecognized, rather than silently dropping it. This
  matters specifically for this rollout: an old build and a new build of your own client
  will briefly coexist across your devices, and per the standing requirement that device
  sync stay deterministic and auditable with no ghost messages, an old device seeing a new
  `knowledge.upsert` kind for the first time should be distinguishable from a lost message,
  not indistinguishable from one.

## 14. Rollout checklist

1. Add `Skill.UpdatedAt`; add the three tables and index statements; bump `schema_version` to `'2'`.
2. Add the six `DeviceSyncKinds` constants and three DTOs in `Mesh.Shared/Contracts.cs`.
3. Add `Load{Type}s`/`Upsert{Type}`/`Delete{Type}`/`TryApply{Type}Upsert`/`TryApply{Type}Delete` (18 methods total across three types) in `MeshDb.cs`, copying the `MemoryItem` methods.
4. Wire `LoadProfile()` hydration and `SaveProfile()` blob exclusion for all three types.
5. Add the one-time migration step gated on `schema_version`.
6. Add the six `AppState` methods (mint version, apply locally, update in-memory list, broadcast) and switch the three Razor pages to call them.
7. Update the circle-rename/delete cascade call sites to re-apply (with a fresh version and broadcast) any Knowledge/Skill/Widget item whose `Visibility` `RewriteVisibilities` changed.
8. Verify (or add) the full-snapshot builder (`device_sync_snapshot_manifests`/`chunks`) includes a synthetic upsert operation per current Knowledge/Skill/Widget row for new-device bootstrap, the same way it presumably already does for Circle/Contact/Memory. Not yet directly verified; treat as an open check before shipping.
9. Add/extend the four test files in Section 12.
10. Manual two-device verification pass.
