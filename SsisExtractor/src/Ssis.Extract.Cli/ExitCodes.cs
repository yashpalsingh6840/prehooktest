namespace Ssis.Extract.Cli;

/// <summary>
/// Phase 1.5 of the independent-review fix plan (`C:\Users\yashpal.singh01\.claude\plans\
/// snappy-skipping-grove.md`, R6). The ONE place every command's own exit-code meaning is
/// written down, so a caller (a human, a script, or an AI assistant following
/// `Tools/COPILOT_GUIDE.md`) never has to infer it per-command from prose scattered across
/// `--help` text and doc files that can drift out of sync with the actual `return N` calls.
///
/// Not every command uses every code -- `extract`'s own gate-1/gate-2 semantics (0/1/2/3) predate
/// this file and are unchanged; this is deliberately just the shared VOCABULARY, not a forced
/// single meaning imposed on every command. Each command's own `--help` text says which subset
/// applies to it.
/// </summary>
internal static class ExitCodes
{
    /// <summary>Ran clean: no blocking gap, no usage error, no check failure.</summary>
    public const int Ok = 0;

    /// <summary>A check this command exists to run (a conformance gate, a diff, a mutation-style comparison) found a real difference/failure. Not a tool bug and not a usage mistake.</summary>
    public const int CheckFailed = 1;

    /// <summary>The command was asked to do something impossible: a missing/malformed argument, an input path that doesn't exist, an explicitly-given directory that isn't there. Never returned for a normal "the portfolio has gaps" outcome -- see <see cref="BlockingGaps"/>.</summary>
    public const int UsageOrInputError = 2;

    /// <summary>`generate` (or an equivalent) wrote real files, but at least one blocking (Tier-1/2/3, not Advisory) gap remains -- "code was written, some of it needs a human/AI to finish." Never means zero files were written; see <see cref="NothingWritten"/> for that.</summary>
    public const int BlockingGaps = 3;

    /// <summary>The command produced ZERO usable output -- every package/flow was blocked, nothing to build. Distinct from <see cref="BlockingGaps"/> specifically so a caller can tell "partial success, keep going" apart from "nothing here at all" without re-parsing a report file.</summary>
    public const int NothingWritten = 4;

    /// <summary>One or more packages hit an unexpected internal crash during generation, but the rest of the batch still completed (GenerateCommand's own crashCount). A real ssisx defect, not a gap -- treat like <see cref="BlockingGaps"/> for pipeline purposes (a partial result worth keeping), but investigate the diagnostic file.</summary>
    public const int PartialCrash = 98;

    /// <summary>ssisx itself crashed with an uncaught, unexpected exception (Program.cs's own top-level catch) -- nothing was produced at all, and this is always a real ssisx bug to report, never a normal outcome of any input.</summary>
    public const int TopLevelCrash = 99;
}
