using System.Text.Json.Serialization;

namespace FileRelay.Core;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(SubdirectoryContext), "subdirectory")]
public abstract class TransferContext
{
    public abstract string RelativePath { get; }

    // Default equality: same concrete type + same RelativePath.
    // Subclasses with additional identity fields should override both.
    public override bool Equals(object? obj)
        => obj is TransferContext other
            && obj.GetType() == GetType()
            && RelativePath == other.RelativePath;

    public override int GetHashCode()
        => (GetType().GetHashCode() * 397) ^ (RelativePath?.GetHashCode() ?? 0);

    public override string ToString() => RelativePath;
}
