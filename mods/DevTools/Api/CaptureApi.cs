#if DEBUG
using System;
using System.Collections;
using System.IO;
using UnityEngine;

namespace DevTools
{
    // What the screen looks like, for an agent that can look at images. Written to a file whose
    // path is answered, since a file is what most agent tools read images from; base64 on request.
    internal static class CaptureApi
    {
        public static string Directory => Path.Combine(Application.persistentDataPath, "DevTools", "screenshots");

        [Route("POST", "/screenshot", "A PNG (or JPG) of the screen as it is at the end of this frame, scaled down to max_width. Answers with the file's path.",
               "name?, max_width=1280, format=png|jpg, base64=false")]
        private static IEnumerator Shot(Args a)
        {
            string name = a.Str("name", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
            foreach (var bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');
            bool jpg = a.Str("format", "png").Equals("jpg", StringComparison.OrdinalIgnoreCase) ||
                       a.Str("format", "png").Equals("jpeg", StringComparison.OrdinalIgnoreCase);
            int maxWidth = Mathf.Clamp(a.Int("max_width", 1280), 64, 8192);

            // CaptureScreenshotAsTexture reads the back buffer, which is only complete here.
            yield return new WaitForEndOfFrame();

            // Built before answering rather than in a try/finally around the answer: the server
            // stops driving a routine once it has its Reply, so a finally after it would never run.
            yield return new Reply(Capture(name, jpg, maxWidth, a.Bool("base64")));
        }

        private static object Capture(string name, bool jpg, int maxWidth, bool base64)
        {
            var full = ScreenCapture.CaptureScreenshotAsTexture();
            var shot = full;
            try
            {
                if (full.width > maxWidth)
                {
                    int w = maxWidth, h = Mathf.RoundToInt(full.height * (maxWidth / (float)full.width));
                    var rt = RenderTexture.GetTemporary(w, h, 0);
                    Graphics.Blit(full, rt);
                    var previous = RenderTexture.active;
                    RenderTexture.active = rt;
                    shot = new Texture2D(w, h, TextureFormat.RGB24, false);
                    shot.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                    shot.Apply();
                    RenderTexture.active = previous;
                    RenderTexture.ReleaseTemporary(rt);
                }

                var bytes = jpg ? shot.EncodeToJPG(85) : shot.EncodeToPNG();
                System.IO.Directory.CreateDirectory(Directory);
                string path = Path.Combine(Directory, name + (jpg ? ".jpg" : ".png"));
                File.WriteAllBytes(path, bytes);

                return new
                {
                    path,
                    width = shot.width,
                    height = shot.height,
                    screen = new { w = Screen.width, h = Screen.height },
                    note = "screen coordinates elsewhere are in screen pixels from the bottom-left; scale by screen.w / width",
                    base64 = base64 ? Convert.ToBase64String(bytes) : null,
                };
            }
            finally
            {
                if (shot != full) UnityEngine.Object.Destroy(shot);
                UnityEngine.Object.Destroy(full);
            }
        }
    }
}
#endif
