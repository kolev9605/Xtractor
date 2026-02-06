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
        File.WriteAllText($"full-pdf-text.txt-{DateTime.Now:yyyyMMddHHmmss}", string.Join("\n---PAGE BREAK---\n", fullPdf));
        var targetPdfText = fullPdf[1];
        System.Console.WriteLine($"Target PDF text extracted: {targetPdfText.Length} characters");

        var store = new StoreProfile(
            Name: "Kaufland",
            Country: "BG",
            ExampleJson: File.ReadAllText("example-output.json"),
            ExamplePdf: referencePdfText
        );

        var conversation = await BuildConversation(store, targetPdfText, store.ExamplePdf, store.ExampleJson);

        var requestOptions = new ChatCompletionOptions()
        {   // Lower for consistent, focused JSON
            Temperature = 0.2f,
            // Slightly restrict token choices
            TopP = 0.9f,
        };

        // Count tokens BEFORE sending
        // int inputTokens = CountTokensForMessages(conversation, model);
        // System.Console.WriteLine($"Estimated input tokens: {inputTokens}");
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
                var words = page.GetWords().ToList();
                if (!words.Any()) continue;

                // Detect column boundaries by clustering words by X position
                var pageWidth = page.Width;
                // Assume ~3 columns for typical brochures
                var columnWidth = pageWidth / 3.0;

                // Group words into columns
                var columns = words
                    .GroupBy(w => (int)(w.BoundingBox.Left / columnWidth))
                    .OrderBy(g => g.Key)
                    .ToList();

                var pageTextBuilder = new System.Text.StringBuilder();

                foreach (var column in columns)
                {
                    // Within each column, sort top-to-bottom
                    var columnWords = column
                        .OrderByDescending(w => w.BoundingBox.Top)
                        .ToList();

                    // Group into lines within the column
                    var lines = new List<List<UglyToad.PdfPig.Content.Word>>();
                    double lineHeightTolerance = 5;

                    foreach (var word in columnWords)
                    {
                        var currentLine = lines.LastOrDefault();

                        if (currentLine == null ||
                            Math.Abs(currentLine.Average(w => w.BoundingBox.Top) - word.BoundingBox.Top) > lineHeightTolerance)
                        {
                            lines.Add(new List<UglyToad.PdfPig.Content.Word> { word });
                        }
                        else
                        {
                            currentLine.Add(word);
                        }
                    }

                    // Build text for this column
                    foreach (var line in lines)
                    {
                        // Sort words in line left-to-right
                        var lineWords = line.OrderBy(w => w.BoundingBox.Left);
                        var lineText = string.Join(" ", lineWords.Select(w => w.Text));
                        pageTextBuilder.AppendLine(lineText);
                    }

                    // Add separator between columns
                    pageTextBuilder.AppendLine("---");
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
    private static SystemChatMessage BuildSystemPrompt(StoreProfile store)
    {
        return new SystemChatMessage($$"""
You extract grocery products from supermarket brochures.

GENERAL RULES:
- Extract ONLY products with an explicit price
- Ignore headers, footers, slogans, and legal text
- Preserve original product names (language, casing, punctuation)
- Do NOT invent products
- If information is unclear or incomplete, omit the product
- Output JSON ONLY, no commentary

PRICES:
- Prefer EUR
- If only BGN is present, convert to EUR using rate 1 EUR = 1.95583 BGN

PACKAGING:
- Normalize packaging into:
  - packs: integer
  - unit_quantity: number
  - unit: one of [GRAM, KILOGRAM, MILLILITER, LITER, PIECE]
- Examples:
  - "3x250g" → packs=3, unit_quantity=250, unit=GRAM
  - "2 x 400 g" → packs=2, unit_quantity=400, unit=GRAM
  - "48 бр" → packs=1, unit_quantity=48, unit=PIECE

OUTPUT JSON SCHEMA:
{
  "store": "{{store.Name}}",
  "page": number,
  "startDate": "YYYY-MM-DD",
  "endDate": "YYYY-MM-DD",
  "products": [
    {
      "name": string,
      "price": {
        "amount": number,
        "currency": "EUR"
      },
      "packaging": {
        "packs": number,
        "unit_quantity": number,
        "unit": "GRAM|KILOGRAM|MILLILITER|LITER|PIECE"
      }
    }
  ]
}

The output MUST match this schema exactly.
The response must be valid JSON.

Do not:
- use markdown
- wrap the response in ``` or ```json
- include explanations, comments, or extra text

Return the JSON object directly.
""");
    }

    private static async Task<List<ChatMessage>> BuildConversation(
        StoreProfile store,
        string targetPdfText,
        string? referencePdfText = null,
        string? exampleJson = null
    )
    {
        var messages = new List<ChatMessage>
        {
            BuildSystemPrompt(store)
        };

        if (referencePdfText != null && exampleJson != null)
        {
            messages.Add(new UserChatMessage($$"""
Below is a brochure page and the correctly extracted JSON.
Learn the extraction logic and formatting.

BROCHURE TEXT:
{{referencePdfText}}
"""));

            messages.Add(new AssistantChatMessage(exampleJson));
        }

        messages.Add(new UserChatMessage($$"""
Extract products from the brochure text below.
Each page is independent.

BROCHURE TEXT:
{{targetPdfText}}
"""));

        return messages;
    }
}