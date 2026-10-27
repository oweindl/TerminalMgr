# Proposal: AI subscriptions below the server list

Status: approved design; initial implementation is documented in [ai-subscriptions.md](ai-subscriptions.md). The interactive concept continues to use illustrative data.
Requested: 27 October 2026.
Placement: lower section of the LEFT server column, directly below the server list.
All accounts and numbers in the accompanying interactive concept are illustrative.

## Behaviour and layout

- The selected machine determines the source. Switching hosts changes the displayed accounts; old values must never appear under the new host's name.
- Include a local "This PC" source alongside remote SSH profiles.
- Add a vertically resizable divider between the server list above and AI subscriptions below. Both areas scroll independently. The terminal keeps its full height. Persist split/collapsed state with WindowLayout.
- Compact revision: about 200–230 px for the heading and two provider summaries, clamped to leave at least two visible server rows. Collapse through its heading.
- Each account uses a compact header (provider, reported plan/authentication mode, one details chevron) and two inline quota rows: window label, tiny bar, used percentage and reset countdown. Account identity, update timestamp and additional fields move to Details; stale/error states must still be visibly flagged. No permanent action-button row.
- Details opens a modeless application detail window with all supported fields. This avoids expanding the sidebar indefinitely or resizing the SSH terminal. The mockup's inspector demonstrates the contents.
- Use existing theme resources and keyboard-accessible controls. Every bar explicitly says "used" or "remaining".
- Visual revision: match the Commander shell with its 56 px icon rail, 260 px server column, IBM Plex Sans / JetBrains Mono font families, dark Theme.cs surfaces, amber selection and thin separators. Provider summaries stay flat and compact, with the same subtle chevron hover and focus treatment as other application controls. The preview remains illustrative and does not query accounts.

"Subscriptions used on the machine" means accounts visible to the selected SSH user and configured CLI contexts. It cannot automatically inventory other operating-system users or arbitrary environment-specific credentials. Additional known contexts can be configured explicitly.

## All available information

Only show safe metadata actually supplied by supported interfaces. Missing means "Not supplied", never a guessed value or 0%.

| Group | Fields, when returned | Meaning |
| --- | --- | --- |
| Identity | Provider, signed-in state, authentication method, masked account/email, organization/workspace, reported plan | Installed CLI does not prove a paid subscription. |
| Source | Host, user, CLI context, CLI version, collection method, observation time, fetch time | Never expose contents of credential/configuration files. |
| Quotas | Every returned bucket, label/ID, consumed %, derived remaining %, duration, reset time/countdown, limit-reached state | Account-wide service allowance, not consumption caused by this server. Keep model buckets separate. |
| Credits | Workspace balance, earned reset credits/count/expiry | Separate units/types. Read-only; no buying or redeeming credits. |
| Activity | Codex lifetime/peak-daily token activity, daily buckets, streaks, duration summaries | Only if supported/returned. Not a bill or remaining subscription tokens. |
| Sessions | Model, session ID/label, context size/usage, token counts, duration/API duration, changes, cache metrics | Session-specific. Multiple Claude sessions get separate detail rows. |
| Estimates | Session cost estimate; gateway spend/limit/period | Session list-price estimates are not subscription charges. |
| Notices | Workspace notices, auth failures, unsupported versions, collector status | Bounded, sanitized display text. |
| Billing | Renewal date, price, invoices | Not supplied by these interfaces; require a future supported integration. |

Same account on several hosts: retain observation/source labels; never add percentages/balances. Deduplicate only with a trustworthy provider account ID or explicit mapping, not an assumed email match.

## Claude Code integration

1. Use documented claude auth status JSON in the selected context. Additional identity/plan fields are optional and version-dependent.
2. An opt-in status-line wrapper receives documented session JSON, writes an allowlisted normalized snapshot atomically, then delegates to the existing script. Preserve the user's current status line.
3. Keep separate snapshots per configured context/session in a bounded user-only state directory. Never include credentials/transcripts.
4. Read snapshots locally or over a separate SSH channel. Never run a paid model prompt merely to refresh usage.
5. Documented five-hour/seven-day fields depend on supported Pro/Max contexts and appear only after a response; windows may be absent. Show "Waiting for Claude activity" or "No quota supplied" accordingly.
6. Refresh rereads the snapshot; it cannot force a fresh provider observation without Claude activity. Installing/configuring the wrapper is an explicit setup action.

