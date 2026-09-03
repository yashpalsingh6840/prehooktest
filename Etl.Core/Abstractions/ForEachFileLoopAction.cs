namespace Etl.Core.Abstractions;

/// <summary>
/// One SSIS ForEach Loop Container's own enumerator config -- Folder/FileSpec/Recurse/NameMode,
/// shared identically by BOTH loop-body shapes the SSIS-to-C# generator supports:
/// <see cref="ForEachLoopStep"/> (a single Execute SQL Task whose SQL text is rebuilt per
/// iteration, the real evidenced shape from RBC_Demo_ETL's own Package_Advanced.dtsx
/// FEL_SampleFiles) and <see cref="ForEachFileDataFlowStep{TRow,TEntity}"/> (a whole Data Flow
/// Task re-run per file, built speculatively 2026-08-30 -- zero real evidenced package anywhere
/// in the tracked portfolio has this shape; see that type's own doc comment). Any OTHER loop
/// body content (a File System Task, a nested container, more than one child executable) is
/// still a generation gap, not something either type models.
/// </summary>
public sealed record ForEachFileLoopAction(
    string Folder,
    string FileSpec,
    bool Recurse,
    ForEachFileNameMode NameMode);

/// <summary>
/// SSIS's own ForEach File Enumerator <c>FileNameRetrievalType</c>. Values are from Microsoft's
/// public <c>IDTSForEachFileEnumerator</c> documentation (FullyQualified=0, NameAndExtension=1,
/// NameOnly=2 -- no extension) -- unlike most raw-enum mappings elsewhere in this codebase's
/// sibling extractor tool, this one was NOT independently probed against a live object model in
/// this environment, since it is long-stable, public Microsoft API surface rather than an
/// undocumented or inferred custom property. <see cref="FullyQualified"/> is the schema default
/// (omitted from a saved .dtsx when left unset, the same convention SSIS uses for
/// FileSystemTask's own OperationRaw).
/// </summary>
public enum ForEachFileNameMode
{
    FullyQualified,
    NameAndExtension,
    NameOnly,
}
