namespace K9Crush.Modules.Admin.Domain.Events;

/// <summary>
/// ADR-031 event-sourcing convention: one record per Tip transition,
/// matching the entity's own domain methods 1:1 - see
/// FeedbackInboxItemEvents.cs (this module's other aggregate) for the
/// naming/location convention this follows.
///
/// Only the emlang yaml's ManagingTipsHealthContent chapter's "Create Tip"
/// -> "Tip Drafted" pair is modeled here. The chapter's four later
/// transitions (Tip Previewed / Tip Published / Tip Edited / Tip
/// Unpublished) are deliberately out of scope for this slice and get their
/// own records when those slices are built - not stubbed out ahead of time.
///
/// The yaml declares no `props` for either "Create Tip" or "Tip Drafted"
/// (unlike, say, HandlingGeneralFeedbackSupport's "Feedback Inbox", which
/// carries feedbackId/ownerId/message/status), so this event carries no
/// content fields - no title/body/category has been invented here. It
/// carries only the stream identity and the occurrence timestamp, the two
/// structural fields every event in this codebase has.
/// </summary>
public sealed record TipDraftedV1(Guid TipId, DateTimeOffset DraftedAt);
