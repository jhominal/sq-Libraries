﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Squared.Util;

namespace Squared.Render.STB {
    public unsafe class Image : IDisposable {
        public int Width, Height, ChannelCount;
        public bool IsDisposed { get; private set; }
        public void* Data { get; private set; }
        public bool IsFloatingPoint { get; private set; }
        public bool Is16Bit { get; private set; }

        private byte[][] MipChain = null;

        private static FileStream OpenStream (string path) {
            return File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }

        public Image (string path, bool premultiply = true, bool asFloatingPoint = false, bool enable16Bit = false)
            : this (OpenStream(path), true, premultiply, asFloatingPoint) {
        }

        public Image (Stream stream, bool ownsStream, bool premultiply = true, bool asFloatingPoint = false, bool enable16Bit = false, bool generateMips = false) {
            var length = stream.Length - stream.Position;

            if (!stream.CanSeek)
                throw new ArgumentException("Stream must be seekable");
            if (!stream.CanRead)
                throw new ArgumentException("Stream must be readable");

            byte[] buffer;
            int readOffset;
            var ms = stream as MemoryStream;
            if (ms != null) {
                buffer = ms.GetBuffer();
                readOffset = (int)ms.Position;
            } else {
                buffer = new byte[length];
                readOffset = 0;
                stream.Read(buffer, 0, (int)length);
            }

            InitializeFromBuffer(
                buffer, readOffset, (int)length, 
                premultiply: premultiply, 
                asFloatingPoint: asFloatingPoint, 
                enable16Bit: enable16Bit,
                generateMips: generateMips
            );

            if (ownsStream)
                stream.Dispose();
        }

        public Image (ArraySegment<byte> buffer, bool premultiply = true, bool asFloatingPoint = false, bool generateMips = false) {
            InitializeFromBuffer(
                buffer.Array, buffer.Offset, buffer.Count,
                premultiply: premultiply, 
                asFloatingPoint: asFloatingPoint,
                enable16Bit: false,
                generateMips: generateMips
            );
        }

        private void InitializeFromBuffer (
            byte[] buffer, int offset, int length, 
            bool premultiply = true, bool asFloatingPoint = false, 
            bool enable16Bit = false, bool generateMips = false
        ) {
            IsFloatingPoint = asFloatingPoint;

            // FIXME: Don't request RGBA?
            fixed (byte * pBuffer = buffer) {
                Is16Bit = enable16Bit && Native.API.stbi_is_16_bit_from_memory(pBuffer + offset, length) != 0;

                if (asFloatingPoint)
                    Data = Native.API.stbi_loadf_from_memory(pBuffer + offset, length, out Width, out Height, out ChannelCount, 4);
                else if (Is16Bit)
                    Data = Native.API.stbi_load_16_from_memory(pBuffer + offset, length, out Width, out Height, out ChannelCount, 4);
                else
                    Data = Native.API.stbi_load_from_memory(pBuffer + offset, length, out Width, out Height, out ChannelCount, 4);
            }

            if (Data == null) {
                var reason = STB.Native.API.stbi_failure_reason();
                var message = "Failed to load image";
                if (reason != null)
                    message += ": " + Encoding.UTF8.GetString(reason, 128);
                throw new Exception(message);
            }

            int components;
            SizeofPixel = STB.ImageWrite.GetBytesPerPixelAndComponents(Format, out components);

            if (asFloatingPoint)
                ConvertFPData(premultiply);
            else if (Is16Bit)
                ConvertData16(premultiply);
            else
                ConvertData(premultiply);

            if (generateMips)
                GenerateMips();
        }

        private unsafe void ConvertFPData (bool premultiply) {
            if (premultiply)
                PremultiplyFPData();
        }

        private unsafe void ConvertData16 (bool premultiply) {
            if (premultiply)
                throw new NotImplementedException();
        }

        private unsafe void ConvertData (bool premultiply) {
#if FNA
            if (premultiply)
                PremultiplyData();
#else
            if (premultiply)
                PremultiplyAndChannelSwapData();
            else
                ChannelSwapData();
#endif
        }

        private unsafe void PremultiplyFPData () {
            var pData = (float*)Data;
            var pEnd = pData + (Width * Height * ChannelCount);
            for (; pData < pEnd; pData+=4) {
                var a = pData[3];
                var temp = pData[0];
                pData[0] *= a;
                pData[1] *= a;
                pData[2] *= a;
            }
        }

        private unsafe void PremultiplyData () {
            var pData = (uint*)Data;
            var pBytes = (byte*)Data;
            var pEnd = pData + (Width * Height);
            for (; pData < pEnd; pData++, pBytes+=4) {
                var value = *pData;
                var a = (value & 0xFF000000) >> 24;
                var r = (value & 0xFF);
                var g = (value & 0xFF00) >> 8;
                var b = (value & 0xFF0000) >> 16;
                pBytes[0] = (byte)(r * a / 255);
                pBytes[1] = (byte)(g * a / 255);
                pBytes[2] = (byte)(b * a / 255);
            }
        }

        private unsafe void PremultiplyAndChannelSwapData () {
            var pData = (uint*)Data;
            var pBytes = (byte*)Data;
            var pEnd = pData + (Width * Height);
            for (; pData < pEnd; pData++, pBytes+=4) {
                var value = *pData;
                var a = (value & 0xFF000000) >> 24;
                var r = (value & 0xFF);
                var g = (value & 0xFF00) >> 8;
                var b = (value & 0xFF0000) >> 16;
                pBytes[0] = (byte)(b * a / 255);
                pBytes[1] = (byte)(g * a / 255);
                pBytes[2] = (byte)(r * a / 255);
            }
        }

