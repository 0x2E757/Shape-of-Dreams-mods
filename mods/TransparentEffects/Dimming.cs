using System;
using System.Collections.Generic;
using UnityEngine;

namespace TransparentEffects
{
    // Two resource variants of the game's own kind, and the work one of them does to a prefab.
    //
    // **A variant, not a per-instance tint.** DewResources keeps, for each asset and each set of
    // variant ids, one processed copy of the prefab; every effect spawned with that set is
    // instantiated from the copy. So the alpha is paid for once per prefab per session rather than
    // once per cast, and nothing walks a live effect's renderers while it is playing.
    // GetNextVariantId and RegisterVariantProcessor are public and static, and the game registers
    // its own three the same way.
    //
    // The price is that the number is baked in: a variant built at 0.5 stays at 0.5 until the
    // cache is cleared. Rebuild() below is what the settings screen calls.
    //
    // What Apply does to a prefab is modelled on the game's own TonedDownProcessor, and
    // deliberately so - that method is the evidence of which shader properties on which materials
    // actually carry opacity in this game, and it is a longer list than it looks. Two things it
    // does are left out: it also thins particle emission rates and it also disables renderers at
    // its lowest step. The first is not opacity and this mod does not claim it; the second is here
    // but only at zero, where there is nothing left to draw.
    internal static class Dimming
    {
        // Registered ids, or 0 for "not registered". The game's own ids come from the same
        // counter, so these are simply the next two after whatever the game and any earlier mod
        // took.
        public static int Mine { get; private set; }
        public static int Others { get; private set; }

        public static bool Ready => Mine > 0 && Others > 0;

        // Written into the name of every copy this mod dims, so that the copies can be found again
        // after their materials have been destroyed. See Repair below for why that is necessary.
        private const string Marker = "(TransparentEffects)";

        private static readonly Action Repairer = Repair;

        // Below this the multiplier is not worth a variant: the effect would be indistinguishable
        // and the cache would carry a second copy of every prefab for nothing.
        public const float NoChange = 0.999f;

        // And below this there is nothing to see, so the renderers come off rather than being
        // asked to draw at zero.
        private const float Invisible = 0.001f;

        public static void Register()
        {
            if (Ready) return;
#if DEBUG
            Exponents.Reload();
#endif

            Mine = DewResources.GetNextVariantId();
            DewResources.RegisterVariantProcessor(Mine, o => Apply(o, MineAlpha(), respectAuthoredFloor: false));

            Others = DewResources.GetNextVariantId();
            DewResources.RegisterVariantProcessor(Others, o => Apply(o, OthersAlpha(), respectAuthoredFloor: true));

            if (DewResources.onVariantsCleared == null) DewResources.onVariantsCleared = new SafeAction();
            DewResources.onVariantsCleared.Add(Repairer);
        }

        public static void Unregister()
        {
            if (!Ready) return;

            // The cached copies go before the processors that made them, because clearing a
            // variant runs the cleanup each processor returned - the one that destroys the
            // material instances it created.
            Rebuild();

            DewResources.onVariantsCleared?.Remove(Repairer);
            DewResources.UnregisterVariantProcessor(Mine);
            DewResources.UnregisterVariantProcessor(Others);
            Mine = 0;
            Others = 0;
        }

