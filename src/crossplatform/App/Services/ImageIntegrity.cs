using SkiaSharp;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  Whether an image file that decoded is actually all there.
///
///  <para><b>Why this exists.</b> Every picture the port shows is decoded by Skia
///  (Avalonia's <c>Bitmap</c> is a thin wrapper over <c>SKBitmap.Decode</c>), and Skia
///  answers a truncated file the same way it answers an intact one: with a bitmap of
///  the full declared size, the missing rows left blank. Measured on a 32×32 sample cut
///  to 90/70/50/30% of its bytes, <c>SKBitmap.Decode</c> returned "ok 32×32" for PNG,
///  GIF and BMP at every one of those cuts, and for WEBP and JPEG down to 70% — no
///  exception, no flag, nothing in the returned object that differs from a whole file.
///  In an image diff that silence is the worst possible answer: half a picture next to
///  a whole one looks exactly like a change the author made, and the "N pixels differ"
///  count that this window puts under it is then a precise number about nothing.</para>
///
///  <para><see cref="SKCodec"/> is the same decoder with the result code left visible:
///  it returns <c>IncompleteInput</c> for exactly the files above, and <c>Success</c>
///  for the intact ones. So the question costs a second decode and no new library — it
///  is the decoder that is already in the process, merely asked properly.</para>
/// </summary>
public static class ImageIntegrity
{
    /// <summary>
    ///  Above this many pixels the question is not asked and the answer is "not
    ///  truncated".
    ///
    ///  <para>The check decodes a second time into a buffer of its own, four bytes a
    ///  pixel: at the cap that is 64 MB and a fraction of a second, and beyond it the
    ///  cost of the warning would start to rival the cost of the window. A file that
    ///  large is also one the user is unlikely to be diffing by eye. The consequence is
    ///  stated rather than hidden: a truncated 30-megapixel image is shown unlabelled,
    ///  as it was before this check existed.</para>
    /// </summary>
    private const long MaxPixels = 16L * 1024 * 1024;

    /// <summary>
    ///  <see langword="true"/> when <paramref name="bytes"/> decode into a picture but
    ///  the bytes ran out before the picture did.
    ///
    ///  <para><see langword="false"/> for everything else on purpose — data no codec
    ///  recognises (an SVG, a TIFF), data broken badly enough that the decode fails
    ///  outright, and an image too large to check. This answers one question only, and a
    ///  caller that shows a warning on it must not be told "maybe".</para>
    /// </summary>
    public static bool IsTruncated(byte[] bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return false;
        }

        try
        {
            using SKData data = SKData.CreateCopy(bytes);
            using SKCodec? codec = SKCodec.Create(data);
            if (codec is null)
            {
                return false;
            }

            int width = codec.Info.Width;
            int height = codec.Info.Height;
            if (width <= 0 || height <= 0 || (long)width * height > MaxPixels)
            {
                return false;
            }

            // Rgba8888/Premul rather than codec.Info: a codec whose native colour type is
            // one Skia cannot write into a plain buffer (index-8, for one) answers
            // Unimplemented, which would read here as "not truncated". The pixels are
            // thrown away — only the result code is wanted — so any type the decoder can
            // certainly produce will do.
            SKImageInfo info = new(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using SKBitmap bitmap = new(info);
            IntPtr pixels = bitmap.GetPixels();
            if (pixels == IntPtr.Zero)
            {
                return false;
            }

            // Deliberately not the incremental API, which can report how many rows
            // arrived: measured, it is Unimplemented for BMP, JPEG and WEBP, and for an
            // INTACT GIF it reports zero rows decoded. A count that is wrong for whole
            // files cannot be put in front of a user; the yes/no from GetPixels is right
            // for every format tried.
            return codec.GetPixels(info, pixels) == SKCodecResult.IncompleteInput;
        }
        catch (Exception)
        {
            // A decoder that throws has not established that the file is truncated, and
            // the caller that could not decode it at all already says so in its own
            // words. Never let a diagnostic take the window down.
            return false;
        }
    }
}
