using System;
using System.Data;
using Microsoft.SqlServer.Dts.Pipeline.Wrapper;
using Microsoft.SqlServer.Dts.Runtime.Wrapper;

[Microsoft.SqlServer.Dts.Pipeline.SSISScriptComponentEntryPointAttribute]
public class ScriptMain : UserComponent
{
    // Row-level Script Component used inside DFT - Load Customers From CSV,
    // between the Flat File Source and the Row Count transform.
    // Trims whitespace, lower-cases/normalizes the email, builds FullName,
    // stamps LoadDateTime, and flags rows that fail basic validation instead
    // of dropping them - bad rows still land in dbo.StagingCustomers with
    // IsValidRow = 0 so they can be reviewed, and DFT - Build Sales Summary
    // excludes them via "WHERE c.IsValidRow = 1".
    public override void Input0_ProcessInputRow(Input0Buffer Row)
    {
        string firstName = Row.FirstName_IsNull ? string.Empty : Row.FirstName.Trim();
        string lastName = Row.LastName_IsNull ? string.Empty : Row.LastName.Trim();
        string email = Row.Email_IsNull ? string.Empty : Row.Email.Trim();
        string customerId = Row.CustomerID_IsNull ? string.Empty : Row.CustomerID.Trim();

        Row.FullName = (firstName + " " + lastName).Trim();

        string cleanEmail = email.ToLowerInvariant();
        Row.CleanEmail = cleanEmail;

        int parsedId;
        bool validId = int.TryParse(customerId, out parsedId);
        bool validEmail = cleanEmail.Contains("@") && cleanEmail.Contains(".");

        Row.IsValidRow = (bool)(validId && validEmail && firstName.Length > 0);
        Row.LoadDateTime = DateTime.Now;
    }
}
