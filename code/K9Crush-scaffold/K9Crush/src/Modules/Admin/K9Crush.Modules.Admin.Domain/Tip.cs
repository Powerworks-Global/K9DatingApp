using System.Text.Json.Serialization;
using K9Crush.BuildingBlocks.Domain;
using K9Crush.Modules.Admin.Domain.Events;

namespace K9Crush.Modules.Admin.Domain;

/// <summary>
/// The emlang yaml's ManagingTipsHealthContent chapter's tip/health-content
/// item. This slice implements that chapter's FIRST step only - "Create
/// Tip" -> "Tip Drafted" (the yaml's own StaffDraftsANewTip test: no
/// `given`, when "Create Tip", then "Tip Drafted"). The chapter's later
/// steps (Preview/Publish/Edit/Unpublish Tip) are separate, deliberately
/// deferred slices; no state/methods for them are stubbed out here.
///
/// Self-aggregating event-sourced entity (ADR-031), structurally identical
/// to this module's FeedbackInboxItem: a private [JsonConstructor], a
/// static Create(event) that Marten replays, and a static factory that
/// mints the event and the entity together. The one difference is where
/// the id comes from - FeedbackInboxItem's is externally supplied (it's
/// Identity's FeedbackId), whereas a Tip has no external origin, so this
/// one generates a fresh Guid the way Media's MediaAsset.Upload does.
///
/// No Inline snapshot is registered for Tip in AdminModule.cs: nothing
/// under ReadModels/** queries it today (the chapter's "New Tip Editor" /
/// "Tip Preview" / "Tip Published" screens are all yaml `t:` triggers, not
/// `v:` state-views). Follow the dual-use pattern documented in ADR-031 and
/// on FeedbackInboxItem if a read model is added later - and remember the
/// accompanying options.Schema.For&lt;Tip&gt;().DatabaseSchemaName(SchemaName)
/// call.
///
/// The yaml declares no `props` for "Create Tip"/"Tip Drafted", so this
/// entity holds no title/body/category - see TipDraftedV1's doc comment.
/// </summary>
public class Tip : Entity
{
    [JsonInclude] public DateTimeOffset DraftedAt { get; private set; }

    [JsonConstructor]
    private Tip() { }

    public static Tip Create(TipDraftedV1 e) => new()
    {
        Id = e.TipId,
        DraftedAt = e.DraftedAt
    };

    /// <summary>The emlang yaml's "Create Tip" -> "Tip Drafted".</summary>
    public static (Tip Tip, TipDraftedV1 Event) Draft()
    {
        var @event = new TipDraftedV1(Guid.NewGuid(), DateTimeOffset.UtcNow);
        return (Create(@event), @event);
    }
}
