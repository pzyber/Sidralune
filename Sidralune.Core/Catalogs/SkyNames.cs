using System;
using System.Collections.Generic;
using System.Linq;
using Sidralune.Core.Util;
using static Sidralune.Core.Util.Lang;

namespace Sidralune.Core.Catalogs
{
    /// <summary>
    /// Constellation names and the nicknames of well-known deep-sky objects in the interface language ("Stora björnen",
    /// "Virvelgalaxen"). Only what is shown changes: the catalogues, saved files, reports and scripts keep the original
    /// names. Translations are matched on the catalogue's own name, so a name the catalogue rewords, or one that is not in
    /// the list, simply shows as the catalogue has it; the lists are checked against the catalogues by the tests.
    /// </summary>
    public static class SkyNames
    {
        /// <summary>Show the names in the interface language (Settings → Appearance); off shows the Latin and English names.</summary>
        public static bool InLanguage = true;

        public const string ConstellationContext = "constellation", ObjectContext = "object name";

        /// <summary>The 88 constellations by their IAU names, as in the built-in data, marked for translation.</summary>
        public static readonly string[] Constellations =
        {
            Nc("constellation", "Andromeda"), Nc("constellation", "Antlia"), Nc("constellation", "Apus"), Nc("constellation", "Aquarius"),
            Nc("constellation", "Aquila"), Nc("constellation", "Ara"), Nc("constellation", "Aries"), Nc("constellation", "Auriga"),
            Nc("constellation", "Boötes"), Nc("constellation", "Caelum"), Nc("constellation", "Camelopardalis"), Nc("constellation", "Cancer"),
            Nc("constellation", "Canes Venatici"), Nc("constellation", "Canis Major"), Nc("constellation", "Canis Minor"), Nc("constellation", "Capricornus"),
            Nc("constellation", "Carina"), Nc("constellation", "Cassiopeia"), Nc("constellation", "Centaurus"), Nc("constellation", "Cepheus"),
            Nc("constellation", "Cetus"), Nc("constellation", "Chamaeleon"), Nc("constellation", "Circinus"), Nc("constellation", "Columba"),
            Nc("constellation", "Coma Berenices"), Nc("constellation", "Corona Australis"), Nc("constellation", "Corona Borealis"), Nc("constellation", "Corvus"),
            Nc("constellation", "Crater"), Nc("constellation", "Crux"), Nc("constellation", "Cygnus"), Nc("constellation", "Delphinus"),
            Nc("constellation", "Dorado"), Nc("constellation", "Draco"), Nc("constellation", "Equuleus"), Nc("constellation", "Eridanus"),
            Nc("constellation", "Fornax"), Nc("constellation", "Gemini"), Nc("constellation", "Grus"), Nc("constellation", "Hercules"),
            Nc("constellation", "Horologium"), Nc("constellation", "Hydra"), Nc("constellation", "Hydrus"), Nc("constellation", "Indus"),
            Nc("constellation", "Lacerta"), Nc("constellation", "Leo"), Nc("constellation", "Leo Minor"), Nc("constellation", "Lepus"),
            Nc("constellation", "Libra"), Nc("constellation", "Lupus"), Nc("constellation", "Lynx"), Nc("constellation", "Lyra"),
            Nc("constellation", "Mensa"), Nc("constellation", "Microscopium"), Nc("constellation", "Monoceros"), Nc("constellation", "Musca"),
            Nc("constellation", "Norma"), Nc("constellation", "Octans"), Nc("constellation", "Ophiuchus"), Nc("constellation", "Orion"),
            Nc("constellation", "Pavo"), Nc("constellation", "Pegasus"), Nc("constellation", "Perseus"), Nc("constellation", "Phoenix"),
            Nc("constellation", "Pictor"), Nc("constellation", "Pisces"), Nc("constellation", "Piscis Austrinus"), Nc("constellation", "Puppis"),
            Nc("constellation", "Pyxis"), Nc("constellation", "Reticulum"), Nc("constellation", "Sagitta"), Nc("constellation", "Sagittarius"),
            Nc("constellation", "Scorpius"), Nc("constellation", "Sculptor"), Nc("constellation", "Scutum"), Nc("constellation", "Serpens"),
            Nc("constellation", "Sextans"), Nc("constellation", "Taurus"), Nc("constellation", "Telescopium"), Nc("constellation", "Triangulum"),
            Nc("constellation", "Triangulum Australe"), Nc("constellation", "Tucana"), Nc("constellation", "Ursa Major"), Nc("constellation", "Ursa Minor"),
            Nc("constellation", "Vela"), Nc("constellation", "Virgo"), Nc("constellation", "Volans"), Nc("constellation", "Vulpecula")
        };

