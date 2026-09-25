using System;
using System.IO;
using OpenSimNGC.Appearance.Baking;
using SkiaSharp;

namespace OpenSim.Region.OptionalModules.Avatar.AppearanceImport;

/// <summary>
/// Turns an image file into the bytes of a texture asset (a raw JPEG 2000 codestream), the way a viewer upload does:
/// scaled to power-of-two sides no larger than <c>maxSize</c>, alpha kept only when the image uses it.
/// Uses the baking library's decoders and <see cref="J2kCodec"/>, so no new dependency.
/// </summary>
public static class TextureFileEncoder
{
    public const int DefaultMaxSize = 1024;

    /// <summary>Encode <paramref name="path"/>. .j2c/.j2k/.jp2 files are passed through unchanged.</summary>
    /// <exception cref="InvalidDataException">The file is not an image this can read.</exception>
    public static byte[] Encode(string path, int maxSize = DefaultMaxSize, double quality = 0.9)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var data = File.ReadAllBytes(path);
        if (ext is ".j2c" or ".j2k" or ".jp2")
            return data;

        var planes = ext == ".tga" ? FromTga(data) : FromSkia(data, path);
        int w = PowerOfTwo(planes.W, maxSize), h = PowerOfTwo(planes.H, maxSize);
        planes = planes.Resample(w, h);
        return J2kCodec.Encode(planes, quality);
    }

    /// <summary>The nearest power of two to <paramref name="n"/>, between 4 and <paramref name="max"/>.</summary>
    public static int PowerOfTwo(int n, int max)
    {
        var p = 4;
        while (p < max && p * 2 <= n) p *= 2;
        // round up when n is nearer the next power than this one
        if (p < max && n - p > p * 2 - n) p *= 2;
        return Math.Min(p, max);
    }

    private static RgbaPlanes FromSkia(byte[] data, string path)
    {
        using var stream = new SKMemoryStream(data);
        using var codec = SKCodec.Create(stream) ?? throw new InvalidDataException($"{path}: not an image format this simulator can read (png, jpg, webp, bmp, gif, tga, j2c)");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bmp = SKBitmap.Decode(codec, info) ?? throw new InvalidDataException($"{path}: could not decode the image");

        var px = bmp.Pixels;
        var hasAlpha = false;
        foreach (var c in px) if (c.Alpha != 255) { hasAlpha = true; break; }

        var planes = new RgbaPlanes(bmp.Width, bmp.Height, hasAlpha);
        for (var i = 0; i < px.Length; i++)
        {
            planes.R[i] = px[i].Red; planes.G[i] = px[i].Green; planes.B[i] = px[i].Blue;
            if (hasAlpha) planes.A[i] = px[i].Alpha;
        }
        return planes;
    }

    private static RgbaPlanes FromTga(byte[] data)
    {
        TgaImage tga;
        try { tga = Tga.Decode(data); }
        catch (FormatException e) { throw new InvalidDataException(e.Message, e); }

        // A grey file carries its values in R only.
        byte[] r = tga.R, g = tga.IsGray || tga.G is null ? tga.R : tga.G, b = tga.IsGray || tga.B is null ? tga.R : tga.B;
        var hasAlpha = tga.HasAlpha && tga.A is not null && Array.Exists(tga.A, a => a != 255);
        var planes = new RgbaPlanes(tga.W, tga.H, hasAlpha);
        Buffer.BlockCopy(r, 0, planes.R, 0, planes.R.Length);
        Buffer.BlockCopy(g, 0, planes.G, 0, planes.G.Length);
        Buffer.BlockCopy(b, 0, planes.B, 0, planes.B.Length);
        if (hasAlpha) Buffer.BlockCopy(tga.A, 0, planes.A, 0, planes.A.Length);
        return planes;
    }
}
