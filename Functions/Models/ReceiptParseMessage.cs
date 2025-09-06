namespace Functions.Models;

public sealed record ReceiptParseMessage(string Container, string Blob, string? ReceiptId, int V = 1);