using UnityEngine;

namespace AreMyGemsCompatible
{
    // The mark drawn over a slot, embedded in the assembly rather than shipped as a loose file.
    // ModItem.path would let it be read off disk, but a single self-contained dll has no way to
    // arrive without its art.
    internal static class Badge
    {
        private const string Resource = "AreMyGemsCompatible.badge.png";

        private static bool _loaded;
        private static Sprite _sprite;

        public static Sprite Sprite
        {
            get
            {
                Load();
                return _sprite;
            }
        }

        // The texture behind it, which the tooltip needs separately: a TMP sprite asset is built
        // around an atlas texture rather than around a Sprite.
        public static Texture2D Texture
        {
            get
            {
                Load();
                return _sprite != null ? _sprite.texture : null;
            }
        }

        private static void Load()
        {
            if (_loaded) return;
            _loaded = true;

            var assembly = typeof(Badge).Assembly;
            using (var stream = assembly.GetManifestResourceStream(Resource))
            {
                if (stream == null)
                {
                    Debug.LogError("[AreMyGemsCompatible] badge resource missing: " + Resource);
                    return;
                }

                var bytes = new byte[stream.Length];
                int read = 0;
                while (read < bytes.Length)
                {
                    int got = stream.Read(bytes, read, bytes.Length - read);
                    if (got <= 0) break;
                    read += got;
                }

                // Mip chain on: the badge is drawn at well under a quarter of its stored size, and
                // without mips that reduction crawls with aliasing as the HUD moves.
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                if (!texture.LoadImage(bytes))
                {
                    Debug.LogError("[AreMyGemsCompatible] badge failed to decode");
                    Object.Destroy(texture);
                    return;
                }

                texture.name = "AreMyGemsCompatible_badge";
                texture.filterMode = FilterMode.Trilinear;
                texture.wrapMode = TextureWrapMode.Clamp;

                // Held in a static for the life of the process, so keep it out of the reach of
                // Resources.UnloadUnusedAssets.
                texture.hideFlags = HideFlags.HideAndDontSave;

                _sprite = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height),
                                        new Vector2(0.5f, 0.5f), 100f, 0u, SpriteMeshType.FullRect);
                _sprite.name = "AreMyGemsCompatible_badge";
                _sprite.hideFlags = HideFlags.HideAndDontSave;
            }
        }
    }
}
