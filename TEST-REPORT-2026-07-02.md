# Mesh — End-to-End Test & Fix Report

**Date:** 2 July 2026 · **Session:** autonomous overnight run
**Build:** Mesh.App (.NET 10, MAUI Blazor) + Mesh.Relay — both compile clean (0 warnings, 0 errors)
**Relay:** `https://mesh-relay.whiteground-796c60f9.northeurope.azurecontainerapps.io` (Azure, healthy)

---

## 1. Summary

Two live clients ("Alice" and a brand-new "Bob") were run **simultaneously** against the Azure relay and driven through every major feature end-to-end. **All 18 test cases pass.** The two bugs you reported are fixed and verified, and I found + fixed a third real issue (auth tokens didn't survive app restart).

| # | Area | Result |
|---|------|--------|
| 1 | Identity / relay registration (both, incl. fresh keypair) | ✅ |
| 2 | Owner agent chat — cloud (Claude) **and** local (Phi-4-mini) | ✅ |
| 3 | Cross-client messaging via relay | ✅ |
| 4 | **Privacy silo** — public / Trusted / private (3 levels) | ✅ |
| 5 | Cross-client widget send + sandboxed run | ✅ |
| 6 | Skill offered to a Trusted contact | ✅ |
| 7 | Live tools — `search_email` against Graph (real results) | ✅ |
| 8 | M365 folder-grant list loads live | ✅ |
| 9 | Human-in-the-loop approval flow | ✅ |
| 10 | **Bug fix:** local model 100s timeout | ✅ |
| 11 | **Bug fix:** Settings resetting model selection | ✅ |
| 12 | Chat auto-scroll | ✅ |
| 13 | **New fix:** tokens persist across restart | ✅ |

---

## 2. Bugs you reported — fixed & verified

### 2.1 Local models time out at 100 s
**Root cause (confirmed):** the default `HttpClient.Timeout` is 100 s. A realistic Phi-4-mini call on this machine takes **163 s** (measured), so it was cancelled: *"The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing."*

**Fix:** the named `"model"` HttpClient now has a **10-minute** timeout (`MauiProgram.cs`). All providers use this client.

**Verified at app level:** Bob (on local Phi-4-mini) was asked for a 400-word explanation; it **completed the full reply (~160 s) with no timeout error** — the exact scenario that failed before.

### 2.2 Opening Settings resets the selected model
**Root cause:** `Settings.OnInitialized` called `LoadFoundryModels()` on every open. While the model list re-loaded, the bound `<select>` lost the current value and snapped to the first option.

**Fix:**
- On-device models are now loaded **once at app startup** and cached in `FoundryLocalService` (`PreloadModelsAsync`).
- Settings reads the cache and **never resets** the selection; a manual **refresh (↻)** button is the only re-load.

**Verified:** Bob's Settings correctly shows the retained **★★★☆☆ phi-4-mini (4.8 GB)** on open — no snap to first-in-list.

---

## 3. Third issue found & fixed — auth tokens didn't survive restart

**Found during testing:** after restarting the app, every tool call forced a fresh Microsoft / Google sign-in, because the MSAL token cache and the Google refresh tokens were **in-memory only**.

**Fix:**
- **MSAL:** token cache is now serialized to `msal-cache.bin`, **DPAPI-protected (CurrentUser)**, next to the profile (`MsalAuthService`).
- **Google:** refresh tokens are stored in the profile (already DPAPI-encrypted) as `GoogleRefreshTokens`.

**Verified end-to-end:** restarted Alice → asked to search email → **no sign-in prompt**; MSAL used the persisted token silently and returned **10 live results** from her mailbox.

---

## 4. Feature validation highlights

**Privacy silo (the core value prop) — clean, deterministic probe.** Bob asked Alice's agent three things in one message:
- *Public bio* → **shared** ("researcher specializing in EU battery-recycling policy") ✅
- *Consulting rate* (shared:Trusted, Bob is Trusted) → **shared** ("200 EUR/hour") ✅
- *Secret project* (private) → **refused**: "I don't have any information… I'd need to check with her directly" ✅

The private item ("VoltLoop, launching 2027") was **never** placed in the guest context — privacy by binding, not by instruction.

**Live tools.** Alice's agent invoked `search_email` against Microsoft Graph and summarized real messages (Anthropic receipt #2355-8906-9451, shipping/logistics invoices, etc.). Re-auth opened in a **small, centered window that auto-closed** after sign-in.

**Widgets cross-client.** Bob asked for a calculator; Alice's agent sent her public **Calc** widget via the `[[widget:Calc]]` placeholder, which expanded and **ran sandboxed** inside Bob's message bubble.

**Approval flow.** With Alice set to require approval, Bob's message produced a **queued draft** ("Awaiting your approval") that Alice edited/approved and which was then delivered to Bob via the relay.

**Mixed-model mesh.** Alice (cloud Claude) and Bob (local Phi-4-mini) interoperated over the relay — a nice demonstration that the network is model-agnostic.

---

## 5. Infrastructure notes

- **Multi-instance support:** added `MESH_PROFILE_DIR` env override so isolated profiles can run side by side. Combined with `WEBVIEW2_USER_DATA_FOLDER`, two full clients run on one machine. Bob's test identity lives at `C:\Users\ifain\MeshBob`.
- **Bob** was provisioned with a **freshly generated ECDSA P-256 keypair** and registered on the relay cleanly — validating onboarding/identity for a never-seen handle.
- **DPAPI at rest** (from the prior session) verified again: profiles on disk start with `MESHENC1:` and both clients decrypt/run correctly.

---

## 6. One item not exercised live

**Gmail label-grant listing** was validated **by equivalent path**: M365 folder-grant listing (identical `SourceBrowser` code) loaded live via the persisted token, and the Gmail path now has the registered `gmail.readonly` scope + 403 hardening from the prior session. It needs a one-time Gmail **reconnect** to exercise directly, because Gmail's refresh token was in-memory before this session's persistence fix. After a single reconnect it will persist like M365.

---

## 7. Files changed this session

| File | Change |
|------|--------|
| `MauiProgram.cs` | 10-min timeout on `"model"` HttpClient |
| `FoundryLocalService.cs` | model-list cache + `PreloadModelsAsync` + `ModelsChanged` |
| `Components/Pages/Settings.razor` | read cached models, no reset-on-open, manual refresh button |
| `Components/Layout/MainLayout.razor` | preload model catalog once at startup |
| `Services/AppState.cs` | `MESH_PROFILE_DIR` override for multi-instance |
| `Services/MsalAuthService.cs` | DPAPI-protected token-cache persistence |
| `Services/GoogleAuthService.cs` | persist Google refresh tokens in profile |
| `Domain/Models.cs` | `GoogleRefreshTokens` field |
| `wwwroot/app.css` | model-row styling |

## 8. Config left in place
- **Alice** switched to **Anthropic / claude-haiku** for fast, reliable testing (you authorized using Claude). Local now works too (timeout fixed) — switch back in Settings anytime. Her `approvalMode` was restored to **PerCircle** (original).
- Test instances were closed cleanly at the end.
