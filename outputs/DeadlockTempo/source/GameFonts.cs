using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace DeadlockTempo
{
    internal static class GameFonts
    {
        // Optional local fonts, never copied into the distributable.
        internal static void Load(Application app)
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Steam\steamapps\common\Deadlock\game\citadel\panorama\fonts");
            LoadOne(app, folder, "valvepulp-bold.ttf", "DisplayFont");
            LoadOne(app, folder, "valveoracle-medium.ttf", "BodyFont");
        }
        private static void LoadOne(Application app, string folder, string file, string key)
        {
            try
            {
                string path = Path.Combine(folder, file);
                if (!File.Exists(path)) return;
                var glyph = new GlyphTypeface(new Uri(path));
                string family = glyph.FamilyNames.Values.First();
                app.Resources[key] = new FontFamily(new Uri(folder + Path.DirectorySeparatorChar), "./#" + family);
            }
            catch { /* The system font remains available if the game is absent. */ }
        }
    }
}
