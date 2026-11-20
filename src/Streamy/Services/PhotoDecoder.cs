using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Streamy.Services;

/// <summary>
/// Decodes photos with Windows' own imaging (WIC through Windows.Graphics.Imaging): JPEG, PNG, GIF, BMP, TIFF, and
/// HEIC, WebP, AVIF or RAW when Windows has those extensions. Turns the picture the way the camera recorded
/// (EXIF orientation) and scales it down to fit a size, so a 50-megapixel photo does not need 200 MB of memory.
/// </summary>
public static class PhotoDecoder
{
    /// <exception cref="NotSupportedException">Windows has no decoder for this format (e.g. HEIC without the HEIF extension).</exception>
    public static async Task<Bitmap> DecodeAsync(string file, int maxSide)
    {
        StorageFile storage = await StorageFile.GetFileFromPathAsync(file);
        using IRandomAccessStream stream = await storage.OpenReadAsync();
        BitmapDecoder decoder;
        try
        {
            decoder = await BitmapDecoder.CreateAsync(stream);
        }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x88982F50)) // WINCODEC_ERR_COMPONENTNOTFOUND
        {
            throw new NotSupportedException("Windows cannot open this kind of picture.", ex);
        }

        // Size after orientation: a portrait photo stored sideways swaps width and height.
        uint w = decoder.OrientedPixelWidth, h = decoder.OrientedPixelHeight;
        double scale = Math.Min(1.0, (double)maxSide / Math.Max(w, h));
        var transform = new BitmapTransform
        {
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        // The transform scales the stored (unrotated) picture, so scale its own dimensions.
        transform.ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale));
        transform.ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale));

        using SoftwareBitmap soft = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);

        var bmp = new Bitmap(soft.PixelWidth, soft.PixelHeight, PixelFormat.Format32bppPArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var buffer = new Windows.Storage.Streams.Buffer((uint)(soft.PixelWidth * soft.PixelHeight * 4));
            soft.CopyToBuffer(buffer);
            byte[] bytes = new byte[buffer.Length];
            using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(bytes);
            int row = soft.PixelWidth * 4;
            for (int y = 0; y < soft.PixelHeight; y++)
                Marshal.Copy(bytes, y * row, data.Scan0 + y * data.Stride, row);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return bmp;
    }
}
