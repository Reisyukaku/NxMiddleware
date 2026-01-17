using BnTxx.Formats;
using System;
using System.Drawing;
using System.IO;

namespace BnTxx
{
    public class BNTX
    {
        public static bool TryLoadFromBytes(byte[] data, string preferredName, out Bitmap bitmap, out string error)
        {
            bitmap = null;
            error = null;
            using var ms = new MemoryStream(data, writable: false);
            return TryLoadFromStream(ms, preferredName, out bitmap, out error);
        }

        public static bool TryLoadFromStream(Stream stream, string preferredName, out Bitmap bitmap, out string error)
        {
            bitmap = null;
            error = null;
            long start = 0;
            try { start = stream.CanSeek ? stream.Position : 0; } catch { start = 0; }

            try
            {
                BinaryTexture BT = new BinaryTexture(stream);
                int chosenIndex = -1;

                if (!string.IsNullOrEmpty(preferredName))
                {
                    for (int i = 0; i < BT.Textures.Count; i++)
                    {
                        if (string.Equals(BT.Textures[i].Name, preferredName, StringComparison.OrdinalIgnoreCase))
                        {
                            chosenIndex = i;
                            break;
                        }
                    }
                }

                if (chosenIndex == -1 && BT.Textures.Count > 0)
                {
                    chosenIndex = 0;
                }

                if (chosenIndex != -1)
                {
                    var chosen = BT.Textures[chosenIndex];
                    if (!PixelDecoder.TryDecode(chosen, out Bitmap Img))
                    {
                        error = $"Unsupported or unhandled format: {chosen.FormatType}";
                        for (int i = 0; i < BT.Textures.Count; i++)
                        {
                            if (PixelDecoder.TryDecode(BT.Textures[i], out Img))
                            {
                                bitmap = Img;
                                error = null;
                                return true;
                            }
                        }
                    }
                    else
                    {
                        bitmap = Img;
                        return true;
                    }
                }
            }
            finally
            {
                try
                {
                    if (stream.CanSeek)
                    {
                        stream.Position = start;
                    }
                }
                catch
                {
                }
            }

            return bitmap != null;
        }

        public static Bitmap LoadFromFile(string file)
        {
            return LoadFromFile(file, null);
        }

        public static Bitmap LoadFromFile(string file, string preferredName)
        {
            TryLoadFromFile(file, preferredName, out var bm, out _);
            return bm;
        }

        public static bool TryLoadFromFile(string file, string preferredName, out Bitmap bitmap, out string error)
        {
            bitmap = null;
            error = null;
            using (FileStream FS = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                return TryLoadFromStream(FS, preferredName, out bitmap, out error);
            }
        }
    }
}
