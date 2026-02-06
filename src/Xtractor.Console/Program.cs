using System.ClientModel;
using Microsoft.Extensions.Configuration;
using OpenAI;
using OpenAI.Chat;
using SharpToken;

namespace Xtractor.Console;

public class Program
{
    public static async Task Main()
    {
        var result = await ExtractPricesOpenAI();
        var outputFileName = $"output-{DateTime.Now:yyyyMMddHHmmss}.json";
        await File.WriteAllTextAsync(outputFileName, result);
        System.Console.WriteLine($"Results written to {outputFileName}");
    }

    public static async Task<string> ExtractPricesOpenAI()
    {
        // Load configuration from user secrets
        var config = new ConfigurationBuilder()
            .AddUserSecrets<Program>()
            .Build();

        var endpoint = "https://models.github.ai/inference";
        var credential = config["GitHubToken"] ?? throw new InvalidOperationException("GitHubToken not found in user secrets");
        var model = "openai/gpt-4o";

        var openAIOptions = new OpenAIClientOptions()
        {
            Endpoint = new Uri(endpoint)
        };

        var client = new ChatClient(model, new ApiKeyCredential(credential), openAIOptions);

        var fullPdf = ExtractTextFromPdfV3("kaufland-1.pdf");
        var referencePdfText = fullPdf[0];
        System.Console.WriteLine($"Reference PDF text extracted: {referencePdfText.Length} characters");

        var targetPdfText = fullPdf[8];
        System.Console.WriteLine($"Target PDF text extracted: {targetPdfText.Length} characters");

        List<ChatMessage> conversation = [
            new SystemChatMessage("""
You extract grocery products from supermarket brochures.

Rules:
- Only extract products that have an explicit price
- Ignore headers, footers, slogans, and legal text
- Preserve original product names (Bulgarian, casing, punctuation)
- Often the price will be both in Euro and in BGN. Extract the Euro price, if not available, extract the BGN price and convert to Euro (divide by 1.95583)
- Do NOT invent products
- If information is unclear, omit the product
- Output JSON only, matching the example structure exactly

Valid unit types (use ONLY these):
- GRAM
- KILOGRAM
- MILLILITER
- LITER
- PIECE (for countable items without weight/volume)
"""),

            new UserChatMessage("""
Below is a brochure page and the correct extracted products.
Learn the format and extraction logic.

BROCHURE TEXT:
""" + referencePdfText),

            new AssistantChatMessage(await File.ReadAllTextAsync("example-output.json")),

            new UserChatMessage("""
            Extract products from the brochure text below.
            Each page is independent.
            Follow the same rules and JSON structure as the example.
            The output must be valid JSON.

            BROCHURE TEXT:
            """ + targetPdfText)
        ];

        var requestOptions = new ChatCompletionOptions()
        {   // Lower for consistent, focused JSON
            Temperature = 0.2f,
            // Slightly restrict token choices
            TopP = 0.9f,
            MaxOutputTokenCount = 1000
        };

        // Count tokens BEFORE sending
        int inputTokens = CountTokensForMessages(conversation, model);
        System.Console.WriteLine($"Estimated input tokens: {inputTokens}");
        var response = await client.CompleteChatAsync(conversation, requestOptions);

        // Display token usage
        if (response.Value.Usage != null)
        {
            System.Console.WriteLine($"Token usage:");
            System.Console.WriteLine($"  Input tokens: {response.Value.Usage.InputTokenCount}");
            System.Console.WriteLine($"  Output tokens: {response.Value.Usage.OutputTokenCount}");
            System.Console.WriteLine($"  Total tokens: {response.Value.Usage.TotalTokenCount}");
        }

        System.Console.WriteLine("Extracted products JSON:");
        return response.Value.Content[0].Text;
    }

    private static string[] ExtractTextFromPdfV3(string filePath)
    {
        var pages = new List<string>();

        try
        {
            using var document = UglyToad.PdfPig.PdfDocument.Open(filePath);

            foreach (var page in document.GetPages())
            {
                // Extract words with spatial positioning
                var words = page.GetWords().ToList();
                
                // Sort by Y position (top to bottom), then X position (left to right)
                // This preserves the visual layout of brochures
                var sortedWords = words
                    .OrderByDescending(w => w.BoundingBox.Top) // Top to bottom
                    .ThenBy(w => w.BoundingBox.Left)          // Left to right
                    .ToList();

                // Group words into lines based on Y position (within tolerance)
                var lines = new List<List<UglyToad.PdfPig.Content.Word>>();
                double lineHeightTolerance = 5; // Adjust if needed
                
                foreach (var word in sortedWords)
                {
                    var currentLine = lines.LastOrDefault();
                    
                    if (currentLine == null || 
                        Math.Abs(currentLine.First().BoundingBox.Top - word.BoundingBox.Top) > lineHeightTolerance)
                    {
                        // Start new line
                        lines.Add(new List<UglyToad.PdfPig.Content.Word> { word });
                    }
                    else
                    {
                        // Add to current line
                        currentLine.Add(word);
                    }
                }

                // Build text with proper line breaks
                var pageTextBuilder = new System.Text.StringBuilder();
                foreach (var line in lines)
                {
                    var lineText = string.Join(" ", line.Select(w => w.Text));
                    pageTextBuilder.AppendLine(lineText);
                }

                var pageText = pageTextBuilder.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(pageText))
                {
                    pages.Add(pageText);
                }
            }
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"Error extracting text from {filePath}: {ex.Message}");
        }

        return pages.ToArray();
    }

    private static int CountTokensForMessages(List<ChatMessage> messages, string modelName)
    {
        // Get the encoding for the model
        var encoding = GptEncoding.GetEncodingForModel(modelName.Split('/').Last());
        int totalTokens = 0;

        foreach (var message in messages)
        {
            // Add tokens for message formatting (role + delimiters)
            totalTokens += 4; // Every message has overhead

            // Count tokens in the content
            string messageText = "";

            if (message is SystemChatMessage system)
            {
                messageText = string.Join("", system.Content.Select(c => c.Text));
            }
            else if (message is UserChatMessage user)
            {
                messageText = string.Join("", user.Content.Select(c => c.Text));
            }
            else if (message is AssistantChatMessage assistant)
            {
                messageText = string.Join("", assistant.Content.Select(c => c.Text));
            }

            if (!string.IsNullOrEmpty(messageText))
            {
                totalTokens += encoding.Encode(messageText).Count;
            }
        }

        totalTokens += 2; // Every request has additional overhead

        return totalTokens;
    }
}