        // Runs after every variant clear, on effects that are on screen right now.
        //
        // The clear destroys the material copies a processor made, and anything spawned from that
        // variant is still holding them - so a fireball mid-flight when Apply is pressed is left
        // with null materials, which draws as the shader-missing magenta.
        //
        // The game has exactly this problem and exactly this answer: OnInit_vTonedDown subscribes
        // to onVariantsCleared, finds actors whose name carries its own marker, and puts
        // DewResources.transparentMat in place of every null. That handler matches on the string
        // "(Other Players Toned Down)" and so would never find this mod's copies, hence a second
        // subscriber matching this mod's own marker.
        //
        // **ClearVariantsOfAsset's repairReferences flag is not the answer**, in case it looks like
        // it should be: RepairMissingReferences_Prepare and RepairMissingReferences_Repair are both
        // empty method bodies in the shipped assembly. The parameter is threaded through several
        // call sites and does nothing at all.
        private static void Repair()
        {
            // FindObjectsByType rather than the FindObjectsOfType the game's own handler uses:
            // same set of active objects, but unsorted, and the sort is the expensive half.
            foreach (var actor in UnityEngine.Object.FindObjectsByType<Actor>(FindObjectsSortMode.None))
            {
                if (actor == null || !actor.gameObject.name.Contains(Marker)) continue;

                foreach (var renderer in actor.GetComponentsInChildren<Renderer>(true))
                {
                    var materials = renderer.sharedMaterials;
                    bool changed = false;

                    for (int i = 0; i < materials.Length; i++)
                    {
                        if (materials[i] != null) continue;
                        materials[i] = DewResources.transparentMat;
                        changed = true;
                    }

                    if (changed) renderer.sharedMaterials = materials;
                }
            }

            PurgePooledFx(all: false);
        }

        // DewEffect's pool root, private and created on first use. Read rather than found by name,
        // since GameObject.Find would also match anything else that happened to be called that.
        private static readonly System.Reflection.FieldInfo PoolRoot =
            HarmonyLib.AccessTools.Field(typeof(DewEffect), "_poolRoot");

        // The other half of a clear, and the half the game has no answer for either.
        //
        // Effects played with FxPlayNew - hits, muzzle flashes, most of what a projectile leaves
        // behind - are clones kept in DewEffect's pools and handed out again, and a clone shares
        // its template's materials: for a dimmed effect, this mod's. The pool is keyed by the
        // parent's asset and the child's path, not by variant, and FlushInactivePools only
        // throws away clones that are inactive at the moment of the clear. One still playing -
        // and with the settings screen open the game is paused, so the last cast's are - keeps
        // its now-destroyed materials, finishes, goes back into the pool, and is handed out on
        // the cast after next with nothing to draw. (It was suspected for parts of Scattershot
        // going missing on later shots and ruled out - nothing was purged - but the hole is real.)
        //
        // They are not Actors, so Repair's walk above never sees them, and there is no right
        // material to put back: the original is gone. So they are destroyed. PlayIntoPool skips a
        // destroyed entry and makes a fresh clone from the live template, and EffectAutoDestroy
        // skips a destroyed entry too.
        //
        // That is the whole job after a clear somebody else started. After this mod's own Apply
        // it is only half: PlayIntoPool hands out any idle clone under a key without asking which
        // template made it, so a clone that was still playing when the setting went from 1.0 to
        // 0.5 keeps the undimmed asset materials - nothing destroyed, nothing to detect - and
        // goes on being handed out at 0.5. So Rebuild takes the lot (all: true); the pool refills
        // from the new templates as effects play.
        private static void PurgePooledFx(bool all)
        {
            var root = PoolRoot?.GetValue(null) as Transform;
            if (root == null) return;

            int purged = 0;
            for (int i = root.childCount - 1; i >= 0; i--)
            {
                var clone = root.GetChild(i).gameObject;
                if (!all && !HasDestroyedMaterial(clone)) continue;

                UnityEngine.Object.Destroy(clone);
                purged++;
            }
#if DEBUG
            if (purged > 0) Debug.Log("[TransparentEffects] purged " + purged + " pooled effect clones" + (all ? "" : " left holding destroyed materials"));
#endif
        }

        private static bool HasDestroyedMaterial(GameObject clone)
        {
            foreach (var renderer in clone.GetComponentsInChildren<Renderer>(true))
            {
                foreach (var material in renderer.sharedMaterials)
                {
                    if (material == null) return true;
                }
            }
            return false;
        }

