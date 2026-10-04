using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace ADOFAIEditorExtension.Utils
{
    /// <summary>标签页图标：原版 AddDecoration 图标右上角加白色加号（离线按原版贴图生成，嵌在 DLL 里）。</summary>
    internal static class TabIcon
    {
        private const string ResourceName = "AeeTabIcon.png";

        // 原版图标贴图不可读，运行时从显卡读回会丢透明度（实机出现整块灰方块），所以改用离线成品
        internal static Sprite Build(Sprite baseSprite)
        {
            byte[] data;
            using (Stream stream = typeof(TabIcon).Assembly.GetManifestResourceStream(ResourceName))
            {
                if (stream == null)
                    throw new InvalidOperationException("缺少内嵌资源 " + ResourceName);
                data = new byte[stream.Length];
                int read = 0;
                while (read < data.Length)
                {
                    int n = stream.Read(data, read, data.Length - read);
                    if (n <= 0)
                        break;
                    read += n;
                }
            }

            // 新编译器会把 LoadImage 解析到 ReadOnlySpan 重载（net48 没有），按名字反射绑定 byte[] 版
            Type conversion = Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule");
            MethodInfo loadImage = conversion?.GetMethod("LoadImage", new[] { typeof(Texture2D), typeof(byte[]) });
            if (loadImage == null)
                throw new MissingMethodException("ImageConversion.LoadImage(Texture2D, byte[])");

            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!(bool)loadImage.Invoke(null, new object[] { texture, data }))
            {
                UnityEngine.Object.Destroy(texture);
                throw new InvalidOperationException("内嵌图标解码失败");
            }
            texture.name = "AeeTabIcon";
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            texture.hideFlags = HideFlags.DontUnloadUnusedAsset;

            Rect baseRect = baseSprite.rect;
            Vector2 pivot = new Vector2(baseSprite.pivot.x / baseRect.width, baseSprite.pivot.y / baseRect.height);
            Sprite sprite = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), pivot,
                baseSprite.pixelsPerUnit * texture.width / baseRect.width, 0, SpriteMeshType.FullRect);
            sprite.name = "AeeTabIcon";
            sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return sprite;
        }
    }
}
