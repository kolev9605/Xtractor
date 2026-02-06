using System.Text.Json;
using Xunit;
using Xunit.Sdk;

namespace Xtractor.Tests;

public class OutputComparisonTests
{
    private const double NameSimilarityThreshold = 0.85;

    [Fact]
    public void LatestOutputMatchesExpectedPage()
    {
        var repoRoot = FindRepoRoot();

        var expectedPath = Environment.GetEnvironmentVariable("XTRACTOR_EXPECTED_OUTPUT")
            ?? Path.Combine(repoRoot, "src", "Xtractor.Tests", "TestData", "expected-page.json");

        var actualPath = Environment.GetEnvironmentVariable("XTRACTOR_ACTUAL_OUTPUT")
            ?? FindLatestOutput(Path.Combine(repoRoot, "src", "Xtractor.Console"));

        if (!File.Exists(expectedPath))
        {
            throw new XunitException($"Expected output file not found: {expectedPath}. Set XTRACTOR_EXPECTED_OUTPUT to your correct JSON file.");
        }

        if (string.IsNullOrWhiteSpace(actualPath) || !File.Exists(actualPath))
        {
            throw new XunitException("No output-*.json file found. Set XTRACTOR_ACTUAL_OUTPUT to the file to compare.");
        }

        var expectedPage = LoadSinglePage(expectedPath);
        var actualPages = LoadPages(actualPath);

        var actualPage = actualPages.FirstOrDefault(p => p.Page == expectedPage.Page);
        if (actualPage == null)
        {
            throw new XunitException($"No page {expectedPage.Page} found in actual output.");
        }

        var compareDates = string.Equals(
            Environment.GetEnvironmentVariable("XTRACTOR_COMPARE_DATES"),
            "true",
            StringComparison.OrdinalIgnoreCase);

        ComparePages(expectedPage, actualPage, compareDates);
    }

    private static void ComparePages(PageData expected, PageData actual, bool compareDates)
    {
        Assert.Equal(expected.Store, actual.Store);
        Assert.Equal(expected.Page, actual.Page);

        if (compareDates)
        {
            Assert.Equal(expected.StartDate, actual.StartDate);
            Assert.Equal(expected.EndDate, actual.EndDate);
        }

        Assert.Equal(expected.Products.Count, actual.Products.Count);

        var remainingActual = new List<ProductData>(actual.Products);

        foreach (var expectedProduct in expected.Products)
        {
            var match = FindBestNameMatch(expectedProduct, remainingActual, out var similarity);
            if (match == null || similarity < NameSimilarityThreshold)
            {
                throw new XunitException($"No close name match for '{expectedProduct.Name}'. Best similarity: {similarity:F2}.");
            }

            Assert.Equal(expectedProduct.Price.Amount, match.Price.Amount);
            Assert.Equal(expectedProduct.Price.Currency, match.Price.Currency);

            Assert.Equal(expectedProduct.Packaging.Packs, match.Packaging.Packs);
            Assert.Equal(expectedProduct.Packaging.UnitQuantity, match.Packaging.UnitQuantity);
            Assert.Equal(expectedProduct.Packaging.Unit, match.Packaging.Unit);

            remainingActual.Remove(match);
        }

        if (remainingActual.Count > 0)
        {
            var extras = string.Join(", ", remainingActual.Select(p => p.Name));
            throw new XunitException($"Found unexpected extra products: {extras}");
        }
    }

    private static ProductData? FindBestNameMatch(
        ProductData expected,
        List<ProductData> candidates,
        out double bestSimilarity)
    {
        bestSimilarity = 0;
        ProductData? best = null;

        foreach (var candidate in candidates)
        {
            var similarity = NameSimilarity(expected.Name, candidate.Name);
            if (similarity > bestSimilarity)
            {
                bestSimilarity = similarity;
                best = candidate;
            }
        }

        return best;
    }

    private static double NameSimilarity(string a, string b)
    {
        var na = NormalizeName(a);
        var nb = NormalizeName(b);

        if (na.Length == 0 && nb.Length == 0) return 1.0;
        if (na.Length == 0 || nb.Length == 0) return 0.0;

        var distance = LevenshteinDistance(na, nb);
        var maxLen = Math.Max(na.Length, nb.Length);
        return 1.0 - (double)distance / maxLen;
    }

    private static string NormalizeName(string name)
    {
        var chars = name
            .ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c))
            .ToArray();

        var normalized = new string(chars);
        return string.Join(" ", normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static int LevenshteinDistance(string s, string t)
    {
        var n = s.Length;
        var m = t.Length;
        var d = new int[n + 1, m + 1];

        for (int i = 0; i <= n; i++) d[i, 0] = i;
        for (int j = 0; j <= m; j++) d[0, j] = j;

        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                var cost = s[i - 1] == t[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }

        return d[n, m];
    }

    private static PageData LoadSinglePage(string path)
    {
        var pages = LoadPages(path);
        if (pages.Count != 1)
        {
            throw new XunitException($"Expected exactly one page in {path}, but found {pages.Count}.");
        }

        return pages[0];
    }

    private static List<PageData> LoadPages(string path)
    {
        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);

        return doc.RootElement.ValueKind switch
        {
            JsonValueKind.Array => doc.RootElement.EnumerateArray().Select(ParsePage).ToList(),
            JsonValueKind.Object => new List<PageData> { ParsePage(doc.RootElement) },
            _ => throw new XunitException($"Unexpected JSON root in {path}.")
        };
    }

    private static PageData ParsePage(JsonElement element)
    {
        var store = element.GetProperty("store").GetString() ?? "";
        var page = element.GetProperty("page").GetInt32();
        var startDate = element.TryGetProperty("startDate", out var sd) ? sd.GetString() : null;
        var endDate = element.TryGetProperty("endDate", out var ed) ? ed.GetString() : null;

        var products = element.GetProperty("products")
            .EnumerateArray()
            .Select(ParseProduct)
            .ToList();

        return new PageData(store, page, startDate, endDate, products);
    }

    private static ProductData ParseProduct(JsonElement element)
    {
        var name = element.GetProperty("name").GetString() ?? "";

        var priceElem = element.GetProperty("price");
        var price = new PriceData(
            Amount: priceElem.GetProperty("amount").GetDecimal(),
            Currency: priceElem.GetProperty("currency").GetString() ?? "");

        var packElem = element.GetProperty("packaging");
        var packaging = new PackagingData(
            Packs: packElem.GetProperty("packs").GetInt32(),
            UnitQuantity: packElem.GetProperty("unit_quantity").GetDecimal(),
            Unit: packElem.GetProperty("unit").GetString() ?? "");

        return new ProductData(name, price, packaging);
    }

    private static string FindLatestOutput(string folder)
    {
        if (!Directory.Exists(folder)) return string.Empty;

        var files = Directory.GetFiles(folder, "output-*.json", SearchOption.TopDirectoryOnly);
        if (files.Length == 0) return string.Empty;

        return files
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .First().FullName;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Xtractor.slnx")))
        {
            dir = dir.Parent;
        }

        if (dir == null)
        {
            throw new XunitException("Unable to locate repo root (Xtractor.slnx).");
        }

        return dir.FullName;
    }

    private record PageData(string Store, int Page, string? StartDate, string? EndDate, List<ProductData> Products);
    private record ProductData(string Name, PriceData Price, PackagingData Packaging);
    private record PriceData(decimal Amount, string Currency);
    private record PackagingData(int Packs, decimal UnitQuantity, string Unit);
}