        /// <summary>
        /// The nicknames that have a name of their own in other languages, spelt as the built-in data or OpenNGC has them
        /// (both spellings when they differ). Designations such as Centaurus A or Omega Centauri, and names without an
        /// established translation, are left out and show as the catalogue has them.
        /// </summary>
        public static readonly string[] Nicknames =
        {
            // galaxies
            Nc("object name", "Andromeda Galaxy"), Nc("object name", "Triangulum Galaxy"), Nc("object name", "Whirlpool Galaxy"),
            Nc("object name", "Sombrero Galaxy"), Nc("object name", "Pinwheel Galaxy"), Nc("object name", "Southern Pinwheel"),
            Nc("object name", "Southern Pinwheel Galaxy"), Nc("object name", "Cigar Galaxy"), Nc("object name", "Bode's Galaxy"),
            Nc("object name", "Black Eye Galaxy"), Nc("object name", "Sunflower Galaxy"), Nc("object name", "Needle Galaxy"),
            Nc("object name", "Whale Galaxy"), Nc("object name", "Antennae Galaxies"), Nc("object name", "Fireworks Galaxy"),
            Nc("object name", "Spindle Galaxy"), Nc("object name", "Phantom Galaxy"), Nc("object name", "Cat's Eye Galaxy"),
            Nc("object name", "Hamburger Galaxy"), Nc("object name", "Large Magellanic Cloud"), Nc("object name", "Small Magellanic Cloud"),
            Nc("object name", "Stephan's Quintet"),
            // nebulae
            Nc("object name", "Orion Nebula"), Nc("object name", "Great Orion Nebula"), Nc("object name", "De Mairan's Nebula"),
            Nc("object name", "Mairan's Nebula"), Nc("object name", "Crab Nebula"), Nc("object name", "Lagoon Nebula"),
            Nc("object name", "Trifid Nebula"), Nc("object name", "Eagle Nebula"), Nc("object name", "Omega Nebula"),
            Nc("object name", "Checkmark Nebula"), Nc("object name", "Ring Nebula"), Nc("object name", "Dumbbell Nebula"),
            Nc("object name", "Little Dumbbell"), Nc("object name", "Barbell Nebula"), Nc("object name", "Owl Nebula"),
            Nc("object name", "Helix Nebula"), Nc("object name", "Cat's Eye Nebula"), Nc("object name", "Eskimo Nebula"),
            Nc("object name", "Saturn Nebula"), Nc("object name", "Blue Snowball"), Nc("object name", "Copeland's Blue Snowball"),
            Nc("object name", "Blinking Planetary"), Nc("object name", "Jupiter's Ghost Nebula"), Nc("object name", "Bug Nebula"),
            Nc("object name", "Rosette Nebula"), Nc("object name", "Horsehead Nebula"), Nc("object name", "Flame Nebula"),
            Nc("object name", "North America Nebula"), Nc("object name", "Pelican Nebula"), Nc("object name", "Veil Nebula"),
            Nc("object name", "Western Veil Nebula"), Nc("object name", "Eastern Veil Nebula"), Nc("object name", "Eastern Veil"),
            Nc("object name", "Heart Nebula"), Nc("object name", "Soul Nebula"), Nc("object name", "Bubble Nebula"),
            Nc("object name", "California Nebula"), Nc("object name", "Crescent Nebula"), Nc("object name", "Cocoon Nebula"),
            Nc("object name", "Tarantula Nebula"), Nc("object name", "Carina Nebula"), Nc("object name", "Witch Head Nebula"),
            Nc("object name", "the Witch Head Nebula"), Nc("object name", "Running Man Nebula"), Nc("object name", "the Running Man Nebula"),
            Nc("object name", "Cone Nebula & Christmas Tree"), Nc("object name", "Christmas Tree Cluster"), Nc("object name", "Elephant's Trunk Nebula"),
            Nc("object name", "Flaming Star Nebula"), Nc("object name", "Thor's Helmet"), Nc("object name", "Iris Nebula"),
            Nc("object name", "Cave Nebula"), Nc("object name", "Pacman Nebula"), Nc("object name", "Wizard Nebula"),
            Nc("object name", "Cat's Paw Nebula"), Nc("object name", "Tadpoles Nebula"), Nc("object name", "Monkey Head Nebula"),
            Nc("object name", "Coalsack Nebula"),
            // clusters and star clouds
            Nc("object name", "Pleiades"), Nc("object name", "Hyades"), Nc("object name", "Beehive Cluster"), Nc("object name", "Beehive"),
            Nc("object name", "Double Cluster"), Nc("object name", "Wild Duck Cluster"), Nc("object name", "Amas de l'Ecu de Sobieski"),
            Nc("object name", "Butterfly Cluster"), Nc("object name", "Ptolemy Cluster"), Nc("object name", "Ptolemy's Cluster"),
            Nc("object name", "Owl Cluster"), Nc("object name", "Herschel's Jewel Box"), Nc("object name", "Hercules Cluster"),
            Nc("object name", "Hercules Globular Cluster"), Nc("object name", "Coathanger"), Nc("object name", "Coma Star Cluster"),
            Nc("object name", "Sagittarius Star Cloud"), Nc("object name", "Small Sgr Star Cloud")
        };

