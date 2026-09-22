namespace SyntheticScriptComponentSeams.Model;

public sealed class SyntheticScriptSeamsTarget
{
    public int ID { get; set; }

    public string FirstName { get; set; } = "";

    public string LastName { get; set; } = "";

    public string FullName { get; set; } = "";

    public bool IsValid { get; set; }
}
