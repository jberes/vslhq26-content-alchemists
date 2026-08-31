# Castmill session changes - 2026-08-31

This document records the product, interaction, API, persistence, test, and deployment work completed in the campaign-sharing, image-safety, UI-polish, and Wire session. It covers both the follow-up commits already pushed after the 2026-08-30 release and the final working tree prepared for this release. Credentials, connection strings, token values, and local secret configuration are intentionally omitted.

## Campaign collaboration

- Added campaign sharing without weakening tenant isolation.
- Added individual email grants that become active when the invited address signs in.
- Added optional domain-wide campaign access controlled by the campaign owner.
- Added a tenant-scoped `CampaignCollaborator` model and a unique campaign/email grant constraint.
- Added migration `20260830214234_CampaignSharing` for collaborators and campaign share-domain state.
- Added owner-only API operations to read and update sharing, add collaborators, and revoke collaborators.
- Kept collaborator lookup neutral for unavailable addresses so the sharing API does not disclose account existence.
- Extended campaign list access so explicitly invited users and eligible domain users can see shared campaigns.
- Extended Brand read access only as needed for a collaborator to render and work in an accessible shared campaign.
- Kept ownership-only mutations, unrelated campaigns, tenant settings, and other tenant data private.
- Serialized campaign-sharing writes so concurrent grant, revoke, and domain changes cannot leave partial state.
- Added cross-tenant API tests for email grants, domain access, revocation, authorization, and isolation.
- Added UI tests for opening sharing, changing domain access, adding an email, and removing a collaborator.
- Appended backend ADR-053 and frontend ADR-F53 for the campaign collaboration contract.
- Shipped the initial campaign collaboration implementation in commit `827120a`.

## Campaign header and sharing polish

- Replaced the permanent text Rename control with a compact pencil icon beside the campaign title.
- Added an explicit accessible name and tooltip to the rename icon.
- Added a real Cancel path for inline renaming.
- Restored the persisted campaign name when rename is cancelled or Escape is pressed.
- Moved owner-only sharing into a compact share icon beside Rename.
- Kept the collaborator-facing `Shared campaign` indicator in the status row.
- Reworked the sharing dialog email row so the input and Add action stay aligned.
- Clarified the empty collaborator state as `No people have been added yet.`
- Moved sharing help text below the email row so it no longer distorts control alignment.
- Standardized the sharing close control and collaborator removal icon.
- Updated campaign rename synchronization and sharing component tests for the revised controls.

## Shared icon and horizontal-scroll controls

- Added `AppIcon.razor` as the shared outline-icon surface for pencil, share, trash, and calendar actions.
- Replaced emoji and multiplication-glyph delete controls in the workspace campaign list, Brand list, Brand editor, Focus artifact list, Mill Floor cards, and campaign sharing list.
- Preserved descriptive accessible names on every icon-only control.
- Added `HorizontalScroller.razor` and `castmill-scroll.js` for measured horizontal overflow.
- Added back/forward controls that enable only when content exists beyond that edge.
- Added resize and scroll observation so control state updates without layout polling.
- Applied the shared scroller to Mill Floor's `Print more from this source` content-type strip.
- Registered and disposed the JS module through the shared UI service boundary.

## Status styling and content hierarchy

- Centralized person-facing status labels in `ArtifactDisplay`.
- Displayed queued artifacts as `Reviewed` rather than exposing the internal queue state.
- Added status-edge modifiers for Draft, In review, Reviewed, and Published artifacts.
- Applied status edges to Focus navigation items and content cards so state is not communicated by text or color alone.
- Aligned cluster-map status labels with the same vocabulary.
- Added distinct draft, review, approved, and published tones to cluster nodes.
- Added semantic status, heading, schedule, pipeline, overlay, and interaction tokens.
- Added the corresponding values to both Warm Editorial and Industry Blueprint theme families in light and dark modes.
- Kept feature CSS on semantic tokens rather than family-specific colors.

## Front page and Home scheduling visibility