        // Every cached variant in the game, not only this mod's.
        //
        // ClearVariantsOfVarDef would be the narrow instrument and it is the wrong one: it matches
        // a VariantDef whole rather than by the ids it contains, so clearing "just ours" would
        // only find effects whose entire definition was this mod's single id - which is never the
        // case, since an AbilityInstance always carries the game's vQualityAdjusted as well. So
        // the whole cache goes and rebuilds itself lazily, which is what the game does for a
        // graphics setting.
        public static void Rebuild()
        {
#if DEBUG
            Exponents.Reload();
#endif
            DewResources.ClearAllVariants(repairReferences: true);
            PurgePooledFx(all: true);
        }

        private static float MineAlpha()
        {
            var config = TransparentEffectsMod.Live;
            return config == null ? 1f : Mathf.Clamp01(config.myOwnEffects);
        }

        private static float OthersAlpha()
        {
            var config = TransparentEffectsMod.Live;
            return config == null ? 1f : Mathf.Clamp01(config.otherPlayersEffects);
        }

        // The alpha the game's own five-step setting would use, so that an authored cap expressed
        // in those steps can be honoured in this mod's units.
        private static float AuthoredFloor(ReduceOtherPlayerEffectsStrength step)
        {
            switch (step)
            {
                case ReduceOtherPlayerEffectsStrength.Low: return 1f;
                case ReduceOtherPlayerEffectsStrength.Medium: return 0.7f;
                case ReduceOtherPlayerEffectsStrength.High: return 0.45f;
                case ReduceOtherPlayerEffectsStrength.VeryHigh: return 0.25f;
                default: return 0f;
            }
        }

        // Returns the cleanup the variant cache will run when this copy is thrown away, or null if
        // there is nothing to clean up. Materials are instantiated per copy, so they are this
        // mod's to destroy.
        private static Action Apply(UnityEngine.Object asset, float alpha, bool respectAuthoredFloor)
        {
            if (!(asset is GameObject prefab)) return null;

            // The game's own hard opt-out, and it is honoured for both rows. It marks the effects
            // that have to stay readable whoever is looking - a knocked-out hero's explosion wears
            // it - and a player dimming their own screen did not mean to lose those either.
            if (prefab.GetComponent<IOtherPlayersTonedDownDisable>() != null) return null;

            // The softer one, a cap rather than a veto, and it means exactly "do not fade this
            // below X for other players". That is a sentence about other players, so it is applied
            // to that row and not to your own.
            if (respectAuthoredFloor)
            {
                var limit = prefab.GetComponent<IOtherPlayersTonedDownLimit>();
                if (limit != null) alpha = Mathf.Max(alpha, AuthoredFloor(limit.maxReduction));
            }

            if (alpha >= NoChange) return null;
            bool invisible = alpha <= Invisible;

            // Already one of ours: fading it again would square the setting. GetVariant always
            // copies the untouched asset, so this should never be true - it is here because a
            // second cast looking dimmed twice over was reported, and if the cause is a copy
            // coming back through here, this both stops it and says so.
            if (prefab.name.Contains(Marker))
            {
                Debug.LogWarning("[TransparentEffects] asked to dim an already dimmed copy, skipped: " + prefab.name);
                return null;
            }

            // Named before anything is touched, so that Repair can find this copy again once its
            // materials have been destroyed. The game marks its own copies the same way and for
            // the same reason.
            prefab.name += Marker;

            // Standard GetComponentsInChildren rather than the game's pooled GetComponents-
            // InChildrenNonAlloc: this runs once per prefab per session, so the allocation is
            // measured in dozens for a whole run, and a pool handle that has to be returned is a
            // lifetime to get wrong for nothing.
            var created = new List<Material>();
#if DEBUG
            var report = new System.Text.StringBuilder();
            report.Append("[TransparentEffects] dimmed ").Append(prefab.name).Append(" at ").Append(alpha.ToString("0.###"));
#endif

            foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(true))
            {
                if (invisible)
                {
                    renderer.enabled = false;
                    continue;
                }

                var materials = renderer.sharedMaterials;
                bool anyMaterial = false;
                bool anyFaded = false;
                for (int i = 0; i < materials.Length; i++)
                {
                    if (materials[i] == null) continue;
                    anyMaterial = true;

                    // A copy, because sharedMaterials on a prefab is the project's asset and
                    // writing to it would dim the effect for everyone, in every variant, until the
                    // game was restarted.
                    materials[i] = UnityEngine.Object.Instantiate(materials[i]);
                    created.Add(materials[i]);
                    float k = Exponents.For(materials[i]);
                    string used = Fade(materials[i], Mathf.Pow(alpha, k));
                    anyFaded |= used != null;
#if DEBUG
                    Describe(report, prefab.transform, renderer, materials[i], used, k);
#endif
                }
                renderer.sharedMaterials = materials;

                // A particle material with nothing in the cascade still has the particles' own
                // vertex colour to go through - Mobile/Particles/Alpha Blended has no colour
                // property at all and takes everything from there. Only when none of the
                // renderer's materials took the multiplier, or the ones that did would be dimmed
                // twice; and only when it has a material at all, since a renderer with none draws
                // nothing - it is usually a root whose start colour sub-emitters inherit, and
                // fading it would dim those a second time.
                if (anyMaterial && !anyFaded && renderer is ParticleSystemRenderer
                    && renderer.TryGetComponent<ParticleSystem>(out var particles))
                {
                    FadeStartColor(particles, alpha);
#if DEBUG
                    report.Append("\n    -> startColor alpha x").Append(alpha.ToString("0.###"));
#endif
                }
            }

