"""SSIS (Integration Services) data type -> .NET/CLR type mapping.

Sourced directly from Microsoft's published documentation, not assumed (per the plan's
independence rule, §2):
- https://learn.microsoft.com/en-us/sql/integration-services/data-flow/integration-services-data-types
  (the full DT_* enumeration and its semantics)
- https://learn.microsoft.com/en-us/sql/integration-services/extending-packages-custom-objects/data-flow/working-with-data-types-in-the-data-flow
  ("Mapping between Integration Services and Managed Data Types" table, and the
  ConvertBufferDataTypeToFitManaged table for the handful of types that map to another SSIS type
  before mapping to a managed type: DT_STR/DT_TEXT/DT_NTEXT -> DT_WSTR -> String,
  DT_IMAGE -> DT_BYTES -> Byte[], DT_DECIMAL/DT_CY -> DT_NUMERIC -> Decimal,
  DT_DATE/DT_DBDATE/DT_FILETIME/DT_DBTIMESTAMP2 -> DT_DBTIMESTAMP -> DateTime).

Real .dtsx XML (confirmed by direct inspection of all 5 sample packages) spells the type as the
enum member name with the "DT_" prefix stripped, in whatever internal case that member happens to
have (e.g. "wstr", "i4", "dbTimeStamp", "dbTimeStamp2") — matching is done case-insensitively so
that undocumented exact-casing variants never cause a false "unrecognized type".

DT_DBDATE additionally accepts System.DateOnly (introduced .NET 6, after this SSIS documentation
was last written) as a modern, semantically correct equivalent for "a date structure that
consists of year, month, and day" — confirmed by direct inspection of real generated code
(Model/SalesSummary.cs: LastOrderDate is DateOnly for a dbDate column). This is not "trusting the
generator's own claim" — DateOnly correctly represents a date-without-time value per the .NET BCL
documentation, independent of anything the generation tool says about itself.
"""
from __future__ import annotations

from dataclasses import dataclass

# Canonical key = DT_ suffix, uppercase. Value = acceptable CLR type name(s), short form
# (no "System." prefix). Closed enumeration transcribed from the docs above — this is not
# per-package guessing, it is the complete, finite set Microsoft documents.
SSIS_TYPE_TO_CLR: dict[str, tuple[str, ...]] = {
    "WSTR": ("String",),
    "STR": ("String",),
    "TEXT": ("String",),
    "NTEXT": ("String",),
    "BYTES": ("Byte[]",),
    "IMAGE": ("Byte[]",),
    "BOOL": ("Boolean",),
    "I1": ("SByte",),
    "I2": ("Int16",),
    "I4": ("Int32",),
    "I8": ("Int64",),
    "UI1": ("Byte",),
    "UI2": ("UInt16",),
    "UI4": ("UInt32",),
    "UI8": ("UInt64",),
    "R4": ("Single",),
    "R8": ("Double",),
    "NUMERIC": ("Decimal",),
    "DECIMAL": ("Decimal",),
    "CY": ("Decimal",),
    "GUID": ("Guid",),
    "DATE": ("DateTime",),
    "DBDATE": ("DateTime", "DateOnly"),
    "DBTIME": ("TimeSpan",),
    "DBTIME2": ("TimeSpan",),
    "DBTIMESTAMP": ("DateTime",),
    "DBTIMESTAMP2": ("DateTime",),
    "DBTIMESTAMPOFFSET": ("DateTimeOffset",),
    "FILETIME": ("DateTime",),
}

# C# keyword aliases -> their canonical BCL short name, so a generated `int` property and the
# documented `Int32` mapping compare equal. Confirmed against real generated POCOs (all use C#
# keyword aliases, e.g. `public int CustomerID`).
_CSHARP_KEYWORD_ALIASES: dict[str, str] = {
    "string": "String",
    "bool": "Boolean",
    "byte": "Byte",
    "sbyte": "SByte",
    "short": "Int16",
    "ushort": "UInt16",
    "int": "Int32",
    "uint": "UInt32",
    "long": "Int64",
    "ulong": "UInt64",
    "float": "Single",
    "double": "Double",
    "decimal": "Decimal",
    "char": "Char",
    "object": "Object",
}


@dataclass
class ResolvedSsisType:
    canonical: str  # e.g. "WSTR"
    clr_candidates: tuple[str, ...]  # e.g. ("String",)


def resolve_ssis_type(raw: str) -> ResolvedSsisType | None:
    """Normalizes a raw .dtsx dataType/cachedDataType attribute value (e.g. "wstr",
    "dbTimeStamp2") to its canonical DT_ suffix and acceptable CLR type(s). Returns None if the
    token isn't in the documented enumeration — never guessed, reported as Unverifiable by the
    caller."""
    key = raw.strip().upper()
    candidates = SSIS_TYPE_TO_CLR.get(key)
    if candidates is None:
        return None
    return ResolvedSsisType(canonical=key, clr_candidates=candidates)


def normalize_clr_type(raw: str) -> str:
    """Normalizes a C# type token from generated source (e.g. "int", "string?", "System.Guid",
    "byte[]") to the short BCL name used as the comparison unit in SSIS_TYPE_TO_CLR."""
    token = raw.strip()
    if token.endswith("?"):
        token = token[:-1].strip()
    if token.startswith("System."):
        token = token[len("System."):]
    if token in ("byte[]", "Byte[]"):
        return "Byte[]"
    return _CSHARP_KEYWORD_ALIASES.get(token, token)