- Loaded upcoming schedule entries for all dashboard artifacts rather than only the six entries rendered in the seven-day Wire preview.
- Indexed schedule entries by artifact ID for stable dashboard lookup.
- Replaced a reviewed artifact's status label with its actual scheduled date and local time when it is on The Wire.
- Preserved the ordinary Draft, In review, Reviewed, and Published label when no schedule exists.
- Kept the compact seven-day Wire preview limited to six chronological entries.
- Added dashboard tests for scheduled artifact labels and schedule lookup behavior.

## Focus mode

- Added a Schedule action for reviewed artifacts directly in the document header.
- Added a future date/time picker and explicit `Schedule to The Wire` confirmation.
- Created schedule entries through the existing schedule client with local-to-UTC conversion.
- Loaded an artifact's existing schedule when Focus opens it.
- Changed the visible header state from Reviewed to Scheduled when an entry exists.
- Displayed the persisted scheduled day and time when the Schedule control is reopened.
- Added success and API-unreachable feedback for scheduling.
- Added a calendar icon and scheduled-state styling without adding a second scheduling system.
- Kept Send to review, Mark reviewed, Back to draft, Copy, Download, and GitHub publishing in one stable action area.
- Preserved the accessible Download disclosure for Markdown, Word, and whole-campaign ZIP exports.
- Added status edges to the left artifact navigator.
- Moved the document outline to a dedicated right-side `On this page` rail.
- Added heading-level indentation, title tooltips, stable markers, and a bounded scroll area to the outline.
- Kept the document and Producer panes independent from the outline's width.
- Added support for `?generate=true` when Focus opens a newly created placeholder.
- Removed the one-shot generation query flag from browser history before starting generation.
- Differentiated `Generating` a placeholder from `Regenerating` an existing artifact.
- Passed the approved SEO/AEO seed direction into the selected artifact kind instead of describing every request as a pillar.
- Updated Focus, artifact-tree, export, navigation, and status regression tests.

## SEO/AEO cluster generation

- Changed `Add` on a missing SEO/AEO cluster node from a long blocking generation call to immediate placeholder creation.
- Preserved the selected cluster angle in placeholder content.
- Linked supporting placeholders to the existing blog pillar when appropriate.
- Navigated immediately to the exact placeholder in Focus.
- Let Focus own the generation request so the user sees real progress in the editing surface.
- Added focused cluster-interaction tests for placeholder creation, parent selection, navigation, and generation handoff.

## Mill Floor and provenance

- Applied the shared horizontal scroller to the full content-type creation strip.
- Moved card hover ownership to the list item so action controls and card gaps do not break provenance highlighting.
- Avoided redundant hover updates when the same artifact remains active.
- Added optional citation auto-scroll when a user hovers a Mill Floor artifact.
- Scrolled the first cited transcript/evidence row into view without stealing keyboard focus.
- Deduplicated citation IDs before geometry and highlight work.
- Added redraw versioning so a late JS measurement cannot overwrite a newer hover selection.
- Reset auto-scroll tracking when no artifact is active.
- Redrew after first render and explicit geometry changes so overlay lines appear against current layout.
- Added `scrollCitationIntoView` to the provenance JS module.
- Preserved pin/click behavior independently from transient hover behavior.
- Updated Mill Floor, source-evidence, content-cluster, and provenance-related tests.

## Image generation composition safety

- Added final target width, target height, and reduced aspect ratio to every image-generation path.
- Appended final composition instructions after Brand, campaign, keyword, and steering text so earlier guidance cannot override crop safety.
- Reserved the middle 76 percent of the frame for all essential content.
- Required at least 12 percent clear space at each edge for words, logos, subjects, labels, and supporting graphics.
- Prohibited partial cards, clipped rows, cut-off panels, content continuing below the canvas, and edge-dependent square compositions.
- Limited text blocks to a small number of short lines and a bounded share of image height.
- Required complete glyphs and instructed the model to omit secondary copy instead of shrinking or clipping it.
- Kept keyword guidance conditional and prohibited invented keyword lists or unrelated marketing copy.
- Applied the guardrails to initial generation and steered variation requests.
- Updated API prompt-composition tests and recorded visual/functional QA in `design-qa.md`.

## Image Studio and variant locks

