using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace CrossMC.HowToFish
{
    /// <summary>
    /// Persists the fixed world mapping once it has been established.
    ///
    /// <p>The mapping is <b>world configuration</b>, not per-session state: it must never be
    /// re-derived from a Minecraft save/spawn position. In the dev-only <c>transform.autoAnchor</c>
    /// mode the origin is computed once from an aligned player pair and then saved here, so a host
    /// restart (or a new Minecraft world) reuses it instead of re-anchoring.</p>
    ///
    /// <p>Formal mode (<c>transform.autoAnchor=false</c>) ignores this file and uses the explicit
    /// <c>transform.origin*</c> values.</p>
    /// </summary>
    public static class MappingStore
    {
        /// <summary>The file used for the persisted mapping (user-level, shared across installs).</summary>
        public static string FilePath()
        {
            string baseDir = Environment.GetEnvironmentVariable("LOCALAPPDATA");

            if (!string.IsNullOrEmpty(baseDir))
            {
                return Path.Combine(baseDir, "CrossMC", "howtofish.anchor");
            }

            return Path.Combine(Path.GetDirectoryName(typeof(MappingStore).Assembly.Location), "howtofish.anchor");
        }

        public static bool TryLoad(out float originX, out float originY, out float originZ, out float scale, out bool flipX)
        {
            originX = 0f;
            originY = 0f;
            originZ = 0f;
            scale = 1f;
            flipX = true;

            try
            {
                string file = FilePath();

                if (!File.Exists(file))
                {
                    return false;
                }

                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (string line in File.ReadAllLines(file))
                {
                    string t = line.Trim();

                    if (t.Length == 0 || t.StartsWith("#") || t.StartsWith(";"))
                    {
                        continue;
                    }

                    int eq = t.IndexOf('=');

                    if (eq <= 0)
                    {
                        continue;
                    }

                    values[t.Substring(0, eq).Trim()] = t.Substring(eq + 1).Trim();
                }

                if (!values.ContainsKey("originX"))
                {
                    return false;
                }

                originX = Parse(values, "originX", 0f);
                originY = Parse(values, "originY", 0f);
                originZ = Parse(values, "originZ", 0f);
                scale = Parse(values, "scale", 1f);
                flipX = !values.TryGetValue("flipX", out string f)
                        || f.Equals("true", StringComparison.OrdinalIgnoreCase) || f == "1";
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static void Save(float originX, float originY, float originZ, float scale, bool flipX)
        {
            try
            {
                string file = FilePath();
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                string text =
                        "# CrossMC fixed world mapping, established once by the host adapter.\n"
                        + "# This is WORLD CONFIGURATION, not per-session state. It is reused on restart\n"
                        + "# and is never re-derived from a Minecraft save/spawn position.\n"
                        + "# Delete this file to re-establish the mapping on the next connect.\n"
                        + "originX=" + F(originX) + "\n"
                        + "originY=" + F(originY) + "\n"
                        + "originZ=" + F(originZ) + "\n"
                        + "scale=" + F(scale) + "\n"
                        + "flipX=" + (flipX ? "true" : "false") + "\n";
                File.WriteAllText(file, text);
            }
            catch (Exception)
            {
                // Best-effort; the mapping still lives in memory for this session.
            }
        }

        private static float Parse(Dictionary<string, string> values, string key, float fallback)
        {
            return values.TryGetValue(key, out string s)
                    && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)
                    ? f : fallback;
        }

        private static string F(float value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }
    }
}
