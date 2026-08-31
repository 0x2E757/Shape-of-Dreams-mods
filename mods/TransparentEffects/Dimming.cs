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
            DewResources.ClearAllVariants(repairReferences: true);
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

            // Named before anything is touched, so that Repair can find this copy again once its
            // materials have been destroyed. The game marks its own copies the same way and for
            // the same reason.
            prefab.name += Marker;

            // Standard GetComponentsInChildren rather than the game's pooled GetComponents-
            // InChildrenNonAlloc: this runs once per prefab per session, so the allocation is
            // measured in dozens for a whole run, and a pool handle that has to be returned is a
            // lifetime to get wrong for nothing.
            var created = new List<Material>();

            foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(true))
            {
                if (invisible)
                {
                    renderer.enabled = false;
                    continue;
                }

                var materials = renderer.sharedMaterials;
                for (int i = 0; i < materials.Length; i++)
                {
                    if (materials[i] == null) continue;

                    // A copy, because sharedMaterials on a prefab is the project's asset and
                    // writing to it would dim the effect for everyone, in every variant, until the
                    // game was restarted.
                    materials[i] = UnityEngine.Object.Instantiate(materials[i]);
                    created.Add(materials[i]);
                    Fade(materials[i], alpha);
                }
                renderer.sharedMaterials = materials;
            }

            foreach (var light in prefab.GetComponentsInChildren<Light>(true))
            {
                light.intensity *= alpha;
                if (invisible) light.range = 0f;
            }

            if (invisible)
            {
                foreach (var point in prefab.GetComponentsInChildren<FxPointLight>(true))
                {
                    point.intensityMultiplier = 0f;
                    point.rangeMultiplier = 0f;
                }
            }

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
        private static void Fade(Material material, float alpha)
        {
            Scale(material, "_Cutoff", alpha);

            // An opaque surface has no alpha to lower, so its brightness is lowered instead.
            if (material.HasProperty("_Surface") && Mathf.Abs(material.GetFloat("_Surface")) < 0.1f)
            {
                ScaleColorRgb(material, "_EmissionColor", alpha);
                return;
            }

            if (Scale(material, "_Alpha", alpha)) return;
            if (Scale(material, "Vector1_2C5A3101", alpha)) return;
            if (Scale(material, "Vector1_ba2f839299ad461eb6b76fbb90d387aa", alpha)) return;
            if (Scale(material, "_Opacity", alpha)) return;
            if (ScaleColorAlpha(material, "_BaseColor", alpha)) return;
            if (ScaleColorAlpha(material, "_Color", alpha)) return;
            if (Scale(material, "_FinalOpacityPower", alpha)) return;
            if (Scale(material, "_ColorFactor", alpha)) return;

            Scale(material, "_Multiplier", alpha);
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