- Reorganized the full-size image lightbox around an image canvas with a fixed top action toolbar.
- Renamed the primary placement action to `Use this image` to describe the result rather than the storage operation.
- Kept Download, Discard/Restore, Delete forever, Lock/Unlock, and Close reachable without scrolling below the preview.
- Separated Close from destructive controls and preserved Escape behavior.
- Removed the old `Keeper` state treatment from gallery ordering and presentation.
- Added an explicit lock badge to protected gallery takes.
- Added `LockedByUserId` and `LockedAt` to image variants.
- Added migration `20260831010942_ImageVariantLocks` and its model snapshot update.
- Added rate-limited lock and unlock endpoints.
- Made repeated locking by the same user idempotent.
- Rejected locking an image already protected by another collaborator.
- Allowed only the lock owner or campaign owner to unlock a protected take.
- Rejected permanent deletion of a locked take at the API boundary.
- Returned `IsLocked`, `CanUnlock`, and `LockedAt` in the image variant contract.
- Added lock/unlock operations to `ImagesClient`.
- Disabled Delete forever while a take is locked.
- Updated open-lightbox and gallery state in place after lock changes.
- Added API tests for lock ownership, conflict, owner override, deletion protection, and tenant isolation.
- Added gallery and lightbox tests for lock badges, disabled destructive actions, and unlock controls.

## The Wire: approved product model

- Replaced the rejected fixed-height week/month grid with the approved three-projection Wire design from `docs/design/the-wire/SPEC-the-wire.md`.
- Kept The Wire at workspace scope rather than adding it to campaign tabs.
- Implemented Run of Show as the default answer to `What goes out, and when?`
- Implemented Pipeline as the monitoring answer to `What is stuck?`
- Implemented Agenda as the dense chronological view and narrow-width fallback.
- Kept all three views as projections over one loaded `WireBoardData` object.
- Preserved view changes without refetching dashboard, schedule, or broker readiness data.
- Added Week and Fortnight ranges for Run of Show and Agenda.
- Added previous/next range navigation with UTC query bounds derived from the local workspace timezone.
- Forced Agenda below the legibility breakpoint and disabled Run of Show while narrow.
- Kept metrics out of all approved Wire UI.

## The Wire: Run of Show

- Added a fixed 288 px ready-to-schedule queue and a fluid `min-width: 0` timeline.
- Added queue cards with two-line title clamps and a fixed Edit/Slot action column.
- Revealed actions on hover and `focus-within` for mouse and keyboard parity.
- Added the 06:00-22:00 spatial time ruler.
- Positioned scheduled cards from local time instead of treating time as text only.
- Added day gutters with today, sent, queued, and error summaries.
- Collapsed empty days to one compact drop row.
- Collapsed weekends without posting windows to one compact row.
- Added 15-minute schedule snapping.
- Clamped out-of-window posts to the lane edge while preserving their true time label.
- Added overlap stacking for items within 90 minutes and grew the day lane instead of overlapping cards.
- Added queued/staged, sent, blocked, and error card treatments.
- Added Retry for failed items and permalink delivery facts for sent items.
- Added a persistent no-broker banner and staged-local messaging without blocking scheduling.

## The Wire: keyboard scheduling and drag/drop

- Built the keyboard path first through a Slot/Move dialog.
- Used Ignite UI date and date-time controls available in the pinned Lite package.
- Added future local date/time selection with DST ambiguity rejection.
- Added queue-to-schedule creation and existing-entry movement.
- Added drag from the ready queue onto a Run of Show day/time lane.
- Calculated drop time from lane-relative pointer geometry.
- Added a Slot-dialog fallback when the browser does not provide a usable pointer coordinate.
- Added drag target feedback and no-op detection for dropping an item on its current slot.
- Removed a newly scheduled artifact from the queue immediately.
- Added broker-aware success messages for sent-to-broker versus staged-locally behavior.
- Kept Edit available for every queue and scheduled item.

## The Wire: Agenda and Pipeline