            // A light driven by FxPointLight has its intensity rewritten every frame from
            // _originalIntensity * intensityMultiplier, the first of them serialized on the
            // prefab - so scaling Light.intensity is overwritten on the first frame and the glow
            // on the ground stays at full strength. The multiplier is the lever for those. The
            // game only ever touches it at Hide, which is why its own steps have the same hole.
            foreach (var light in prefab.GetComponentsInChildren<Light>(true))
            {
                if (light.TryGetComponent<FxPointLight>(out var point) && point.animateIntensity)
                    point.intensityMultiplier *= alpha;
                else
                    light.intensity *= alpha;

                if (invisible)
                {
                    light.range = 0f;
                    if (point != null) point.rangeMultiplier = 0f;
                }
#if DEBUG
                report.Append("\n  ").Append(PathFrom(prefab.transform, light.transform))
                      .Append(" | Light ").Append(light.type)
                      .Append(" intensity=").Append(light.intensity.ToString("0.###"))
                      .Append(point != null ? " | FxPointLight x" + point.intensityMultiplier.ToString("0.###")
                                              + (point.animateIntensity ? "" : " (not animating intensity)")
                                            : "");
#endif
            }

            if (invisible)
            {
                foreach (var point in prefab.GetComponentsInChildren<FxPointLight>(true))
                {
                    point.intensityMultiplier = 0f;
                    point.rangeMultiplier = 0f;
                }
            }

#if DEBUG
            Debug.Log(report.ToString());
#endif

            foreach (var color in prefab.GetComponentsInChildren<FxEntityColor>(true))
            {
                color.emission *= alpha;
            }

            if (created.Count == 0) return null;

            return () =>
            {
                foreach (var material in created)
                {
                    if (material != null) UnityEngine.Object.DestroyImmediate(material);
                }
            };
        }

