# PDF Text Extraction Documentation

## Method: `ExtractTextFromPdfV3`

### Overview
This method extracts text from multi-column PDF documents (like grocery brochures) while preserving the visual layout and reading order. It's specifically designed for documents with column-based layouts where maintaining the spatial relationship between text elements is critical.

### Method Signature
```csharp
private static string[] ExtractTextFromPdfV3(string filePath)
```

**Parameters:**
- `filePath` (string): Absolute path to the PDF file

**Returns:**
- `string[]`: Array of strings, one per page. Each string contains the extracted text with preserved layout.

---

## How It Works

### 1. Document Opening
```csharp
using var document = UglyToad.PdfPig.PdfDocument.Open(filePath);
```
Opens the PDF using PdfPig library, which provides access to individual words with their spatial coordinates.

### 2. Page-by-Page Processing
```csharp
foreach (var page in document.GetPages())
```
Iterates through each page in the document.

### 3. Word Extraction with Positions
```csharp
var words = page.GetWords().ToList();
```
Extracts all words from the page along with their bounding box coordinates (x, y, width, height). This is crucial for spatial analysis.

**Why this matters:** Unlike simple text extraction (`page.Text`), this gives us the exact position of each word on the page.

---

## Column Detection Algorithm

### Step 1: Calculate Column Boundaries
```csharp
var pageWidth = page.Width;
var columnWidth = pageWidth / 3.0; // Assume ~3 columns
```
Divides the page width into 3 equal sections. This is based on typical brochure layouts.

**Adjustable:** Change the divisor (3.0) if your PDFs have a different number of columns.

### Step 2: Group Words by Column
```csharp
var columns = words
    .GroupBy(w => (int)(w.BoundingBox.Left / columnWidth))
    .OrderBy(g => g.Key)
    .ToList();
```

**How it works:**
1. Takes the X-coordinate of each word's left edge (`w.BoundingBox.Left`)
2. Divides by `columnWidth` and converts to integer
3. This creates a column index: 0 (left column), 1 (middle), 2 (right)
4. Groups all words with the same column index together
5. Orders columns left-to-right

**Example:**
```
Page width: 600
Column width: 200

Word at X=50  → 50/200 = 0 → Column 0 (left)
Word at X=250 → 250/200 = 1 → Column 1 (middle)
Word at X=450 → 450/200 = 2 → Column 2 (right)
```

---

## Within-Column Processing

### Step 3: Sort Words Top-to-Bottom
```csharp
var columnWords = column
    .OrderByDescending(w => w.BoundingBox.Top)
    .ToList();
```
Within each column, sort words by their Y-coordinate (descending = top to bottom).

**Why descending?** PDF coordinate systems typically have Y=0 at the bottom, so higher Y values are at the top.

### Step 4: Group Words into Lines
```csharp
var lines = new List<List<UglyToad.PdfPig.Content.Word>>();
double lineHeightTolerance = 5;

foreach (var word in columnWords)
{
    var currentLine = lines.LastOrDefault();

    if (currentLine == null ||
        Math.Abs(currentLine.First().BoundingBox.Top - word.BoundingBox.Top) > lineHeightTolerance)
    {
        lines.Add(new List<UglyToad.PdfPig.Content.Word> { word });
    }
    else
    {
        currentLine.Add(word);
    }
}
```

**Algorithm:**
1. Check if there's a current line being built
2. If not, or if the word's Y-position differs by more than `lineHeightTolerance` (5 pixels):
   - Start a new line
3. Otherwise:
   - Add word to the current line

**Line Height Tolerance:** Words within 5 pixels vertically are considered on the same line. This handles minor vertical misalignments.

**Visual example:**
```
Word "Price:" at Y=100
Word "€1.99"  at Y=102  → Same line (|100-102|=2 < 5)
Word "Product" at Y=120 → New line (|102-120|=18 > 5)
```

---

## Text Assembly