- Added fixed-width Agenda time, channel, status, and action columns.
- Added single-line clamping for Agenda titles.
- Added hover/focus action reveal without placing controls in title flow.
- Kept empty Agenda days as compact valid drop targets.
- Added the ready queue as a side dock on wide layouts and a compact unscheduled bar on narrow layouts.
- Added four Pipeline columns: Ready, Staged/Queued, Sent, and Needs attention.
- Added per-column counts, status bars, date chips, blocked reasons, and resolving actions.
- Added campaign-only filtering without another server query.
- Added drag-based state transitions using existing schedule operations.
- Required date/time selection when moving Ready work into Staged/Queued.
- Kept Sent immutable and excluded it as a drop target.
- Added direct unschedule and retry paths where the API supports them.

## The Wire: API and persistence contract

- Added `SentAtUtc`, `Permalink`, and `MetricsJson` to schedule entries.
- Added migration `20260831042547_WireSentDeliveryContract` and updated the EF model snapshot.
- Extended broker post responses with optional sent time and permalink delivery facts.
- Persisted sent time and permalink when broker reconciliation reports successful delivery.
- Cleared sent facts and metrics when a schedule entry is reset or moved back out of Sent.
- Added a structured nullable metrics DTO with nullable reach, engagement, open-rate, and completion-rate values.
- Kept missing metrics as `null`, never zero, and did not render an empty metrics surface.
- Added safe metrics deserialization for future providers while treating invalid or absent provider data as unavailable.
- Added UTC/local conversion, status mapping, channel mapping, title fallback, and queue filtering in `WireBoardMapper`.
- Added sent-contract and reconciliation API tests.

## The Wire: implementation files

- Rebuilt `Pages/Wire.razor` as the route-level query and interaction owner.
- Added `WireModels.cs` for the projection model, mapper, time conversion, snapping, and overlap levels.
- Added `RunOfShowView.razor` for queue, ruler, days, spatial cards, and drag scheduling.
- Added `AgendaView.razor` for chronological rows and responsive queue presentation.
- Added `PipelineView.razor` and `PipelineCard.razor` for status monitoring and transitions.
- Added `QueueRail.razor` and `QueueCard.razor` for the shared ready queue.
- Added `SlotDialog.razor` for the keyboard-equivalent date/time path.
- Added `castmill-wire.js` for lane-relative pointer geometry and viewport-width observation.
- Cache-busted the Wire module import after its responsive behavior changed.
- Added the complete approved design handoff under `docs/design/the-wire/`.

## ApexTree and cluster rendering follow-ups

- Restored a readable cluster-tree viewport after the first fit-to-content attempt made the tree too small.
- Removed the no-upscale fit rule that over-shrank ordinary reports.
- Matched the implementation height to the approved reference while preserving contained overflow.
- Updated bundle tests for the revised ApexTree behavior.
- Shipped these corrections in commits `422a154` and `c7fa3ed`.

## App Service deployment follow-up

- Prevented the deployment script from reapplying startup tracking when the desired startup state already matches the live App Service.
- Avoided an unnecessary settings write and restart during deployment.
- Shipped the deployment-script correction in commit `fc76beb`.

## CSS, interaction, and responsive work

- Added the complete Run of Show, Agenda, Pipeline, queue, slot-dialog, Focus schedule, document-outline, image-lock, and lightbox-toolbar styles.
- Added stable dimensions and clamp rules so titles and controls cannot resize rows.
- Added narrow-width Agenda forcing based on viewport width rather than an inner content box.
- Added keyboard focus rings and `focus-within` action visibility across new controls.
- Added error, review, staged, sent, and blocked state encoding with bars plus labels.
- Kept the approved radius-zero Wire language and existing compact Castmill layout grammar.
- Preserved both theme families and both light/dark modes through semantic tokens.
- Avoided indeterminate spinners by using skeleton rows or explicit busy labels.

## Final review hardening

