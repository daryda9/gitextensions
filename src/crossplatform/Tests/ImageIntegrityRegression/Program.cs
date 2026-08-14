using GitExtensions.Avalonia.Services;
using SkiaSharp;

// Regression suite for ImageIntegrity.IsTruncated (App/Services/ImageIntegrity.cs),
// the check that stops the image diff from presenting half a file as if it were the
// file.
//
// Usage: dotnet run --project Tests/ImageIntegrityRegression/ImageIntegrityRegression.Harness.csproj
//
// Exit code 0 means every case held; any other value means at least one did not, and
// each is printed.
//
// WHY THIS EXISTS. The defect being guarded against is a silence: Skia decodes a
// truncated PNG into a full-size bitmap with the missing rows blank, and neither
// Avalonia's Bitmap nor anything downstream can tell that from an intact file. The
// only way that silence comes back is if the check stops answering — which no
// compiler and no eye on the screen would catch, since the window looks perfectly
// well when it is wrong.
//
// The central case is written as an INVARIANT rather than as a table of expected
// answers per format and per cut, because a table would be pinned to the exact byte
// offsets of these five samples and would have to be rewritten the day they change:
//
//     for every prefix of every sample, if Skia still decodes it into a bitmap,
//     IsTruncated must say so.
//
// That is exactly the user-visible contract — a picture that appears on screen is
// either whole or labelled — and it holds for any sample, any format and any cut. The
// prefixes that Skia refuses outright are counted and reported, not asserted about:
// the window already says "could not be decoded" for those, out of a different code
// path.

int checks = 0;
int decodable = 0;
int refused = 0;
List<string> failures = [];

foreach ((string format, byte[] bytes) in Samples.ByFormat)
{
    // An intact file must never be flagged. This is the case that a check which
    // simply returned true would pass the invariant below and fail here.
    Check(!ImageIntegrity.IsTruncated(bytes), $"{format}: the intact sample is not reported truncated");

    // A whole file with junk appended is not truncated either — it is the opposite
    // problem, and calling it truncated would be a lie the user cannot check.
    byte[] padded = [.. bytes, .. new byte[64]];
    Check(!ImageIntegrity.IsTruncated(padded), $"{format}: trailing bytes are not a truncation");

    // Up to 94% and no further, for a reason worth stating rather than hiding behind a
    // constant. Measured: cutting the last 2% off the 852-byte PNG removes its IEND
    // chunk and nothing else — every pixel arrived, Skia reports Success, and the
    // picture on screen IS the picture in the file. This check exists to say "what you
    // are looking at is not all of it", so a file missing only its end marker is
    // correctly not flagged, and the invariant below would be wrong to demand it.
    for (int cut = 2; cut <= 94; cut += 2)
    {
        byte[] prefix = bytes[..(bytes.Length * cut / 100)];
        bool skiaDecodes = Decodes(prefix);
        bool flagged = ImageIntegrity.IsTruncated(prefix);

        if (skiaDecodes)
        {
            decodable++;
            Check(flagged, $"{format} cut to {cut}%: decodes into a bitmap, so it must be reported truncated");
        }
        else
        {
            // Nothing is asserted about the answer here — the window says "could not
            // be decoded" for these out of a different code path — but the call is
            // still made, and made before this line, so a prefix that drove the check
            // into an exception would take the suite down rather than pass it.
            refused++;
        }
    }
}

// Data that is not an image at all, and data that is an image header with nothing
// behind it. Both reach this check in real use: the diff view offers the image window
// on the strength of a signature in the first bytes.
Check(!ImageIntegrity.IsTruncated([]), "empty input is not truncated");
Check(!ImageIntegrity.IsTruncated(new byte[512]), "512 zero bytes are not truncated");
Check(
    !ImageIntegrity.IsTruncated("<svg xmlns=\"http://www.w3.org/2000/svg\"><rect/></svg>"u8.ToArray()),
    "an SVG, which no codec here reads, is not truncated");
Check(
    !ImageIntegrity.IsTruncated([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
    "a PNG signature with no image behind it is not truncated");

// The eight-byte PNG signature above answers false because no codec can be built from
// it at all; a PNG whose header IS complete but whose pixel data is empty is the case
// that must answer true, and it is covered by the 2% cut of the PNG sample above.

// The other end of the same question, pinned explicitly rather than left to the sweep:
// a PNG with its 12-byte IEND chunk cut off is a damaged file, but not a partial
// picture — every pixel arrived. Flagging it would be a false alarm on a window whose
// only warning has to be believed.
byte[] png = Samples.ByFormat["PNG"];
byte[] withoutEnd = png[..^12];
Check(Decodes(withoutEnd), "a PNG without its IEND chunk still decodes");
Check(!ImageIntegrity.IsTruncated(withoutEnd), "a PNG missing only its end marker is not reported truncated");

if (failures.Count > 0)
{
    foreach (string failure in failures)
    {
        Console.Error.WriteLine("FAIL: " + failure);
    }

    Console.Error.WriteLine($"FAILED: {failures.Count} of {checks} cases");
    return 1;
}

Console.WriteLine(
    $"PASS: {checks} image-integrity cases ({decodable} truncated prefixes decoded and flagged, "
    + $"{refused} refused by the decoder)");
return 0;

// What Avalonia's Bitmap does internally, asked directly: this is the exact question
// "would a picture appear on screen for these bytes?".
static bool Decodes(byte[] bytes)
{
    try
    {
        using SKBitmap? bitmap = SKBitmap.Decode(bytes);
        return bitmap is not null && bitmap.Width > 0 && bitmap.Height > 0;
    }
    catch (Exception)
    {
        return false;
    }
}

void Check(bool value, string message)
{
    checks++;
    if (!value)
    {
        failures.Add(message);
    }
}