## Codex integration

Use documented Codex app-server in the same user/configuration context:

- Initialize handshake, then account/read for available identity/plan.
- account/rateLimits/read and supported account/rateLimits/updated notifications.
- Prefer rateLimitsByLimitId; fall back to the legacy single-bucket view.
- Feature-detect account/usage/read for account activity and daily buckets.
- Feature-detect account/workspaceMessages/read for safe display notices.
- Include credits/reset-credit metadata if returned. Missing methods are capability gaps, not a total integration failure.

Run a separate local process or non-PTY SSH channel. Override RemoteCommand and RequestTTY for that channel so it does not start tmux. Never inject status commands into a running terminal/conversation.

Label reported quota as the relevant Codex/service bucket, not a universal allowance for all ChatGPT website models.

## API-key accounts

Show "API billing" and reported auth state. Do not create subscription bars. API usage/cost reporting is a separate optional connector with its own configured credentials and organization permissions.

## Proposed components

- Core: AiAccountSnapshot, AiQuotaWindow, AiSessionSnapshot, AiCapability; nullable typed metrics with units/scope; normalization, identity, freshness/reset calculations.
- Windows: IAiAccountSource implementations for local/SSH snapshots and Codex app-server transport; cancellation, timeout, bounded structured payloads and error handling.
- App: MainWindow.Subscriptions.cs coordinator, AiSubscriptionsPanel below ProfilesList, AiAccountDetailsWindow, explicit setup/discovery and refresh.

Normalized fields:
sourceId, contextId, provider, accountId?, plan?, authMode, capabilities,
observedAtUtc, fetchedAtUtc, status,
quotaWindows[{id,label?,usedPercent?,windowMinutes?,resetsAtUtc?,scope}],
credits, activity?, sessions, notices, diagnostics.

Each observation retains its actual source timestamp or explicitly labelled collection time. Never transport access/refresh tokens, API keys, cookies or arbitrary authentication JSON into Windows.

## Refresh and lifecycle

- Discover on explicit setup and first panel opening for an authenticated source.
- Fetch on machine selection; immediately show cached data with its age.
- While visible, start with configurable 60-second quota refresh; obey provider backoff and prefer notifications. Snapshot reads can be cheaper/more frequent but do not make the underlying Claude observation newer.
- Cancel on selection changes/disconnect/shutdown. Use a generation key so late responses cannot overwrite another host.
- No login/first observation: helpful empty state, no empty usage bars.
- Display observation age; after a reported reset passes, mark "Awaiting update" rather than inventing 0%.
- Offline/provider errors retain the last successful snapshot and never disconnect the terminal.
- Cache sanitized data in memory for v1. Persisted caches must retain privacy/source boundaries.

## Implementation phases

1. WPF sidebar and detail window against fixtures; test resizing, small windows and host switching.
2. Codex account discovery/rate windows via existing CLI login.
3. Opt-in Claude status-line wrapper plus local/SSH transport, preserving current settings.
4. Optional activity, session/cache metrics, credits and notices where supported.
5. Manual validation on the user's selected machines; document capability gaps.

## Acceptance criteria

- Below server list, left side; resizable and collapsible.
- Show Claude and Codex when discovered for the selected user/context.
- All supported safe fields accessible in Details; missing, zero and unavailable remain distinct.
- Shared-account limits never summed; session context separately labelled.
- Deterministic host switching, stale/offline, expired auth and unsupported-version states.
- No changes to running tmux, model conversation, subscription or login.
- No credentials/transcripts in snapshots, cache, logs, exports or screenshots.
- Existing terminal, clipboard, dictation and navigation retain focus/layout behaviour.

## Official references (checked 27 October 2026)

- Claude auth: https://code.claude.com/docs/en/cli-reference
- Claude status-line schema: https://code.claude.com/docs/en/statusline
- Codex account, limits, activity and notices: https://developers.openai.com/codex/app-server/

Proposal only: no remote discovery, helper installation, login or subscription query has been run against the user's machines.