        // Which property on a material carries its opacity, asked in the game's own order.
        //
        // There is no one answer - the effects are built on a spread of shader graphs, and the
        // names below are what those graphs happened to call the same idea, generated ones
        // included. The order is a cascade: the first property that exists is the one that means
        // opacity for this material, and the rest are not tried.
        //
        // **Where this parts from the game** is opaque surfaces, and it parts from it because the
        // game's version draws a visible artifact. TonedDownProcessor multiplies _Cutoff down on
        // every material and darkens _EmissionColor on every opaque one. On an alpha-clipped
        // opaque particle - Scattershot's muzzle dust is Dew/Dew Particles Unlit at _Surface 0,
        // queue 2450 - a lower cutoff lets the soft margin of the texture through as solid, and
        // the darkening turns that into a dark square behind the flash. So:
        //
        //   - an alpha-clipped opaque surface has its cutoff *raised* toward 1, which erodes the
        //     shape instead, and is not darkened;
        //   - an opaque surface with no clipping is darkened as before, having no other lever;
        //   - a transparent surface is the game's: cutoff and alpha both multiplied.
        //
        // **The transparent cutoff has to go down with the alpha**, which looks backwards and is
        // not. The Dew particle shaders clip in transparent mode too - Lacerta's bullet is
        // _Surface 1 with _Cutoff 0.5 - so an alpha of 0.18 against an untouched 0.5 discards
        // every pixel and the attack vanishes outright. Scaling both keeps the clipped shape
        // where it was and lets the alpha do the fading.
        //
        // Returns the property that took the multiplier, or null if none did.
        private static string Fade(Material material, float alpha)
        {
            // Dew/Dew Entity - the hero and monster shader, which a few effects use for solid
            // props such as Teal Blade's sword - has its own opacity, the one EntityVisual drives
            // when a hero fades. It is opaque by _Surface, so without this it would only be
            // darkened, and a dark sword is still a solid sword.
            //
            // In both of the paths that thin a solid surface - this one and the raised cutoff
            // below - what is left is still drawn at full strength, and if it glows the glow is
            // HDR: Teal Blade's sword at (1.66, 3.0, 1.82), Pew's and Scattershot's InnerGlow at
            // 2.8, a hit Glow at 4.9. So the emission comes down with it, or the thinned surface
            // shines through as if nothing had happened - but by Exponents.ThinnedEmission, a
            // share of the fade rather than all of it, since the area is already going at a.
            if (Scale(material, "_CMOpacity", alpha))
            {
                FadeEmission(material, Mathf.Pow(alpha, Exponents.ThinnedEmission));
                return "_CMOpacity";
            }

            if (material.HasProperty("_Surface") && Mathf.Abs(material.GetFloat("_Surface")) < 0.1f)
            {
                if (IsAlphaClipped(material) && material.HasProperty("_Cutoff"))
                {
                    float cutoff = material.GetFloat("_Cutoff");
                    material.SetFloat("_Cutoff", cutoff + (1f - cutoff) * (1f - Mathf.Pow(alpha, Exponents.ThinnedCutoff)));
                    FadeEmission(material, Mathf.Pow(alpha, Exponents.ThinnedEmission));
                    return "_Cutoff";
                }

                return ScaleColorRgb(material, "_EmissionColor", alpha) ? "_EmissionColor" : null;
            }

            Scale(material, "_Cutoff", alpha);

            // Emission is added to the colour before blending, and on the URP-style particle
            // shaders it is HDR: Lacerta's bullet glows at (1.4, 1.07, 0) on top of its base
            // colour. Blended at an alpha of 0.25 that is still brighter than the ground behind
            // it, and after tonemapping and bloom it reads as barely dimmed at all. Taking the
            // emission down with the alpha is what makes a lower number look like one.
            FadeEmission(material, alpha);

            if (Scale(material, "_Alpha", alpha)) return "_Alpha";
            if (Scale(material, "Vector1_2C5A3101", alpha)) return "Vector1_2C5A3101";
            if (Scale(material, "Vector1_ba2f839299ad461eb6b76fbb90d387aa", alpha)) return "Vector1_ba2f839299ad461eb6b76fbb90d387aa";
            if (Scale(material, "_Opacity", alpha)) return "_Opacity";
            if (ScaleColorAlpha(material, "_BaseColor", alpha)) return "_BaseColor";
            if (ScaleColorAlpha(material, "_Color", alpha)) return "_Color";
            if (Scale(material, "_FinalOpacityPower", alpha)) return "_FinalOpacityPower";
            if (Scale(material, "_ColorFactor", alpha)) return "_ColorFactor";
            if (Scale(material, "_Multiplier", alpha)) return "_Multiplier";

            return null;
        }

#if DEBUG
        // One line per material: where it sits in the prefab, what draws it, which shader, which
        // property the cascade settled on, and the blend state that decides what that property
        // does to the picture. The blend floats are the URP/shader-graph names; a shader without
        // them prints nothing for them.
        private static void Describe(System.Text.StringBuilder report, Transform root, Renderer renderer,
                                     Material material, string used, float k)
        {
            report.Append("\n  ").Append(PathFrom(root, renderer.transform))
                  .Append(" | ").Append(renderer.GetType().Name)
                  .Append(" | ").Append(material.shader != null ? material.shader.name : "<no shader>")
                  .Append(Exponents.IsListed(material) ? " (k=" + k.ToString("0.##") + ")" : "")
                  .Append(" | ").Append(used ?? "NOTHING SCALED");

            if (used != null)
            {
                var shader = material.shader;
                int index = shader.FindPropertyIndex(used);
                bool isColor = index >= 0 && shader.GetPropertyType(index) == UnityEngine.Rendering.ShaderPropertyType.Color;
                report.Append(" = ").Append(isColor ? material.GetColor(used).ToString() : material.GetFloat(used).ToString("0.###"));
            }

            if (material.IsKeywordEnabled("_EMISSION") && used != "_EmissionColor" && material.HasProperty("_EmissionColor"))
                report.Append(" | emission=").Append(material.GetColor("_EmissionColor"));
            if (material.HasProperty("_Cutoff")) report.Append(" | _Cutoff=").Append(material.GetFloat("_Cutoff").ToString("0.###"));
            foreach (var blend in new[] { "_Surface", "_Blend", "_SrcBlend", "_DstBlend", "_ZWrite" })
            {
                if (material.HasProperty(blend)) report.Append(" | ").Append(blend).Append('=').Append(material.GetFloat(blend));
            }
            report.Append(" | queue=").Append(material.renderQueue);

            DumpShaderOnce(material);
        }

