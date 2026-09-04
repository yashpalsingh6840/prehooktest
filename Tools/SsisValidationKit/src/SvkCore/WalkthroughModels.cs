namespace Svk.Core;

public enum WalkthroughStatus { NotValidated, Validated, Blocked }

/// <summary>A reviewer's own record for one `ssisx conformance` DataFlow rule -- written once
/// as a stub by `svk walkthrough`, then human-owned: the tool never overwrites an existing
/// entry, only adds stubs for rules that didn't exist yet (mirrors ssisx conformance's own
/// claims-file lifecycle).</summary>
public sealed class WalkthroughClaim
{
    public required string RuleId { get; set; }
    public WalkthroughStatus Status { get; set; } = WalkthroughStatus.NotValidated;
    public string? Reviewer { get; set; }
    public string? ReviewedDate { get; set; }
    public string? Note { get; set; }
}
