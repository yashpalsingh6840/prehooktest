using System;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.Dts.Pipeline;

namespace SC_CleanseCustomerRow
{
    /// <summary>
    /// Row-level cleansing performed inside the data flow (requirement 4).
    /// Trims and normalises the incoming CSV values, derives FullName, validates the
    /// e-mail address and the customer id, and stamps a load timestamp.
    /// </summary>
    [SSISScriptComponentEntryPoint]
    public class ScriptMain : UserComponent
    {
        // Deliberately simple - enough to reject "not-an-email" while accepting the rest.
        private static readonly Regex EmailPattern =
            new Regex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

        public override void Input0_ProcessInputRow(Input0Buffer Row)
        {
            string first = (Row.FirstName ?? string.Empty).Trim();
            string last = (Row.LastName ?? string.Empty).Trim();
            string email = (Row.Email ?? string.Empty).Trim();
            string customerId = (Row.CustomerID ?? string.Empty).Trim();

            Row.FullName = (first + " " + last).Trim();

            Row.CleanEmail = email.ToLowerInvariant();

            // A row is valid only when the customer id parses as an integer AND the
            // e-mail looks like an address. int.TryParse("") is false - which is why the
            // T-SQL harness has to use NULLIF(...,'') to match this behaviour.
            int parsedId;
            bool validId = int.TryParse(customerId, out parsedId);
            bool validEmail = email.Length > 0 && EmailPattern.IsMatch(email);

            Row.IsValidRow = validId && validEmail;

            Row.LoadDateTime = DateTime.Now;
        }
    }
}