        // Every property a shader declares, with this material's value, and the keywords it has
        // on - once per shader for the session. The cascade only knows names; this is what shows
        // whether a shader has some other property that decides the blended alpha, which is what
        // a material that dims on paper and not on screen comes down to.
        private static readonly HashSet<string> DumpedShaders = new HashSet<string>();

        private static void DumpShaderOnce(Material material)
        {
            var shader = material.shader;
            if (shader == null || !DumpedShaders.Add(shader.name)) return;

            var dump = new System.Text.StringBuilder();
            dump.Append("[TransparentEffects] shader ").Append(shader.name).Append(" (values from ").Append(material.name).Append(')');

            for (int i = 0; i < shader.GetPropertyCount(); i++)
            {
                string name = shader.GetPropertyName(i);
                var type = shader.GetPropertyType(i);
                dump.Append("\n  ").Append(name).Append(' ').Append(type).Append(" = ");

                switch (type)
                {
                    case UnityEngine.Rendering.ShaderPropertyType.Color:
                    case UnityEngine.Rendering.ShaderPropertyType.Vector:
                        dump.Append(material.GetVector(name));
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Float:
                    case UnityEngine.Rendering.ShaderPropertyType.Range:
                        dump.Append(material.GetFloat(name).ToString("0.###"));
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Texture:
                        var texture = material.GetTexture(name);
                        dump.Append(texture != null ? texture.name : "none");
                        break;
                    default:
                        dump.Append('?');
                        break;
                }
            }

            dump.Append("\n  keywords: ").Append(string.Join(" ", material.shaderKeywords));
            Debug.Log(dump.ToString());
        }

