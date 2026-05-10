namespace FileRelay.Core.Models;

public class RotateKeyRequest
{
    public string? ClientEntropy { get; set; } // base64-encoded random bytes from the client
}
