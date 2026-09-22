-- Backing table for SyntheticXmlSource.dtsx (an XML Source, Microsoft.XmlSourceAdapter, reading
-- synthetic-xml-source.xml against synthetic-xml-source.xsd -> OLE DB Destination, no transform,
-- a genuine direct-copy pipeline -- the same shape as SyntheticExcelSource.dtsx). Column names
-- are lowercase with underscores, matching the pipeline's own buffer column names exactly (the
-- XML element names id/first_name/last_name/email/gender/country) -- required for this fixture
-- builder's own ResolveOleDbMetadata helper, which maps every external (destination) column by
-- NAME against the upstream input column, not by position.
--
-- No surrogate key column matching EF's own "Id"/"{Type}Id" convention or PrimaryKeyInference's
-- naming heuristic (both need an exact "Id" spelling, not "id" alongside an underscore-separated
-- sibling) -- deliberately left this way, same as SyntheticExcelSource.dtsx, so DbContextEmitter's
-- already-proven HasNoKey() fallback is exercised again rather than depending on a coincidental
-- convention match.
DROP TABLE IF EXISTS dbo.SyntheticXmlSourceTarget;
CREATE TABLE dbo.SyntheticXmlSourceTarget (
    id INT NOT NULL,
    first_name NVARCHAR(255) NOT NULL,
    last_name NVARCHAR(255) NOT NULL,
    email NVARCHAR(255) NULL,
    gender NVARCHAR(255) NULL,
    country NVARCHAR(255) NOT NULL
);
