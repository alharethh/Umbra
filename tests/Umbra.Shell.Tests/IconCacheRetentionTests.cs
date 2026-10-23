using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Umbra.Core.Tiers;
using Umbra.Shell.Rendering;
using Umbra.Shell.Wear;

namespace Umbra.Shell.Tests;

/// <summary>
/// A replaced icon must not outlive its replacement in the bar's caches.
/// </summary>
/// <remarks>
/// <para>
/// ManagedShell builds a new bitmap for a tray icon on every <c>NIF_ICON</c> message, and Explorer
/// re-sends the speaker icon on every volume change. <see cref="IconPipeline"/> and
/// <see cref="SceneReader"/> both cached by bitmap in a plain dictionary, so every superseded
/// icon and every render of it stayed alive for the life of the bar: about 65 KB of native WIC
/// memory per volume change, 310 MB after ~2,000. The managed heap stayed small throughout, which
/// is why nothing in it looked wrong.
/// </para>
/// <para>
/// Each test drops its own references and then asks the collector whether the bitmaps went. With
/// a strongly keyed cache they cannot: the cache is the reference.
/// </para>
/// </remarks>
public class IconCacheRetentionTests
{
    private const int Updates = 40;

    private static BitmapSource AnIcon(byte shade)
    {
        byte[] pixels = new byte[16 * 16 * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = pixels[i + 1] = pixels[i + 2] = shade;
            pixels[i + 3] = 255;
        }

        BitmapSource icon = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Pbgra32, null, pixels, 16 * 4);
        icon.Freeze();
        return icon;
    }

    private static void Collect()
    {
        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    // Not inlined, so no local in the test's own frame keeps a source alive past the call.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<WeakReference> RenderUpdates(IconPipeline pipeline)
    {
        List<WeakReference> sources = [];
        for (int i = 0; i < Updates; i++)
        {
            BitmapSource source = AnIcon((byte)(i * 5));
            sources.Add(new WeakReference(source));

            // Two levels, as a hover would ask for: both renders hang off the one source.
            Assert.NotNull(pipeline.Render(source, BarElement.TrayIcon, 0.0));
            Assert.NotNull(pipeline.Render(source, BarElement.TrayIcon, 2.0));
        }

        return sources;
    }

    [Fact]
    public void A_Replaced_Tray_Icon_Is_Not_Held_By_The_Render_Cache()
    {
        IconPipeline pipeline = new();
        List<WeakReference> sources = RenderUpdates(pipeline);

        Collect();

        Assert.Equal(0, sources.Count(s => s.IsAlive));
        GC.KeepAlive(pipeline);
    }

    [Fact]
    public void An_Icon_Still_Drawn_Keeps_Its_Render()
    {
        // The other half: weak keys must not cost the cache its point. A source that is still in
        // use renders once per level and is served from the cache after a collection.
        IconPipeline pipeline = new();
        BitmapSource source = AnIcon(200);

        BitmapSource? first = pipeline.Render(source, BarElement.TrayIcon, 0.0);
        Collect();
        BitmapSource? second = pipeline.Render(source, BarElement.TrayIcon, 0.0);

        Assert.NotNull(first);
        Assert.Same(first, second);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<WeakReference> SampleUpdates()
    {
        List<WeakReference> bitmaps = [];
        for (int i = 0; i < Updates; i++)
        {
            BitmapSource bitmap = AnIcon((byte)(i * 5));
            bitmaps.Add(new WeakReference(bitmap));
            Assert.NotNull(SceneReader.IconField(bitmap));
        }

        return bitmaps;
    }

    [Fact]
    public void A_Replaced_Icon_Is_Not_Held_By_The_Wear_Sampler()
    {
        List<WeakReference> bitmaps = SampleUpdates();

        Collect();

        Assert.Equal(0, bitmaps.Count(b => b.IsAlive));
    }

    [Fact]
    public void The_Wear_Sampler_Reuses_A_Field_While_The_Icon_Lives()
    {
        BitmapSource bitmap = AnIcon(120);

        LuminanceField first = SceneReader.IconField(bitmap);
        Collect();

        Assert.Same(first, SceneReader.IconField(bitmap));
    }
}