### Step 5: Build Text for Each Line
```csharp
foreach (var line in lines)
{
    var lineWords = line.OrderBy(w => w.BoundingBox.Left);
    var lineText = string.Join(" ", lineWords.Select(w => w.Text));
    pageTextBuilder.AppendLine(lineText);
}
```

**Process:**
1. Within each line, sort words left-to-right by X-coordinate
2. Join words with spaces
3. Add line to the page text builder

### Step 6: Add Column Separator
```csharp
pageTextBuilder.AppendLine("---");
```
Adds `---` marker between columns to help AI distinguish column boundaries.

### Step 7: Finalize Page
```csharp
var pageText = pageTextBuilder.ToString().Trim();
if (!string.IsNullOrWhiteSpace(pageText))
{
    pages.Add(pageText);
}
```
Trim whitespace and add to pages array if not empty.

---

## Complete Flow Example

### Input PDF Structure:
```
┌─────────────────────────────────────────┐
│  PRODUCT A    │  PRODUCT D  │  PRODUCT G│
│  €1.99        │  €3.49      │  €2.99    │
│  500g         │  1kg        │  750g     │
├───────────────┼─────────────┼───────────┤
│  PRODUCT B    │  PRODUCT E  │  PRODUCT H│
│  €2.49        │  €4.99      │  €1.49    │
│  300g         │  2kg        │  200g     │
└─────────────────────────────────────────┘
```

### Output Text:
```
PRODUCT A
€1.99
500g
---
PRODUCT D
€3.49
1kg
---
PRODUCT G
€2.99
750g
---
PRODUCT B
€2.49
300g
---
PRODUCT E
€4.99
2kg
---
PRODUCT H
€1.49
200g
---
```

**Reading order:** Column 1 (top to bottom), Column 2 (top to bottom), Column 3 (top to bottom)

---

## Key Design Decisions

### Why Column-First Approach?
For multi-column brochures, reading top-to-bottom across all columns would mix unrelated products:
```
❌ Wrong: PRODUCT A, PRODUCT D, PRODUCT G, PRODUCT B, ...
✅ Right: PRODUCT A, PRODUCT B, PRODUCT C, PRODUCT D, ...
```

### Why Line Grouping?
Ensures product names, prices, and details stay together as a semantic unit.

### Why Column Separators?
Helps LLMs understand document structure and prevent cross-column confusion.

---

## Limitations & Tuning Parameters

### Adjustable Parameters:

1. **Number of Columns** (Line 125)
   ```csharp
   var columnWidth = pageWidth / 3.0; // Change 3.0 for different layouts
   ```

2. **Line Height Tolerance** (Line 140)
   ```csharp
   double lineHeightTolerance = 5; // Increase if lines are splitting incorrectly
   ```

### Known Limitations:

- **Fixed Column Count:** Assumes all pages have the same number of columns
- **Equal Column Widths:** Assumes columns are evenly spaced
- **No Image Text:** Doesn't extract text from images (OCR not included)
- **No Table Detection:** Tables may not extract cleanly

---

## Error Handling

```csharp
catch (Exception ex)
{
    System.Console.WriteLine($"Error extracting text from {filePath}: {ex.Message}");
}
```

Errors are logged to console but don't stop processing. Empty array is returned on failure.

---

## Dependencies

- **UglyToad.PdfPig**: PDF parsing library
- **System.Linq**: For grouping and sorting operations

---

## Usage Example

```csharp
var pages = ExtractTextFromPdfV3("brochure.pdf");

Console.WriteLine($"Extracted {pages.Length} pages");
foreach (var page in pages)
{
    Console.WriteLine(page);
    Console.WriteLine("=== END OF PAGE ===");
}
```

---

## Performance Considerations

- **Memory:** Loads entire page into memory as word list
- **Time Complexity:** O(n log n) per page (sorting operations)
- **Suitable For:** Small to medium PDFs (< 100 pages)

For large documents, consider processing pages in parallel or streaming.
