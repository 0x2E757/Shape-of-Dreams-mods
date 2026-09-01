using System.Collections.Generic;
using System.Globalization;
using TMPro;
using UnityEngine;

namespace AreMyGemsCompatible
{
    // The same mark again, this time inline in the tooltip text.
    //
    // TextMeshPro draws a sprite inside a string through a <sprite> tag, and a tag can only reach a
    // sprite that is in a TMP_SpriteAsset. The mark is a loose Texture2D, so an asset has to be
    // built for it at runtime.
    //
    // **The tag has to be by name, not by index, and the asset has to be reached as a fallback.**
    // The game's own descriptions are full of <sprite=1> and <sprite=5> - the ability-power and
    // level-scaling icons - and those are indices into whichever asset the text is using. Assigning
    // an asset of ours to TMP_Text.spriteAsset would repoint every one of them at our single
    // sprite, turning every damage number in the tooltip into an exclamation mark. So nothing is
    // assigned: our asset is added to the *existing* asset's fallbackSpriteAssets, and referenced
    // by name. TMP_Text resolves <sprite name="..."> through
    // TMP_SpriteAsset.SearchForSpriteByHashCode with includeFallbacks true, so the name finds us
    // while every index still finds what it always did.
    internal static class TooltipSprite
    {
        // Prefixed and deliberately unlovely: it shares a namespace with every sprite name the
        // game and any other mod use.
        private const string SpriteName = "amgc_mark";

        // The tag, dressed by BadgeAppearance's two tooltip numbers.
        //
        // The sprite tag itself carries no size or offset - TMP gives it tint, index and name and
        // nothing else - so the dressing is done by wrapping it. <size=N%> works because a sprite
        // is scaled from m_currentFontSize, which is exactly what that tag writes; <voffset> moves
        // the baseline the sprite sits on.
        //
        // InvariantCulture on both numbers, and that is not fussiness: on a machine whose locale
        // uses a decimal comma the default formatting emits <voffset=-0,06em>, which TMP does not
        // parse and which shows up as the tag being printed as text.
        public static string Tag
        {
            get
            {
                string percent = (BadgeAppearance.TooltipScale * 100f)
                    .ToString("0.##", CultureInfo.InvariantCulture);
                string rise = BadgeAppearance.TooltipRise
                    .ToString("0.###", CultureInfo.InvariantCulture);

                return "<size=" + percent + "%><voffset=" + rise + "em>" +
                       "<sprite name=\"" + SpriteName + "\">" +
                       "</voffset></size>";
            }
        }

        private static TMP_SpriteAsset _asset;
        private static Material _material;

        // Every asset we have hung ourselves off, so that unloading can take us back out of them.
        // These belong to the game and outlive the mod.
        private static readonly List<TMP_SpriteAsset> Hosts = new List<TMP_SpriteAsset>();

        // Makes the tag resolvable for this text component, and says whether it worked. A caller
        // that gets false leaves the tag out rather than writing one that would draw a blank box.
        public static bool Attach(TMP_Text text)
        {
            if (text == null) return false;

            // Whichever asset this component's <sprite> tags already resolve against, which is the
            // one our name has to be reachable from. The order matches TMP_Text's own.
            var host = text.spriteAsset != null ? text.spriteAsset : TMP_Settings.defaultSpriteAsset;
            if (host == null) return false;

            var asset = Build(host);
            if (asset == null) return false;

            if (host.fallbackSpriteAssets == null)
                host.fallbackSpriteAssets = new List<TMP_SpriteAsset>();

            if (!host.fallbackSpriteAssets.Contains(asset))
            {
                host.fallbackSpriteAssets.Add(asset);
                Hosts.Add(host);
            }

            return true;
        }

        public static void Detach()
        {
            foreach (var host in Hosts)
            {
                if (host == null || host.fallbackSpriteAssets == null) continue;
                host.fallbackSpriteAssets.Remove(_asset);
            }
            Hosts.Clear();

            if (_asset != null) Object.Destroy(_asset);
            if (_material != null) Object.Destroy(_material);
            _asset = null;
            _material = null;
        }

        private static TMP_SpriteAsset Build(TMP_SpriteAsset template)
        {
            if (_asset != null) return _asset;

            var texture = Badge.Texture;
            var sprite = Badge.Sprite;
            if (texture == null || sprite == null) return null;

            // The template's material rather than Shader.Find("TextMeshPro/Sprite"): a shader is
            // only findable if the build kept it, and borrowing the one the game is already
            // drawing sprites with cannot be wrong about that.
            if (template.material == null) return null;
            _material = new Material(template.material) { hideFlags = HideFlags.HideAndDontSave };
            _material.SetTexture(ShaderUtilities.ID_MainTex, texture);

            var asset = ScriptableObject.CreateInstance<TMP_SpriteAsset>();
            asset.name = "AreMyGemsCompatible Mark";
            asset.hideFlags = HideFlags.HideAndDontSave;
            asset.spriteSheet = texture;

            // Filled in the legacy shape and then upgraded, rather than by writing the character
            // and glyph tables directly. TMP_SpriteAsset.UpdateLookupTables calls
            // UpgradeSpriteAsset whenever a material is present and the version string is empty,
            // and that upgrade *clears* both tables and rebuilds them from spriteInfoList - so
            // hand-built tables would be wiped on the first lookup, and a null spriteInfoList
            // would throw. Meeting it where it starts is both shorter and not a race.
            //
            // yOffset is the whole height and xAdvance the whole width, which is what makes the
            // mark sit on the line like a capital letter. faceInfo is deliberately left at zero:
            // TMP_Text scales a sprite from an asset with no point size to the font's own ascent
            // line, which is exactly the wanted behaviour and needs no numbers from here.
            asset.spriteInfoList = new List<TMP_Sprite>
            {
                new TMP_Sprite
                {
                    id = 0,
                    name = SpriteName,
                    hashCode = TMP_TextUtilities.GetSimpleHashCode(SpriteName),
                    unicode = 0,
                    x = 0f,
                    y = 0f,
                    width = texture.width,
                    height = texture.height,
                    xOffset = 0f,
                    yOffset = texture.height,
                    xAdvance = texture.width,
                    scale = 1f,
                    pivot = new Vector2(0.5f, 0.5f),
                    sprite = sprite,
                },
            };

            asset.material = _material;
            asset.UpdateLookupTables();

            _asset = asset;
            return _asset;
        }
    }
}