        private unsafe void ChannelSwapData () {
            var pBytes = (byte*)Data;
            var pEnd = pBytes + (Width * Height * ChannelCount);
            for (; pBytes < pEnd; pBytes += 4) {
                var r = pBytes[0];
                pBytes[0] = pBytes[2];
                pBytes[2] = r;
            }
        }

        public SurfaceFormat Format {
            get {
                if (IsFloatingPoint)
                    return SurfaceFormat.Vector4;
                else if (Is16Bit)
                    return SurfaceFormat.Rgba64;
                else
                    return SurfaceFormat.Color;
            }
        }

        public Texture2D CreateTexture (RenderCoordinator coordinator, bool padToPowerOfTwo = false) {
            if (IsDisposed)
                throw new ObjectDisposedException("Image is disposed");
            // FIXME: Channel count

            int width = padToPowerOfTwo ? Arithmetic.NextPowerOfTwo(Width) : Width;
            var height = padToPowerOfTwo ? Arithmetic.NextPowerOfTwo(Height) : Height;

            Texture2D result;
            lock (coordinator.CreateResourceLock)
                result = new Texture2D(coordinator.Device, width, height, MipChain != null, Format);

            // FIXME: FP mips, 16bit mips
            if ((MipChain != null) && !IsFloatingPoint && !Is16Bit)
                UploadWithMips(coordinator, result);
            else
                UploadDirect(coordinator, result);

            return result;
        }

        public int SizeofPixel { get; private set; }

        private Stopwatch UploadTimer = new Stopwatch();

        private void UploadDirect (RenderCoordinator coordinator, Texture2D result) {
            UploadTimer.Restart();
            lock (coordinator.UseResourceLock)
                Evil.TextureUtils.SetDataFast(result, 0, Data, Width, Height, (uint)(Width * SizeofPixel));
            if (UploadTimer.Elapsed.TotalMilliseconds > 1)
                Debug.Print($"Uploading non-mipped texture took {UploadTimer.Elapsed.TotalMilliseconds}ms");
        }

        private unsafe void GenerateMips () {
            void* pPreviousLevelData = null, pLevelData = Data;
            int levelWidth = Width, levelHeight = Height;
            int previousLevelWidth = Width, previousLevelHeight = Height;
            // FIXME
            MipChain = new byte[64][];

            var pin = default(GCHandle);
            for (uint level = 0; (levelWidth >= 1) && (levelHeight >= 1); level++) {
                if (level > 0) {
                    if (pin.IsAllocated)
                        pin.Free();
                    MipChain[level - 1] = new byte[levelWidth * levelHeight * SizeofPixel];
                    pin = GCHandle.Alloc(MipChain[level - 1], GCHandleType.Pinned);
                    pLevelData = (void*)pin.AddrOfPinnedObject();

                    MipGenerator.Color(pPreviousLevelData, previousLevelWidth, previousLevelHeight, pLevelData, levelWidth, levelHeight);
                }

                previousLevelWidth = levelWidth;
                previousLevelHeight = levelHeight;
                var newWidth = levelWidth / 2;
                var newHeight = levelHeight / 2;
                levelWidth = newWidth;
                levelHeight = newHeight;
                pPreviousLevelData = pLevelData;
            }
            if (pin.IsAllocated)
                pin.Free();
        }

        private unsafe void UploadWithMips (RenderCoordinator coordinator, Texture2D result) {
            var pPreviousLevelData = Data;
            var pLevelData = Data;
            int levelWidth = Width, levelHeight = Height;
            int previousLevelWidth = Width, previousLevelHeight = Height;

            if (MipChain == null)
                throw new Exception("Mip chain not generated or already uploaded");

            UploadTimer.Restart();

            var pin = default(GCHandle);
            for (uint level = 0; (levelWidth >= 1) && (levelHeight >= 1); level++) {
                uint mipSize;
                if (level > 0) {
                    if (pin.IsAllocated)
                        pin.Free();
                    var mip = MipChain[level - 1];
                    pin = GCHandle.Alloc(mip, GCHandleType.Pinned);
                    pLevelData = (void*)pin.AddrOfPinnedObject();
                    mipSize = (uint)mip.Length;
                } else {
                    mipSize = (uint)(Width * Height * SizeofPixel);
                }

                // FIXME: Create a work item for each mip to avoid blocking the main thread for too long
                lock (coordinator.UseResourceLock)
                    Evil.TextureUtils.SetDataFast(result, level, pLevelData, levelWidth, levelHeight, mipSize);

                previousLevelWidth = levelWidth;
                previousLevelHeight = levelHeight;
                var newWidth = levelWidth / 2;
                var newHeight = levelHeight / 2;
                levelWidth = newWidth;
                levelHeight = newHeight;
            }
            if (pin.IsAllocated)
                pin.Free();

            if (UploadTimer.Elapsed.TotalMilliseconds > 2)
                Debug.Print($"Uploading mipped texture took {UploadTimer.Elapsed.TotalMilliseconds}ms");
        }

        public void Dispose () {
            IsDisposed = true;
            MipChain = null;
            if (Data != null) {
                Native.API.stbi_image_free(Data);
                Data = null;
            }
        }
    }
}
