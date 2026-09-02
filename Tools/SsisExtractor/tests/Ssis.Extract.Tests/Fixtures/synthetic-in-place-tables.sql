/*
    Synthetic fixture tables -- extractor component-coverage test data only.
    -------------------------------------------------------------------------
    NOT part of the SSIS PoC's own deliverable. Backs the two IN-PLACE COLUMN MODIFICATION
    fixtures added by the 2026-09-02 gap audit (Phase 2):

        SyntheticDerivedColumnReplace.dtsx  Flat File Source -> Derived Column in
                                            "Replace <column>" mode (Name <- UPPER(Name))
                                            -> OLE DB Destination
        SyntheticCharacterMap.dtsx          Flat File Source -> Microsoft.CharacterMap in place
                                            (MapFlags = 8) -> OLE DB Destination

    WHAT THESE EXIST FOR
    --------------------
    A component that MODIFIES an existing column rather than adding a new one declares NO output
    column for it: the expression (or MapFlags) is persisted on the component's own INPUT column,
    marked usageType="readWrite", and the column keeps its UPSTREAM lineageId. Every
    output-column-based mechanism in this tool was therefore blind to it, and they all failed the
    same way at once:

        ssisx generate      emitted  Name = row.Name   (the RAW value)
                            where SSIS computes  UPPER(Name)
        ssisx conformance   produced no obligation for the transformation (gate 1)
        ssisx testgen       produced no boundary test for it (gate 2)
        ssisx report        omitted it from expressions.csv, and from nondeterministic.json
                            -- which is what feeds gate 3's ExcludedColumns
        ssisx graph         drew the column as an untouched passthrough

    and it did all of that while reporting 0 blocking gaps, 100% extraction coverage and nothing
    unmapped. Silently wrong data, reported as success -- the exact failure category this tool
    exists to prevent.

    THE SEED DATA IS CHOSEN TO MAKE THE TRANSFORMATION UNMISTAKABLE
    ---------------------------------------------------------------
    CSV rows: (1, 'alice')  (2, 'BoB')  (3, 'carol smith')

    Mixed case on purpose. If the in-place expression is dropped the loaded Name column is
    byte-identical to the CSV; if it is applied every row differs. Neither fixture has any
    post-load SQL, and the in-place transform is the ONLY thing in either package that can change
    a value -- an earlier attempt reused SyntheticPostFlowSql.dtsx, whose own SQL_PostLoad runs
    UPDATE ... SET Name = UPPER(Name), under which a working and a broken Derived Column produce
    identical rows. That fixture could not have detected the bug it was meant to prove.

    Expected after running SyntheticDerivedColumnReplace.dtsx:
        1  ALICE
        2  BOB
        3  CAROL SMITH

    SyntheticCharacterMap.dtsx is expected to produce a GAP rather than generated code: the ~20
    MapFlags values have never been measured against real SSIS, and inferring "8 means uppercase"
    from the flag's name is precisely the unmeasured guess that produced the Merge Join JoinType
    bug. Its table exists so the fixture can be built and run under dtexec if someone later
    measures MapFlags for real.

    Run once against .\SQLFORPOC_2022 before generating/executing either fixture:
        sqlcmd -S ".\SQLFORPOC_2022" -E -i Tools\SsisExtractor\tests\Ssis.Extract.Tests\Fixtures\synthetic-in-place-tables.sql
*/

USE SsisPoC;
GO

DROP TABLE IF EXISTS dbo.SyntheticDerivedColumnReplaceTarget;
GO
CREATE TABLE dbo.SyntheticDerivedColumnReplaceTarget
(
    ID   INT          NOT NULL,
    Name NVARCHAR(50) NOT NULL
);
GO

DROP TABLE IF EXISTS dbo.SyntheticCharacterMapTarget;
GO
CREATE TABLE dbo.SyntheticCharacterMapTarget
(
    ID   INT          NOT NULL,
    Name NVARCHAR(50) NOT NULL
);
GO