- Added an HTTP/HTTPS allowlist for both broker permalinks and broker IDs before either can become a live-post link.
- Cleared sent timestamps, permalinks, and metrics whenever broker reconciliation moves an entry out of Sent.
- Made malformed optional metrics JSON degrade to unavailable instead of failing the schedule endpoint.
- Made image lock acquisition a conditional database update so concurrent collaborators cannot overwrite each other's ownership.
- Made unlock a conditional database update limited to the lock owner or campaign owner.
- Made permanent deletion conditional on the take still being unlocked at the SQL delete itself.
- Added concurrent lock and lock-versus-delete tests that require exactly one database winner.
- Routed Focus scheduling through the same DST-aware local-to-UTC conversion used by The Wire.
- Rejected ambiguous Focus wall times and all non-future Focus or Wire schedule instants.
- Forced Agenda when narrow from Pipeline as well as Run of Show, and disabled both spatial view choices at that width.
- Added explicit `aria-expanded` and `aria-controls` state to the compact unscheduled queue disclosure.

## Test changes

- Expanded `WirePageTests` to cover all three projections, one-load view switching, range changes, narrow fallback, queue state, broker state, schedule creation, moves, DST ambiguity, overlap stacking, title clamping, and sent delivery facts.
- Added `wire-workflow.spec.js` for real-browser keyboard scheduling, drag/drop, Run of Show geometry, Pipeline transitions, Agenda rendering, no-refetch switching, and cleanup.
- Added a stable 1440 px Run of Show visual baseline for macOS Chromium.
- Expanded `ScheduleAndRevisionTests` for sent timestamps, permalinks, nullable metrics, reconciliation, and reset behavior.
- Expanded `ImageVariantTests` for image prompt composition and variant-lock authorization.
- Expanded Image Studio gallery and lightbox tests for the revised toolbar and lock states.
- Added `SeoClusterInteractionTests` for immediate placeholder creation and Focus generation handoff.
- Updated artifact tree, campaign rename, campaign sharing, chrome affordance, content cluster, dashboard, Mill Floor, and source-evidence tests for the revised interactions.
- Updated webpage-import, Image Studio, media/voice, navigation, and Wire E2E login helpers to wait for development credential prefill before replacing values.
- Replaced stale emoji assertions with stable accessible-name or shared-SVG assertions.
- Updated Playwright's API web-server command to launch the dual HTTP/HTTPS profile required by the WebAssembly development API address.
- Updated editor bundle-budget expectations for the approved ApexTree and editor output.

## Validation completed before commit

- Recreated a disposable SQL Server 2022 database and applied every EF migration, including both new migrations.
- Ran the complete Playwright suite against the clean database: 6 passed, 1 intentionally skipped, 0 failed.
- Verified the signed-out entry point, webpage import, Image Studio, uploaded media and voice flow, Mill Floor navigation, and the full Wire workflow.
- Ran the editor bundle build successfully.
- Ran all editor tests: 57 passed, 0 failed.
- Ran `dotnet test Castmill.NoDesktop.slnf` before final review: 799 passed, 0 failed, 0 skipped.
- Ran `dotnet test Castmill.NoDesktop.slnf` after final review fixes: 802 passed, 0 failed, 0 skipped.
- Built `Castmill.Web` successfully with no reported errors.
- Cleaned and rebuilt `Castmill.Desktop` for `net10.0-maccatalyst` successfully with no reported errors.
- Compared all 38 packaged `Castmill.UI` static assets to their RCL sources: 0 missing and 0 mismatched.
- Explicitly verified packaged `css/views.css` and `js/castmill-wire.js` match their sources.
- Launched the Mac Catalyst app from the fresh bundle against the disposable local API.
- Verified the live Catalyst process established the expected HTTPS API connection.
- Repeated the complete Playwright suite after final review fixes: 6 passed, 1 intentionally skipped, 0 failed.
- Rebuilt both UI shells after final review fixes and repeated the 38-file Catalyst asset comparison with 0 missing and 0 mismatched.
- Relaunched the post-review Catalyst bundle and verified its live HTTPS API connection.
- Stopped the temporary Catalyst app and local API after validation.

## Release state at document creation

- The feature and regression gates above are complete.
- The disposable local SQL container remains temporary and is not part of the release.
- This document must be committed with the source, migrations, tests, E2E baseline, and approved Wire design handoff.
- Git diff, whitespace, secret scanning, Azure preflight, push, migration application, deployment, and production smoke verification follow this document and should be recorded below when complete.

## Deployment result

- Pending at document creation.