        /// <summary>A constellation's name as shown: in the interface language, or its IAU name.</summary>
        public static string ConstellationName(Constellation c) => c == null ? null : ConstellationName(c.Name);

        /// <summary>A constellation's IAU name ("Ursa Major") as shown.</summary>
        public static string ConstellationName(string iauName) => string.IsNullOrEmpty(iauName) || !InLanguage ? iauName : Tc(ConstellationContext, iauName);

        /// <summary>An object's nickname from a catalogue ("Whirlpool Galaxy") as shown; names not in the list stay as they are.</summary>
        public static string Nickname(string name) => string.IsNullOrEmpty(name) || !InLanguage ? name : Tc(ObjectContext, name);

        // the shown names that differ from the originals, (shown, original), for the language in use
        private static string _pairsFor;
        private static List<KeyValuePair<string, string>> _constellationPairs, _nicknamePairs;
        private static readonly object _lock = new object();

        private static void Pairs(out List<KeyValuePair<string, string>> constellations, out List<KeyValuePair<string, string>> nicknames)
        {
            lock (_lock)
            {
                string key = Generation + "|" + InLanguage;
                if (_pairsFor != key)
                {
                    _constellationPairs = Constellations.Select(n => new KeyValuePair<string, string>(ConstellationName(n), n)).Where(p => p.Key != p.Value).ToList();
                    _nicknamePairs = Nicknames.Select(n => new KeyValuePair<string, string>(Nickname(n), n)).Where(p => p.Key != p.Value).ToList();
                    _pairsFor = key;
                }
                constellations = _constellationPairs;
                nicknames = _nicknamePairs;
            }
        }

        /// <summary>The catalogue's nicknames shown as a text in the interface language: exactly, or (partial) starting with or containing it.</summary>
        public static List<string> OriginalNicknames(string shown, bool partial)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(shown)) return result;
            string t = shown.Trim();
            List<KeyValuePair<string, string>> c, n;
            Pairs(out c, out n);
            foreach (var p in n)
                if (string.Equals(p.Key, t, StringComparison.CurrentCultureIgnoreCase)
                    || partial && (p.Key.StartsWith(t, StringComparison.CurrentCultureIgnoreCase) || t.Length >= 3 && Contains(p.Key, t)))
                    result.Add(p.Value);
            return result;
        }

        /// <summary>The constellation named by a text: its abbreviation ("UMa"), IAU name or the name shown ("Stora björnen"), else null.</summary>
        public static Constellation FindConstellation(IEnumerable<Constellation> all, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string t = text.Trim();
            List<KeyValuePair<string, string>> c, n;
            Pairs(out c, out n);
            string iau = c.Where(p => string.Equals(p.Key, t, StringComparison.CurrentCultureIgnoreCase)).Select(p => p.Value).FirstOrDefault() ?? t;
            return all.FirstOrDefault(x => string.Equals(x.Abbr, t, StringComparison.Ordinal))
                ?? all.FirstOrDefault(x => string.Equals(x.Name, iau, StringComparison.OrdinalIgnoreCase) || string.Equals(x.Name, t, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The constellations whose shown or IAU name starts with or contains a text (three letters or more), best first.</summary>
        public static List<Constellation> SearchConstellations(IEnumerable<Constellation> all, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new List<Constellation>();
            string t = text.Trim();
            var exact = FindConstellation(all, t);
            return all.Select(x => new { x, s = exact == x ? 0 : Score(ConstellationName(x), t, x.Name) })
                .Where(p => p.s >= 0).OrderBy(p => p.s).Select(p => p.x).ToList();
        }

        private static int Score(string shown, string t, string iau)
        {
            int best = -1;
            foreach (var name in new[] { shown, iau })
            {
                int s = name.StartsWith(t, StringComparison.CurrentCultureIgnoreCase) ? 1 : t.Length >= 3 && Contains(name, t) ? 2 : -1;
                if (s >= 0 && (best < 0 || s < best)) best = s;
            }
            return best;
        }

        private static bool Contains(string text, string part) => text.IndexOf(part, StringComparison.CurrentCultureIgnoreCase) >= 0;
    }
}
