namespace Xtractor.Console;

public record StoreProfile(
    string Name,
    string Country,
    string? ExampleJson = null,
    string? ExamplePdf = null
);