using BnTxx.Formats;
using System;
using System.Drawing;
using System.IO;

namespace BnTxx
{
    public class BNTX
    {
        public static Bitmap LoadFromFile(string file)
        {
            return LoadFromFile(file, null);
        }

        public static Bitmap LoadFromFile(string file, string preferredName)
        {
            Bitmap bm = null;
            using (FileStream FS = new FileStream(file, FileMode.Open))
            {
                BinaryTexture BT = new BinaryTexture(FS);
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
                        for (int i = 0; i < BT.Textures.Count; i++)
                        {
                            if (PixelDecoder.TryDecode(BT.Textures[i], out Img))
                            {
                                bm = Img;
                                return bm;
                            }
                        }
                    }
                    else
                    {
                        bm = Img;
                    }
                }
            }

            return bm;
        }
    }
}
