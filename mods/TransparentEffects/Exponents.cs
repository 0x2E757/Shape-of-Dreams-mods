using System.Collections.Generic;
using UnityEngine;

namespace TransparentEffects
{
    // How hard each shader family has to be pushed for a setting to look like itself.
    //
    // A material is faded by a^k rather than a, with k looked up by shader name. The reason is
    // perceptual and it was measured, not reasoned: at the same setting Scattershot - URP
    // Particles Unlit, a dissolve graph taking _Alpha - looked about as dimmed as the number said,
    // while Pew and Teal Blade, whose main parts are the additive "_add" shader graphs, did not
    // look half gone until somewhere near 0.2 or 0.3. Additive HDR glow under tonemapping and
    // bloom does not lose brightness in proportion to its multiplier, so those shaders get a
    // steeper curve. Anything not listed is 1, the plain multiplier.
    //
    // Per shader rather than per effect: the list stays short, a new effect on a known shader is
    // calibrated already, and the debug report names any shader it has not seen.
    internal static class Exponents
    {
        private static readonly Dictionary<string, float> Shipped = new Dictionary<string, float>
        {
            ["Shader Graphs/Fx_Shoot&Hit_Particle_add"] = 1.6f,
            ["Shader Graphs/Fx_Hit&Slash_add_SG"] = 1.6f,
            ["Dew/Dew Entity"] = 1.6f,
            [ThinnedEmissionKey] = 0f,
            [ThinnedCutoffKey] = 0.6f,
        };

        // Not a shader: the exponent the emission of a *thinned* surface fades by - one whose
        // cutoff was raised or whose _CMOpacity was lowered. Those already lose area at a, and
        // taking the glow down by a as well made them fade as a^2: Scattershot's muzzle dust went
        // from looking about right to visibly ahead of everything else. 0.5 and 0.25 still left it
        // ahead, and the log showed why: the glow was barely moving by then, and the erosion was
        // doing the cutting. So 0 - the glow is left alone - and the erosion is eased instead.
        private const string ThinnedEmissionKey = "@thinned-emission";

        // The exponent on the thinning itself: a raised cutoff goes to c + (1 - c)(1 - a^k).
        // At 1, Scattershot's muzzle dust (authored cutoff 0.39) went to 0.69 at a setting of
        // 0.5, and a soft dust texture is mostly low alpha, so far more than half of it was gone.
        // 0.6 takes it to 0.60 at 0.5 and 0.77 at 0.2.
        private const string ThinnedCutoffKey = "@thinned-cutoff";

        public static float ThinnedEmission => _table.TryGetValue(ThinnedEmissionKey, out float k) ? k : 0f;

        public static float ThinnedCutoff => _table.TryGetValue(ThinnedCutoffKey, out float k) ? k : 0.6f;

        private static Dictionary<string, float> _table = new Dictionary<string, float>(Shipped);

        public static float For(Material material)
        {
            var shader = material.shader;
            if (shader == null) return 1f;
            return _table.TryGetValue(shader.name, out float k) ? k : 1f;
        }

        public static bool IsListed(Material material)
        {
            return material.shader != null && _table.ContainsKey(material.shader.name);
        }

#if DEBUG
        // For calibrating without a rebuild. Debug builds read
        //
        //     <persistentDataPath>/QuickSave/Mods/<modId>/exponents.txt
        //
        // on load and on every Apply, one "shader name = k" per line, # for comments. Its entries
        // are laid over the shipped ones; a missing file leaves the shipped table alone. Release
        // builds never look: the numbers that come out of calibrating belong in Shipped above.
        public static string OverridePath;

        public static void Reload()
        {
            _table = new Dictionary<string, float>(Shipped);
            if (string.IsNullOrEmpty(OverridePath) || !System.IO.File.Exists(OverridePath)) return;

            foreach (var raw in System.IO.File.ReadAllLines(OverridePath))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;

                int eq = line.LastIndexOf('=');
                if (eq <= 0) continue;

                string name = line.Substring(0, eq).Trim();
                if (float.TryParse(line.Substring(eq + 1).Trim(), System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out float k) && k > 0f)
                {
                    _table[name] = k;
                }
            }

            var summary = new System.Text.StringBuilder("[TransparentEffects] exponents from ").Append(OverridePath);
            foreach (var pair in _table) summary.Append("\n  ").Append(pair.Key).Append(" = ").Append(pair.Value);
            Debug.Log(summary.ToString());
        }
#endif
    }
}
