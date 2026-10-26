using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DeadlockTempo
{
    // Object cutouts and original NPC portraits; provenance is in icons/SOURCES.txt.
    // Embedded for offline use: the running overlay never reads the game archive.
    internal static class GameIcons
    {
        private static readonly Dictionary<string, BitmapSource> cache = new Dictionary<string, BitmapSource>();
        private static string Resource(string id)
        {
            switch (id)
            {
                case "boxes": case "tunnels": case "midboxes": return "object-crate.png";
                case "small": return "neutral_plant_weak_psd.png";
                case "early": return "neutral_book_weak_psd.png";
                case "medium": return "neutral_plant_normal_psd.png";
                case "large": return "neutral_plant_heavy_psd.png";
                case "machines": return "object-machine.png";
                case "runes": return "object-rune.png";
                case "rift": return "object-rift.png";
                case "urn": return "soul_jar_marker_psd.png";
                case "midboss": return "object-midboss.png";
                case "lane": return "trooper_psd.png";
                case "snacks": return "health_pad_psd.png";
                default: return null;
            }
        }
        private static Int32Rect Viewport(string name)
        {
            // Native viewports retain the actual source pixels and their proportions.
            // Focus the machine on its marquee/display and the rune on the floating stone.
            switch (name)
            {
                case "object-crate.png": return new Int32Rect(25, 60, 640, 650);
                case "object-machine.png": return new Int32Rect(100, 20, 710, 940);
                case "object-rune.png": return new Int32Rect(185, 28, 300, 365);
                case "object-rift.png": return new Int32Rect(810, 395, 300, 300);
                default: return Int32Rect.Empty;
            }
        }
        internal static BitmapSource Get(string id)
        {
            string name = Resource(id);
            if (name == null) return null;
            BitmapSource found;
            if (cache.TryGetValue(name, out found)) return found;
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("GameIcon." + name))
            {
                if (stream == null) throw new InvalidOperationException("Missing game icon: " + name);
                var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream; image.EndInit(); image.Freeze();
                Int32Rect viewport = Viewport(name);
                if (!viewport.IsEmpty)
                {
                    found = new CroppedBitmap(image, viewport); found.Freeze();
                    cache[name] = found; return found;
                }
                // Remove only empty canvas around the original art; retain its colors and shape.
                var rgba = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0); rgba.Freeze();
                int width = rgba.PixelWidth, height = rgba.PixelHeight, stride = width * 4;
                var pixels = new byte[stride * height]; rgba.CopyPixels(pixels, stride, 0);
                int left = width, top = height, right = -1, bottom = -1;
                for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                    if (pixels[y * stride + x * 4 + 3] > 0)
                    { left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y); }
                if (right < left) throw new InvalidOperationException("Empty game icon: " + name);
                found = new CroppedBitmap(rgba, new Int32Rect(left, top, right - left + 1, bottom - top + 1));
                found.Freeze(); cache[name] = found; return found;
            }
        }
    }
}
