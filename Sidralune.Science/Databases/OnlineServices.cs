using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using Sidralune.Core.Ephemeris;
using Sidralune.Core.Mathematics;
using Sidralune.Core.MinorBodies;
using Sidralune.Core.Util;

namespace Sidralune.Science.Databases
{
    /// <summary>A generic table result from TAP/CSV services.</summary>
    public class DataTable
    {
        public List<string> Columns = new List<string>();
        public List<string[]> Rows = new List<string[]>();
        public int Col(string name) => Columns.FindIndex(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));
        public string Get(int row, string col) { int c = Col(col); return c >= 0 && c < Rows[row].Length ? Rows[row][c] : null; }
        public double GetDouble(int row, string col) => AstroMath.ParseInvariant(Get(row, col));

        public static DataTable FromCsv(string text, char sep = ',')
        {
            var t = new DataTable();
            using (var r = new CsvReader(new System.IO.StringReader(text), sep))
            {
                if (r.Header == null) return t;
                t.Columns.AddRange(r.Header);
                string[] row;
                while ((row = r.ReadRow()) != null) if (row.Length > 1 || (row.Length == 1 && row[0].Length > 0)) t.Rows.Add(row);
            }
            return t;
        }
    }

    public class ResolvedName
    {
        public string Name, Type, SpectralType;
        public double RaDeg = double.NaN, DecDeg = double.NaN, Vmag = double.NaN;
        public List<string> Aliases = new List<string>();
    }

    /// <summary>Clients for astronomical web services. All calls are synchronous; run them off the UI thread.</summary>
    public static class OnlineServices
    {
        public static string SimbadTap = "https://simbad.cds.unistra.fr/simbad/sim-tap/sync";
        public static string VizierTap = "https://tapvizier.cds.unistra.fr/TAPVizieR/tap/sync";
        public static string ExoplanetTap = "https://exoplanetarchive.ipac.caltech.edu/TAP/sync";
        public static string Horizons = "https://ssd.jpl.nasa.gov/api/horizons.api";
        public static string Sbdb = "https://ssd-api.jpl.nasa.gov/sbdb.api";
        public static string Cad = "https://ssd-api.jpl.nasa.gov/cad.api";

        /// <summary>CDS Sesame name resolver (SIMBAD, NED, VizieR).</summary>
        public static ResolvedName Sesame(string name)
        {
            string xml = Net.GetString("https://cds.unistra.fr/cgi-bin/nph-sesame/-oxp/SNV?" + Net.UrlEncode(name), 30000);
            var doc = XDocument.Parse(xml);
            var res = doc.Descendants("Resolver").FirstOrDefault(r => r.Element("jradeg") != null);
            if (res == null) return null;
            var r0 = new ResolvedName
            {
                Name = (string)res.Element("oname") ?? name,
                Type = (string)res.Element("otype"),
                RaDeg = AstroMath.ParseInvariant((string)res.Element("jradeg")),
                DecDeg = AstroMath.ParseInvariant((string)res.Element("jdedeg")),
                SpectralType = (string)res.Element("spType")
            };
            var mag = res.Elements("mag").FirstOrDefault(m => (string)m.Attribute("band") == "V");
            if (mag != null) r0.Vmag = AstroMath.ParseInvariant((string)mag.Element("v"));
            foreach (var a in res.Elements("alias")) r0.Aliases.Add((string)a);
            return r0;
        }

        public static DataTable Tap(string endpoint, string adql, int timeoutMs = 120000)
        {
            string url = endpoint + "?REQUEST=doQuery&LANG=ADQL&FORMAT=csv&QUERY=" + Net.UrlEncode(adql);
            return DataTable.FromCsv(Net.GetString(url, timeoutMs));
        }

        public static DataTable SimbadQuery(string adql) => Tap(SimbadTap, adql);
        public static DataTable VizierQuery(string adql) => Tap(VizierTap, adql);

        /// <summary>SIMBAD objects within radius (deg) with basic data.</summary>
        public static DataTable SimbadCone(double raDeg, double decDeg, double radiusDeg, int top = 200) => SimbadQuery(string.Format(CultureInfo.InvariantCulture,
            "SELECT TOP {3} main_id, ra, dec, otype_txt, sp_type, flux FROM basic LEFT JOIN flux ON oid = oidref AND filter = 'V' WHERE CONTAINS(POINT('ICRS', ra, dec), CIRCLE('ICRS', {0}, {1}, {2})) = 1 ORDER BY flux",
            raDeg, decDeg, radiusDeg, top));

        /// <summary>Confirmed exoplanets (composite parameters) brighter than a host V magnitude, with transit ephemerides.</summary>
        public static DataTable Exoplanets(double maxVmag = 13, bool transitingOnly = true)
        {
            string q = "select pl_name,hostname,ra,dec,sy_vmag,pl_orbper,pl_tranmid,pl_trandur,pl_trandep,pl_ratror,pl_rade,pl_radj,pl_bmassj,st_rad,st_teff,disc_year,discoverymethod from pscomppars where sy_vmag < "
                + maxVmag.ToString(CultureInfo.InvariantCulture) + (transitingOnly ? " and tran_flag = 1" : "");
            string url = ExoplanetTap + "?query=" + Net.UrlEncode(q) + "&format=csv";
            return DataTable.FromCsv(Net.GetString(url, 180000));
        }

        /// <summary>JPL Horizons observer ephemeris (text table between $$SOE and $$EOE).</summary>
        public static List<string> HorizonsEphemeris(string command, string startUtc, string stopUtc, string step, string site = "500@399", string quantities = "1,9,20,23,24")
        {
            string url = string.Format("{0}?format=text&COMMAND='{1}'&OBJ_DATA='NO'&EPHEM_TYPE='OBSERVER'&CENTER='{2}'&START_TIME='{3}'&STOP_TIME='{4}'&STEP_SIZE='{5}'&QUANTITIES='{6}'",
                Horizons, Net.UrlEncode(command), Net.UrlEncode(site), Net.UrlEncode(startUtc), Net.UrlEncode(stopUtc), Net.UrlEncode(step), quantities);
            var text = Net.GetString(url, 120000);
            var lines = text.Split('\n');
            var res = new List<string>();
            bool on = false;
            foreach (var l in lines)
            {
                if (l.StartsWith("$$SOE")) { on = true; continue; }
                if (l.StartsWith("$$EOE")) break;
                if (on) res.Add(l.TrimEnd('\r'));
            }
            if (res.Count == 0) res.AddRange(lines.Take(40));
            return res;
        }

        /// <summary>Osculating elements of a small body from the JPL Small-Body Database.</summary>
        public static MinorBody SbdbElements(string designation)
        {
            var d = Json.ParseObject(Net.GetString(Sbdb + "?sstr=" + Net.UrlEncode(designation) + "&phys-par=1&full-prec=1", 60000));
            var orbit = Json.Get(d, "orbit") as Dictionary<string, object>;
            if (orbit == null) return null;
            var els = ((List<object>)orbit["elements"]).Cast<Dictionary<string, object>>().ToDictionary(e => (string)e["name"], e => AstroMath.ParseInvariant(Convert.ToString(e["value"], CultureInfo.InvariantCulture)));
            double epoch = AstroMath.ParseInvariant(Convert.ToString(orbit["epoch"], CultureInfo.InvariantCulture));
            string name = Json.GetString(d, "object.fullname");
            bool comet = Convert.ToString(Json.Get(d, "object.kind")).StartsWith("c");
            var phys = Json.Get(d, "phys_par") as List<object>;
            Func<string, double> pp = key =>
            {
                if (phys == null) return double.NaN;
                foreach (Dictionary<string, object> p in phys) if ((string)p["name"] == key) return AstroMath.ParseInvariant(Convert.ToString(p["value"], CultureInfo.InvariantCulture));
                return double.NaN;
            };
            var el = new OrbitalElements
            {
                Epoch = epoch, Eccentricity = els["e"], PerihelionDistance = els["q"],
                Inclination = els["i"] * AstroMath.Deg2Rad, Node = els["om"] * AstroMath.Deg2Rad, ArgPerihelion = els["w"] * AstroMath.Deg2Rad,
            };
            double tp;
            if (els.TryGetValue("tp", out tp) && !double.IsNaN(tp)) el.PerihelionTime = tp;
            else
            {
                double M = els["ma"] * AstroMath.Deg2Rad, n = els.ContainsKey("n") ? els["n"] * AstroMath.Deg2Rad : el.MeanMotion;
                el.PerihelionTime = epoch - AstroMath.NormalizeRadPi(M) / n;
            }
            var b = new MinorBody { Name = name.Trim(), Designation = designation, IsComet = comet, Elements = el, H = pp("H"), G = double.IsNaN(pp("G")) ? 0.15 : pp("G") };
            if (comet) { b.H = pp("M1"); b.CometK = double.IsNaN(pp("K1")) ? 4 : pp("K1") / 2.5; }
            return b;
        }

        /// <summary>Upcoming close approaches of NEOs to Earth within distance (AU).</summary>
        public static DataTable CloseApproaches(double maxDistAu = 0.05, string dateMin = "now", string dateMax = "+60")
        {
            var d = Json.ParseObject(Net.GetString(Cad + "?dist-max=" + maxDistAu.ToString(CultureInfo.InvariantCulture) + "&date-min=" + dateMin + "&date-max=" + dateMax + "&sort=date", 60000));
            var t = new DataTable();
            foreach (var f in (List<object>)d["fields"]) t.Columns.Add(Convert.ToString(f));
            object data;
            if (d.TryGetValue("data", out data) && data is List<object>)
                foreach (List<object> row in (List<object>)data) t.Rows.Add(row.Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)).ToArray());
            return t;
        }

        /// <summary>AAVSO VSX variable star record.</summary>
        public static Dictionary<string, object> VsxObject(string ident)
        {
            var d = Json.ParseObject(Net.GetString("https://www.aavso.org/vsx/index.php?view=api.object&ident=" + Net.UrlEncode(ident) + "&format=json", 60000));
            return Json.Get(d, "VSXObject") as Dictionary<string, object>;
        }

        /// <summary>AAVSO VSP comparison-star sequence (photometry table) for a star and field of view (arcmin).</summary>
        public static DataTable AavsoComparisonStars(string star, double fovArcmin = 60, double magLimit = 15)
        {
            string url = string.Format(CultureInfo.InvariantCulture, "https://app.aavso.org/vsp/api/chart/?format=json&star={0}&fov={1}&maglimit={2}", Net.UrlEncode(star), fovArcmin, magLimit);
            var d = Json.ParseObject(Net.GetString(url, 60000));
            var t = new DataTable { Columns = { "auid", "ra", "dec", "label", "V", "Verr", "B-V" } };
            var phot = Json.Get(d, "photometry") as List<object>;
            if (phot == null) return t;
            foreach (Dictionary<string, object> p in phot)
            {
                var bands = p["bands"] as List<object>;
                string v = "", ve = "", b = "";
                if (bands != null)
                    foreach (Dictionary<string, object> bd in bands)
                    {
                        if ((string)bd["band"] == "V") { v = Convert.ToString(bd["mag"], CultureInfo.InvariantCulture); ve = Convert.ToString(bd["error"], CultureInfo.InvariantCulture); }
                        if ((string)bd["band"] == "B") b = Convert.ToString(bd["mag"], CultureInfo.InvariantCulture);
                    }
                double bv = AstroMath.ParseInvariant(b) - AstroMath.ParseInvariant(v);
                t.Rows.Add(new[] { Convert.ToString(p["auid"]), Convert.ToString(p["ra"]), Convert.ToString(p["dec"]), Convert.ToString(p["label"]), v, ve, double.IsNaN(bv) ? "" : bv.ToString("0.000", CultureInfo.InvariantCulture) });
            }
            return t;
        }

        /// <summary>CelesTrak GP elements in TLE format for a group (visual, stations, starlink, gps-ops, ...).</summary>
        public static string CelestrakTle(string group = "visual") => Net.GetString("https://celestrak.org/NORAD/elements/gp.php?GROUP=" + Net.UrlEncode(group) + "&FORMAT=tle", 60000);
    }
}
