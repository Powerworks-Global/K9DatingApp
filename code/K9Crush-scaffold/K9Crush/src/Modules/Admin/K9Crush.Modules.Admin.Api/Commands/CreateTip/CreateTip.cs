namespace K9Crush.Modules.Admin.Api.Commands.CreateTip;

/// <summary>
/// What this slice hands back to the caller.
///
/// There is deliberately no CreateTipRequest record alongside it: the
/// emlang yaml's ManagingTipsHealthContent chapter declares no `props` on
/// "Create Tip", so there is nothing for the caller to send and nothing to
/// validate. Same shape as this module's ResolveFeedback.cs, which is also
/// response-only.
/// </summary>
public sealed record CreateTipResponse(Guid TipId, DateTimeOffset DraftedAt);