        private static string PathFrom(Transform root, Transform node)
        {
            string path = node.name;
            while (node != root && node.parent != null)
            {
                node = node.parent;
                path = node.name + "/" + path;
            }
            return path;
        }
#endif

        // Only where emission is switched on: a material can carry an _EmissionColor it never
        // reads, and the debug report would then claim a change that draws nothing.
        private static void FadeEmission(Material material, float alpha)
        {
            if (material.IsKeywordEnabled("_EMISSION")) ScaleColorRgb(material, "_EmissionColor", alpha);
        }

        // URP's own toggle, its keyword, or failing both the render queue: the Dew particle
        // shaders sit at 2450 without saying anything else, and 2450 up to GeometryLast (2500)
        // is Unity's AlphaTest band.
        private static bool IsAlphaClipped(Material material)
        {
            if (material.HasProperty("_AlphaClip") && material.GetFloat("_AlphaClip") > 0.5f) return true;
            if (material.IsKeywordEnabled("_ALPHATEST_ON")) return true;

            int queue = material.renderQueue;
            return queue >= (int)UnityEngine.Rendering.RenderQueue.AlphaTest
                && queue < (int)UnityEngine.Rendering.RenderQueue.GeometryLast;
        }

        // The particle system on the variant copy, not the asset, so there is nothing to undo:
        // the copy is thrown away with its variant. Colour over lifetime multiplies the start
        // colour, so the start colour is the one place to change.
        private static void FadeStartColor(ParticleSystem particles, float alpha)
        {
            var main = particles.main;
            var color = main.startColor;

            switch (color.mode)
            {
                case ParticleSystemGradientMode.Color:
                    color.color = WithAlpha(color.color, alpha);
                    break;
                case ParticleSystemGradientMode.TwoColors:
                    color.colorMin = WithAlpha(color.colorMin, alpha);
                    color.colorMax = WithAlpha(color.colorMax, alpha);
                    break;
                case ParticleSystemGradientMode.Gradient:
                case ParticleSystemGradientMode.RandomColor:
                    color.gradient = WithAlpha(color.gradient, alpha);
                    break;
                case ParticleSystemGradientMode.TwoGradients:
                    color.gradientMin = WithAlpha(color.gradientMin, alpha);
                    color.gradientMax = WithAlpha(color.gradientMax, alpha);
                    break;
            }

            main.startColor = color;
        }

        private static Color WithAlpha(Color color, float alpha)
        {
            color.a *= alpha;
            return color;
        }

        private static Gradient WithAlpha(Gradient gradient, float alpha)
        {
            if (gradient == null) return null;

            var keys = gradient.alphaKeys;
            for (int i = 0; i < keys.Length; i++) keys[i].alpha *= alpha;

            var faded = new Gradient { mode = gradient.mode };
            faded.SetKeys(gradient.colorKeys, keys);
            return faded;
        }

        private static bool Scale(Material material, string property, float alpha)
        {
            if (!material.HasProperty(property)) return false;
            material.SetFloat(property, material.GetFloat(property) * alpha);
            return true;
        }

        // Brightness down, alpha left alone: this is for the emission of a surface that is not
        // transparent in the first place.
        private static bool ScaleColorRgb(Material material, string property, float alpha)
        {
            if (!material.HasProperty(property)) return false;

            var color = material.GetColor(property);
            float keep = color.a;
            color *= alpha;
            color.a = keep;
            material.SetColor(property, color);
            return true;
        }

        private static bool ScaleColorAlpha(Material material, string property, float alpha)
        {
            if (!material.HasProperty(property)) return false;

            var color = material.GetColor(property);
            color.a *= alpha;
            material.SetColor(property, color);
            return true;
        }
    }
}
