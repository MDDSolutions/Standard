namespace FileRelay.Core;

public class SubdirectoryContext : TransferContext
{
    // Parameter name matches property name so System.Text.Json
    // can bind it without [JsonConstructor].
    public SubdirectoryContext(string relativePath)
    {
        RelativePath = relativePath;
    }

    public override string RelativePath { get; }
}
