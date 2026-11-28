using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Sidralune.Imaging.Planetary
{
    /// <summary>How filter stacks were combined.</summary>
    public enum FilterCombination { Single, Rgb, LRgb, ColourWithLuminance, Separate }

    /// <summary>The filters of planetary images taken with a mono camera, and colour from them.</summary>
    public static class PlanetaryFilters
    {
        /// <summary>The codes: a colour camera's images, then the filters.</summary>
        public const string Colour = "RGB", L = "L", R = "R", G = "G", B = "B", IR = "IR", CH4 = "CH4", UV = "UV";

        public static readonly string[] All = { Colour, L, R, G, B, IR, CH4, UV };

        private static readonly Dictionary<string, string> Words = new Dictionary<string, string>
        {
            { "r", R }, { "red", R }, { "g", G }, { "green", G }, { "b", B }, { "blue", B },
            { "l", L }, { "lum", L }, { "luminance", L }, { "clear", L }, { "ch4", CH4 }, { "methane", CH4 }, { "uv", UV }
        };

        /// <summary>
        /// The filter of an image: a colour image is Colour; a mono one's filter is read from its file name, the last
        /// filter word in it ("R", "Red", "IR", "IR685", "L", "CH4"…), as FireCapture puts the observer's initial first
        /// (2026-11-28-2130_4-B-R-Jup: observer B, filter R). L when the name names none.
        /// </summary>
        public static string FromImage(string path, int channels)
        {
            if (channels >= 3) return Colour;
            string found = null;
            foreach (var word in Regex.Split(Path.GetFileNameWithoutExtension(path ?? "").ToLowerInvariant(), @"[^a-z0-9]+"))
            {
                string f;
                if (Words.TryGetValue(word, out f)) found = f;
                else if (Regex.IsMatch(word, @"^ir(\d{3,4}|pass)?$")) found = IR;
            }
            return found ?? L;
        }

        /// <summary>
        /// Colour from stacks of one moment by filter: the colour images' stack, or R, G and B when all three are there;
        /// with L as its luminance, or IR when there is no L. Without colour the stacks cannot be combined
        /// (Separate), and with one filter there is nothing to combine (Single); combined is then null.
        /// </summary>
        public static AstroImage Combine(IDictionary<string, AstroImage> stacks, out FilterCombination how, out string luminance)
        {
            luminance = null;
            if (stacks.Count == 1) { how = FilterCombination.Single; return stacks.Values.First(); }
            AstroImage colour = null, lum = null;
            AstroImage c, r, g, b;
            bool rgb = false;
            if (stacks.TryGetValue(Colour, out c)) colour = c;
            else if (stacks.TryGetValue(R, out r) && stacks.TryGetValue(G, out g) && stacks.TryGetValue(B, out b))
            {
                colour = new AstroImage(r.Width, r.Height, new[] { (float[])r.Data[0].Clone(), (float[])g.Data[0].Clone(), (float[])b.Data[0].Clone() });
                rgb = true;
            }
            if (colour == null) { how = FilterCombination.Separate; return null; }
            foreach (var code in new[] { L, IR })
                if (lum == null && stacks.TryGetValue(code, out c)) { lum = c; luminance = code; }
            if (lum == null) { how = FilterCombination.Rgb; return colour; }
            how = rgb ? FilterCombination.LRgb : FilterCombination.ColourWithLuminance;
            var mono = lum.Channels == 1 ? lum : new AstroImage(lum.Width, lum.Height, new[] { lum.Luminance() });
            return ColorOps.LrgbCombine(mono, colour, 1.0, 1.0);
        }
    }
}
