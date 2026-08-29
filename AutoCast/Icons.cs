using UnityEngine;

namespace AutoCast
{
    // The artwork, embedded in the assembly rather than shipped as loose files. ModItem.path
    // would let them be read off disk, but a single self-contained dll has no way to arrive
    // without its art, which is worth more than being able to swap a png by hand.
    //
    // Each state is two sprites, because the arrows turn and the frame does not. They carry
    // straight alpha and share one framing, so stacking them at rest reproduces the original
    // icon exactly and nothing shifts when the state changes.
    internal static class Icons
    {
        private static readonly string[] StateNames = { "autocast_off", "autocast_on", "autocast_locked" };

        private static bool _loaded;
        private static Sprite[] _rings;
        private static Sprite[] _arrows;
        private static Sprite _plate;

        public static Sprite Ring(int state) { Load(); return _rings[Mathf.Clamp(state, 0, _rings.Length - 1)]; }
        public static Sprite Arrows(int state) { Load(); return _arrows[Mathf.Clamp(state, 0, _arrows.Length - 1)]; }
        public static Sprite Plate { get { Load(); return _plate; } }

        private static void Load()
        {
            if (_loaded) return;
            _loaded = true;

            _rings = new Sprite[StateNames.Length];
            _arrows = new Sprite[StateNames.Length];
            for (int i = 0; i < StateNames.Length; i++)
            {
                _rings[i] = Read(StateNames[i] + "_ring");
                _arrows[i] = Read(StateNames[i] + "_arrows");
            }
            _plate = Read("plate");
        }

        private static Sprite Read(string name)
        {
            var assembly = typeof(Icons).Assembly;
            var resource = "AutoCast." + name + ".png";

            using (var stream = assembly.GetManifestResourceStream(resource))
            {
                if (stream == null)
                {
                    Debug.LogError("[AutoCast] icon resource missing: " + resource);
                    return null;
                }

                var bytes = new byte[stream.Length];
                int read = 0;
                while (read < bytes.Length)
                {
                    int got = stream.Read(bytes, read, bytes.Length - read);
                    if (got <= 0) break;
                    read += got;
                }

                // Mip chain on: the icon is drawn at roughly a quarter of its stored size at
                // 1080p, and without mips that reduction crawls with aliasing as the HUD moves.
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                if (!texture.LoadImage(bytes))
                {
                    Debug.LogError("[AutoCast] icon failed to decode: " + resource);
                    Object.Destroy(texture);
                    return null;
                }

                texture.name = "AutoCast_" + name;
                texture.filterMode = FilterMode.Trilinear;
                texture.wrapMode = TextureWrapMode.Clamp;

                // Held in statics for the life of the process, so keep it out of the reach of
                // Resources.UnloadUnusedAssets.
                texture.hideFlags = HideFlags.HideAndDontSave;

                var sprite = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height),
                                           new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
                sprite.name = texture.name;
                sprite.hideFlags = HideFlags.HideAndDontSave;
                return sprite;
            }
        }
    }
}
