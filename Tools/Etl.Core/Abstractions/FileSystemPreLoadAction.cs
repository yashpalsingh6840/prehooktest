namespace Etl.Core.Abstractions;

/// <summary>
/// A pre-load file operation -- e.g. archiving a source workbook before a Data Flow Task reads
/// it. Kept as its own action list in a generated <c>Program.cs</c>, not folded into the pre-load
/// SQL statement list or the ordered step list: it isn't SQL (so it can't join the string list),
/// and it isn't transactional/row-counted the way an <see cref="ILoadTask"/> step is (a file copy
/// can't be rolled back, and reporting "0 rows" for it in <see cref="PackageResult.Steps"/> would
/// be misleading). Runs after every pre-load SQL statement and before any step -- SSIS-evidenced
/// order (RBC_Demo_ETL's Package_Advanced.dtsx: SQL_TruncateTargets, an Execute SQL Task, always precedes
/// FST_ArchiveWorkbook, a File System Task, inside the same Sequence Container). A package
/// whose real precedence constraints interleave SQL and file pre-load actions in some OTHER
/// order is a gap for the generator to report, not something this two-list shape can express.
/// </summary>
public sealed record FileSystemPreLoadAction(
    FileSystemOperation Operation,
    string SourcePath,
    string? DestinationPath,
    bool OverwriteDestination);

/// <summary>
/// The subset of SSIS's own <c>DTSFileSystemOperation</c> enum this generator can translate --
/// confirmed via the real object model (`Operation` property,
/// `Microsoft.SqlServer.Dts.Tasks.FileSystemTask.DTSFileSystemOperation`), not guessed: the full
/// enum also has SetAttributes/CopyDirectory/MoveDirectory/DeleteDirectory/
/// DeleteDirectoryContent, none evidenced in any real package yet -- left out rather than
/// implemented against documentation alone.
/// </summary>
public enum FileSystemOperation
{
    Copy,
    Move,
    Delete,
    Rename,
    CreateDirectory,
}
