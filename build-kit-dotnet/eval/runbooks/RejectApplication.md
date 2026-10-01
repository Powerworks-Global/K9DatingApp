# Runbook — Reject Application

_Generated skeleton (WS4.3) from specifications/RejectApplication.json and operability/RejectApplication.json — TODO markers are real gaps, not omissions._

## Precondition: Only an application under review can be rejected by its shelter owner

### Step: Rejecting an application that does not exist
**Trigger:** no application exists with the given id → the shelter owner attempts to reject it
**Expected outcome:** a Not Found result is returned and no integration event is produced
**Rollback:** TODO: not yet specified
**Escalation:** TODO: not yet specified

### Step: Rejecting an application under review
**Trigger:** an application submitted for a dog listing is under review → the shelter owner rejects it with a reason
**Expected outcome:** the application status becomes Rejected and an ApplicationRejected event is produced carrying the dog's name and the rejection reason
**Rollback:** Once ApplicationRejected has been delivered to the Notifications module, the event can't be recalled. (compensating action: A future 're-open application' command could exist as a compensating action; not implemented today -- a real, on-record gap, not silently assumed away.)
**Escalation:** TODO: not yet specified -- no on-call rotation or escalation path declared anywhere for this module.

### Step: Rejecting an application that is already rejected
**Trigger:** an application has already been rejected → the shelter owner attempts to reject it again
**Expected outcome:** a Conflict result is returned and no new integration event is produced
**Rollback:** N/A -- this Outcome should never occur at all. The fix is closing the guard (CONSTITUTION.md Rule C1), not rolling back an occurrence after the fact.
**Escalation:** TODO: not yet specified
**Known gap:** yes — see "Known gaps" section below

## Known gaps — failure modes without a verified path today

- **Duplicate rejection (a rejected-again application producing a second ApplicationRejected event)** — CONSTITUTION.md Rule C1
  - Rollback: N/A -- this Outcome should never occur at all. The fix is closing the guard (CONSTITUTION.md Rule C1), not rolling back an occurrence after the fact.
  - Escalation: TODO: not yet specified
