using BnTxx.Formats;
using BnTxx.Utilities;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace BnTxx
{
#pragma warning disable CA1416 // System.Drawing is Windows-only; entry points are OS-guarded (TryDecode).
    static class PixelDecoder
    {
        private delegate Bitmap DecodeFunc(Texture Tex, int Offset);

        private static
            Dictionary<TextureFormatType, DecodeFunc> DecodeFuncs = new
            Dictionary<TextureFormatType, DecodeFunc>()
        {
            { TextureFormatType.R5G6B5,    DecodeR5G6B5    },
            { TextureFormatType.R8G8,      DecodeR8G8      },
            { TextureFormatType.R16,       DecodeR16       },
            { TextureFormatType.R8G8B8A8,  DecodeR8G8B8A8  },
            { TextureFormatType.R11G11B10, DecodeR11G11B10 },
            { TextureFormatType.R32,       DecodeR32       },
            { TextureFormatType.BC1,       BCn.DecodeBC1   },
            { TextureFormatType.BC2,       BCn.DecodeBC2   },
            { TextureFormatType.BC3,       BCn.DecodeBC3   },
            { TextureFormatType.BC4,       BCn.DecodeBC4   },
            { TextureFormatType.BC5,       BCn.DecodeBC5   },
            { TextureFormatType.BC6,       BCn.DecodeBC6   },
            { TextureFormatType.BC7,       BCn.DecodeBC7   }
        };

        public static bool TryDecode(Texture Tex, out Bitmap Img, int Offset = 0)
        {
            if (!OperatingSystem.IsWindows())
            {
                Img = null;
                return false;
            }

            return TryDecodeWindows(Tex, out Img, Offset);
        }

        [SupportedOSPlatform("windows")]
        private static bool TryDecodeWindows(Texture Tex, out Bitmap Img, int Offset)
        {
            if (DecodeFuncs.ContainsKey(Tex.FormatType))
            {
                Img = DecodeFuncs[Tex.FormatType](Tex, Offset);

                if (Img.Width != Tex.Width ||
                    Img.Height != Tex.Height)
                {
                    Bitmap Output = new Bitmap(Tex.Width, Tex.Height);

                    using (Graphics g = Graphics.FromImage(Output))
                    {
                        Rectangle Rect = new Rectangle(0, 0, Tex.Width, Tex.Height);

                        g.DrawImage(Img, Rect, Rect, GraphicsUnit.Pixel);
                    }

                    Img.Dispose();

                    Img = Output;
                }

                // Apply the per-texture channel mapping (swizzle) from the BNTX header.
                // Some mask textures rely on this (e.g., layer masks where the "layer3" weight may be stored
                // in the file's red channel but remapped to blue via ChannelTypes).
                ApplyChannelTypesInPlace(Img, Tex.Channel0Type, Tex.Channel1Type, Tex.Channel2Type, Tex.Channel3Type);

                return true;
            }

            // Fallback for formats we don't explicitly map yet.
            // Some packs store mask-like textures inside 64-bit depth-stencil surfaces (commonly reported as
            // D32_FLOAT_S8X24_UINT). Treat them as a single-channel float in the first 4 bytes of an 8-byte texel.
            if (TryDecodeDepthFloat8Bpp(Tex, Offset, out Img))
            {
                ApplyChannelTypesInPlace(Img, Tex.Channel0Type, Tex.Channel1Type, Tex.Channel2Type, Tex.Channel3Type);
                return true;
            }

            Img = null;
            return false;
        }

        private static void ApplyChannelTypesInPlace(Bitmap img, ChannelType ch0, ChannelType ch1, ChannelType ch2, ChannelType ch3)
        {
            if (img == null)
            {
                return;
            }

            // Fast path: default RGBA mapping.
            if (ch0 == ChannelType.Red &&
                ch1 == ChannelType.Green &&
                ch2 == ChannelType.Blue &&
                ch3 == ChannelType.Alpha)
            {
                return;
            }

            // This decoder uses PixelFormat.Format32bppArgb with a BGRA byte layout.
            if (img.PixelFormat != PixelFormat.Format32bppArgb)
            {
                // Keep behavior predictable: only apply swizzle when we know the byte layout.
                return;
            }

            Rectangle rect = new Rectangle(0, 0, img.Width, img.Height);
            BitmapData imgData = img.LockBits(rect, ImageLockMode.ReadWrite, img.PixelFormat);

            try
            {
                int byteCount = Math.Abs(imgData.Stride) * img.Height;
                byte[] buffer = new byte[byteCount];
                Marshal.Copy(imgData.Scan0, buffer, 0, buffer.Length);

                // Permute in-place using the same mapping as PermChAndGetBitmap.
                ChannelType[] types = new[] { ch0, ch1, ch2, ch3 };
                if (types.Length == 4)
                {
                    for (int offset = 0; offset + 3 < buffer.Length; offset += 4)
                    {
                        byte b = buffer[offset + 0];
                        byte g = buffer[offset + 1];
                        byte r = buffer[offset + 2];
                        byte a = buffer[offset + 3];

                        int j = 0;
                        foreach (int i in new int[] { 2, 1, 0, 3 })
                        {
                            switch (types[j++])
                            {
                                case ChannelType.Zero: buffer[offset + i] = 0; break;
                                case ChannelType.One: buffer[offset + i] = 0xff; break;
                                case ChannelType.Red: buffer[offset + i] = r; break;
                                case ChannelType.Green: buffer[offset + i] = g; break;
                                case ChannelType.Blue: buffer[offset + i] = b; break;
                                case ChannelType.Alpha: buffer[offset + i] = a; break;
                            }
                        }
                    }
                }

                Marshal.Copy(buffer, 0, imgData.Scan0, buffer.Length);
            }
            finally
            {
                img.UnlockBits(imgData);
            }
        }

        private static bool TryDecodeDepthFloat8Bpp(Texture Tex, int offset, out Bitmap img)
        {
            img = null;

            if (Tex.Width <= 0 || Tex.Height <= 0 || Tex.Data == null || Tex.Data.Length < 8)
            {
                return false;
            }

            long pixelCount = (long)Tex.Width * Tex.Height;
            if (pixelCount <= 0)
            {
                return false;
            }

            long mip0Size = Tex.MipmapCount > 1 ? Tex.MipOffsets[1] : Tex.Data.Length;
            if (mip0Size <= 0)
            {
                mip0Size = Tex.Data.Length;
            }

            float estimate = (float)mip0Size / pixelCount;
            if (!(estimate >= 7.5f && estimate <= 8.5f))
            {
                return false;
            }

            try
            {
                int bytesPerTexel = 8;
                byte[] output = new byte[Tex.Width * Tex.Height * 4];
                int o = 0;
                ISwizzle swizzle = new BlockLinearSwizzle(Tex.GetWidthInTexels(), bytesPerTexel, Tex.GetBlockHeight());

                using var ms = new MemoryStream(Tex.Data);
                using var reader = new BinaryReader(ms);

                for (int y = 0; y < Tex.Height; y++)
                {
                    for (int x = 0; x < Tex.Width; x++)
                    {
                        int i = offset + swizzle.GetSwizzleOffset(x, y);
                        float value = 0.0f;
                        if (i >= 0 && i + 4 <= Tex.Data.Length)
                        {
                            ms.Seek(i, SeekOrigin.Begin);
                            value = reader.ReadSingle();
                            if (float.IsNaN(value) || float.IsInfinity(value))
                            {
                                value = 0.0f;
                            }
                        }

                        float clamped = Math.Min(1.0f, Math.Max(0.0f, value));
                        byte b = (byte)(clamped * 0xff);
                        output[o + 0] = b;
                        output[o + 1] = b;
                        output[o + 2] = b;
                        output[o + 3] = 0xff;
                        o += 4;
                    }
                }

                img = GetBitmap(output, Tex.Width, Tex.Height);
                return img != null;
            }
            catch
            {
                img = null;
                return false;
            }
        }

        public static Bitmap DecodeR5G6B5(Texture Tex, int Offset)
        {
            byte[] Output = new byte[Tex.Width * Tex.Height * 4];

            int OOffset = 0;

            ISwizzle Swizzle = Tex.GetSwizzle();

            for (int Y = 0; Y < Tex.Height; Y++)
            {
                for (int X = 0; X < Tex.Width; X++)
                {
                    int IOffs = Offset + Swizzle.GetSwizzleOffset(X, Y);

                    int Value =
                        Tex.Data[IOffs + 0] << 0 |
                        Tex.Data[IOffs + 1] << 8;

                    int R = ((Value >> 0) & 0x1f) << 3;
                    int G = ((Value >> 5) & 0x3f) << 2;
                    int B = ((Value >> 11) & 0x1f) << 3;

                    Output[OOffset + 0] = (byte)(B | (B >> 5));
                    Output[OOffset + 1] = (byte)(G | (G >> 6));
                    Output[OOffset + 2] = (byte)(R | (R >> 5));
                    Output[OOffset + 3] = 0xff;

                    OOffset += 4;
                }
            }

            return GetBitmap(Output, Tex.Width, Tex.Height);
        }

        public static Bitmap DecodeR8G8(Texture Tex, int Offset)
        {
            byte[] Output = new byte[Tex.Width * Tex.Height * 4];

            int OOffset = 0;

            ISwizzle Swizzle = Tex.GetSwizzle();

            for (int Y = 0; Y < Tex.Height; Y++)
            {
                for (int X = 0; X < Tex.Width; X++)
                {
                    int IOffs = Offset + Swizzle.GetSwizzleOffset(X, Y);

                    Output[OOffset + 1] = Tex.Data[IOffs + 1];
                    Output[OOffset + 2] = Tex.Data[IOffs + 0];
                    Output[OOffset + 3] = 0xff;

                    OOffset += 4;
                }
            }

            return GetBitmap(Output, Tex.Width, Tex.Height);
        }

        public static Bitmap DecodeR16(Texture Tex, int Offset)
        {
            //TODO: What should be done with the extra precision?
            //TODO: Can this be used with Half floats too?
            byte[] Output = new byte[Tex.Width * Tex.Height * 4];

            int OOffset = 0;

            ISwizzle Swizzle = Tex.GetSwizzle();

            for (int Y = 0; Y < Tex.Height; Y++)
            {
                for (int X = 0; X < Tex.Width; X++)
                {
                    int IOffs = Offset + Swizzle.GetSwizzleOffset(X, Y);

                    Output[OOffset + 2] = Tex.Data[IOffs + 1];
                    Output[OOffset + 3] = 0xff;

                    OOffset += 4;
                }
            }

            return GetBitmap(Output, Tex.Width, Tex.Height);
        }

        public static Bitmap DecodeR8G8B8A8(Texture Tex, int Offset)
        {
            byte[] Output = new byte[Tex.Width * Tex.Height * 4];

            int OOffset = 0;

            ISwizzle Swizzle = Tex.GetSwizzle();

            for (int Y = 0; Y < Tex.Height; Y++)
            {
                for (int X = 0; X < Tex.Width; X++)
                {
                    int IOffs = Offset + Swizzle.GetSwizzleOffset(X, Y);

                    Output[OOffset + 0] = Tex.Data[IOffs + 2];
                    Output[OOffset + 1] = Tex.Data[IOffs + 1];
                    Output[OOffset + 2] = Tex.Data[IOffs + 0];
                    Output[OOffset + 3] = Tex.Data[IOffs + 3];

                    OOffset += 4;
                }
            }

            return GetBitmap(Output, Tex.Width, Tex.Height);
        }

        public static Bitmap DecodeR11G11B10(Texture Tex, int Offset)
        {
            //TODO: What should be done with the extra precision?
            byte[] Output = new byte[Tex.Width * Tex.Height * 4];

            int OOffset = 0;

            ISwizzle Swizzle = Tex.GetSwizzle();

            for (int Y = 0; Y < Tex.Height; Y++)
            {
                for (int X = 0; X < Tex.Width; X++)
                {
                    int IOffs = Offset + Swizzle.GetSwizzleOffset(X, Y);

                    int Value = IOUtils.Get32(Tex.Data, IOffs);

                    int R = (Value >> 0) & 0x7ff;
                    int G = (Value >> 11) & 0x7ff;
                    int B = (Value >> 22) & 0x3ff;

                    Output[OOffset + 0] = (byte)(B >> 2);
                    Output[OOffset + 1] = (byte)(G >> 3);
                    Output[OOffset + 2] = (byte)(R >> 3);
                    Output[OOffset + 3] = 0xff;

                    OOffset += 4;
                }
            }

            return GetBitmap(Output, Tex.Width, Tex.Height);
        }

        public static Bitmap DecodeR32(Texture Tex, int Offset)
        {
            // Decodes single-channel 32-bit textures. Some games also store 32-bit float masks inside
            // 64-bit depth-stencil surfaces (D32_FLOAT_S8X24_UINT); in those cases the first 32 bits
            // are still a float, but each texel is 8 bytes.
            byte[] Output = new byte[Tex.Width * Tex.Height * 4];

            int OOffset = 0;

            int bytesPerTexel = 4;
            try
            {
                long mip0Size = Tex.MipmapCount > 1 ? Tex.MipOffsets[1] : Tex.Data.Length;
                long pixelCount = (long)Tex.Width * Tex.Height;
                if (pixelCount > 0)
                {
                    float estimate = (float)mip0Size / pixelCount;
                    if (estimate >= 7.5f && estimate <= 8.5f)
                    {
                        bytesPerTexel = 8;
                    }
                }
            }
            catch
            {
            }

            ISwizzle Swizzle = bytesPerTexel == 4
                ? Tex.GetSwizzle()
                : new BlockLinearSwizzle(Tex.GetWidthInTexels(), bytesPerTexel, Tex.GetBlockHeight());

            using (MemoryStream MS = new MemoryStream(Tex.Data))
            {
                BinaryReader Reader = new BinaryReader(MS);

                for (int Y = 0; Y < Tex.Height; Y++)
                {
                    for (int X = 0; X < Tex.Width; X++)
                    {
                        int IOffs = Offset + Swizzle.GetSwizzleOffset(X, Y);

                        if (IOffs < 0 || IOffs + 4 > Tex.Data.Length)
                        {
                            Output[OOffset + 3] = 0xff;
                            OOffset += 4;
                            continue;
                        }

                        MS.Seek(IOffs, SeekOrigin.Begin);
                        float Value = Reader.ReadSingle();
                        if (float.IsNaN(Value) || float.IsInfinity(Value))
                        {
                            Value = 0.0f;
                        }

                        float clamped = Math.Min(1.0f, Math.Max(0.0f, Value));
                        byte b = (byte)(clamped * 0xff);
                        Output[OOffset + 0] = b;
                        Output[OOffset + 1] = b;
                        Output[OOffset + 2] = b;
                        Output[OOffset + 3] = 0xff;

                        OOffset += 4;
                    }
                }
            }

            return GetBitmap(Output, Tex.Width, Tex.Height);
        }

        public static Bitmap PermChAndGetBitmap(
            byte[] Buffer,
            int Width,
            int Height,
            params ChannelType[] ChTypes)
        {
            if (ChTypes.Length == 4 && (
                ChTypes[0] != ChannelType.Red ||
                ChTypes[1] != ChannelType.Green ||
                ChTypes[2] != ChannelType.Blue ||
                ChTypes[3] != ChannelType.Alpha))
            {
                for (int Offset = 0; Offset < Buffer.Length; Offset += 4)
                {
                    byte B = Buffer[Offset + 0];
                    byte G = Buffer[Offset + 1];
                    byte R = Buffer[Offset + 2];
                    byte A = Buffer[Offset + 3];

                    int j = 0;

                    foreach (int i in new int[] { 2, 1, 0, 3 })
                    {
                        switch (ChTypes[j++])
                        {
                            case ChannelType.Zero: Buffer[Offset + i] = 0; break;
                            case ChannelType.One: Buffer[Offset + i] = 0xff; break;
                            case ChannelType.Red: Buffer[Offset + i] = R; break;
                            case ChannelType.Green: Buffer[Offset + i] = G; break;
                            case ChannelType.Blue: Buffer[Offset + i] = B; break;
                            case ChannelType.Alpha: Buffer[Offset + i] = A; break;
                        }
                    }
                }
            }

            return GetBitmap(Buffer, Width, Height);
        }

        public static Bitmap GetBitmap(byte[] Buffer, int Width, int Height)
        {
            Rectangle Rect = new Rectangle(0, 0, Width, Height);

            Bitmap Img = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);

            BitmapData ImgData = Img.LockBits(Rect, ImageLockMode.WriteOnly, Img.PixelFormat);

            Marshal.Copy(Buffer, 0, ImgData.Scan0, Buffer.Length);

            Img.UnlockBits(ImgData);

            return Img;
        }
    }
#pragma warning restore CA1416
}
