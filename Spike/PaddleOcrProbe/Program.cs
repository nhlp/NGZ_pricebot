using ClosedXML.Excel;

// testFOt1 Excel'i düzeltilmiş fallback ile tekrar oku -- kod sütunu artık dogru mu?
var excelPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "samples", "testfot1.xlsx");
Console.WriteLine($"Excel: {Path.GetFileName(excelPath)}\n");

var candidates = ExcelPriceReader.LoadCandidateCodeColumns(excelPath);
Console.WriteLine($"{candidates.Count} aday sutun bulundu:\n");
foreach (var c in candidates)
{
    Console.WriteLine($"  '{c.HeaderName}' (sutun {c.ColumnNumber}, oncelik={c.HeaderPriority}, urun={c.Prices.Count})");
    foreach (var kv in c.Prices.Take(5))
        Console.WriteLine($"      {kv.Key} -> {kv.Value}");
}

Console.WriteLine();
var expectedCodes = new[] { "8511", "8484", "8522", "8479", "8477", "8516" }; // gercek gorsellerde okunanlar
foreach (var code in expectedCodes)
{
    var found = candidates.Any(c => c.Prices.ContainsKey(code));
    Console.WriteLine($"  {(found ? "[VAR]" : "[YOK]")} kod {code} herhangi bir adayda bulunuyor mu?");
}
