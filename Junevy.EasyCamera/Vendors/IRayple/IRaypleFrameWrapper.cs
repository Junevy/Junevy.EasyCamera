using Junevy.EasyCamera.Core.Abstractions;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using static MVSDK_Net.IMVDefine;

namespace Junevy.EasyCamera.Vendors.IRayple
{
    /// <summary>
    /// Irayple工业相机图像帧包装器。
    /// 构造时即将像素数据复制为托管内存，因此原生帧归还SDK后本对象依然安全可用。
    /// </summary>
    [Obsolete("未开发完毕", true)]
    public class IRaypleFrameWrapper : IFrame
    {
        /// <summary>
        /// Irayple原生图像帧
        /// </summary>
        private readonly IMV_Frame native;

        /// <summary>
        /// 引用计数，初始为发布方持有的1个引用
        /// </summary>
        private int refCount = 1;

        /// <summary>
        /// 是否已释放
        /// </summary>
        private int disposed;

        /// <summary>
        /// 引用计数保护锁：串行化 AddRef 与最后一次 Dispose
        /// </summary>
        private readonly object refLock = new();

        /// <summary>
        /// 图像数据数组，托管内存
        /// </summary>
        public byte[] Data { get; }

        /// <summary>
        /// 构造Irayple工业相机图像帧包装器
        /// </summary>
        /// <param name="nativeFrame">Irayple原生图像帧</param>
        public IRaypleFrameWrapper(IMV_Frame nativeFrame)
        {
            this.native = nativeFrame;
            this.Data = CopyImageData(nativeFrame);
        }

        /// <summary>
        /// 图像像素数据指针，非托管内存。
        /// 注意：该指针在原生帧归还SDK后可能失效，
        /// 建议使用更安全的属性 <see cref="Data" />
        /// </summary>
        public IntPtr PixelDataPtr => this.native.pData;

        /// <summary>
        /// 图像行步长，单位：字节
        /// </summary>
        public int Stride => this.Height > 0 ? (int)(this.ImageSize / this.Height) : 0;

        /// <summary>
        /// 图像宽度
        /// </summary>
        public uint Width => this.native.frameInfo.width;

        /// <summary>
        /// 图像高度
        /// </summary>
        public uint Height => this.native.frameInfo.height;

        /// <summary>
        /// 图像像素格式
        /// </summary>
        public ImagePixelFormat PixelType => ConvertFormat(this.native.frameInfo.pixelFormat);

        /// <summary>
        /// 图像大小，单位：字节
        /// </summary>
        public ulong ImageSize => this.native.frameInfo.size;

        /// <summary>
        /// 增加引用计数。已释放的帧不能重新取得引用，避免下游继续使用已终结的帧对象。
        /// 注意：AddRef 应在持有帧的回调（如订阅 handler）返回前完成引用转移。
        /// </summary>
        public void AddRef()
        {
            lock (this.refLock)
            {
                if (Volatile.Read(ref this.disposed) == 1 || this.refCount <= 0)
                    throw new ObjectDisposedException(nameof(IRaypleFrameWrapper));

                this.refCount++;
            }
        }

        /// <summary>
        /// 释放图像帧。
        /// 像素数据为托管副本，原生帧已在采集回调中通过 IMV_ReleaseFrame 归还SDK，此处无需释放非托管内存
        /// </summary>
        public void Dispose()
        {
            lock (this.refLock)
            {
                this.refCount--;
                if (this.refCount > 0)
                    return;

                // Dispose 保持幂等：重复释放不把引用计数压到负数
                if (this.refCount < 0)
                {
                    this.refCount++;
                    return;
                }

                Interlocked.Exchange(ref this.disposed, 1);
            }
        }

        /// <summary>
        /// 获取图像帧的Bitmap表示。
        /// 仅支持 Mono8 与 24bpp 打包格式（RGB8/BGR8）；其他格式返回 <c>null</c>，由上层自行转换
        /// </summary>
        /// <returns>
        /// 图像帧的Bitmap表示；格式不支持或帧已释放时返回 <c>null</c>
        /// </returns>
        public Bitmap GetBitmap()
        {
            var data = this.Data;

            if (data == null || Volatile.Read(ref this.disposed) == 1)
                return null;

            var width = (int)this.Width;
            var height = (int)this.Height;

            if (width <= 0 || height <= 0)
                return null;

            switch (this.PixelType)
            {
                case ImagePixelFormat.RGB8:
                    return CreatePacked24bppBitmap(data, width, height, swapRedBlue: true);

                case ImagePixelFormat.BGR8:
                    return CreatePacked24bppBitmap(data, width, height, swapRedBlue: false);

                case ImagePixelFormat.Mono8:
                    return CreateMono8Bitmap(data, width, height);

                default:
                    return null;
            }
        }

        /// <summary>
        /// 复制原生帧的像素数据到托管数组
        /// </summary>
        /// <param name="frame">Irayple原生图像帧</param>
        /// <returns>
        /// 像素数据副本；数据不可用返回 <c>null</c>
        /// </returns>
        private static byte[] CopyImageData(IMV_Frame frame)
        {
            var size = (int)frame.frameInfo.size;

            if (frame.pData == IntPtr.Zero || size <= 0)
                return null;

            var data = new byte[size];
            Marshal.Copy(frame.pData, data, 0, size);
            return data;
        }

        /// <summary>
        /// 由紧凑排列的24bpp数据创建Bitmap
        /// </summary>
        /// <param name="data">像素数据</param>
        /// <param name="width">图像宽度</param>
        /// <param name="height">图像高度</param>
        /// <param name="swapRedBlue">是否需要交换红蓝通道（RGB8 转 GDI+ 的 BGR 内存序）</param>
        /// <returns>
        /// Bitmap；数据长度不足时返回 <c>null</c>
        /// </returns>
        private static Bitmap CreatePacked24bppBitmap(byte[] data, int width, int height, bool swapRedBlue)
        {
            var sourceStride = width * 3;

            if (data.Length < sourceStride * height)
                return null;

            var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            var rect = new Rectangle(0, 0, width, height);
            var bitmapData = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);

            try
            {
                var row = new byte[Math.Abs(bitmapData.Stride)];

                for (var y = 0; y < height; y++)
                {
                    var sourceOffset = y * sourceStride;

                    for (var x = 0; x < width; x++)
                    {
                        var source = sourceOffset + x * 3;
                        var target = x * 3;

                        // GDI+ 的24bppRgb在内存中的通道顺序为 B、G、R
                        row[target] = swapRedBlue ? data[source + 2] : data[source];
                        row[target + 1] = data[source + 1];
                        row[target + 2] = swapRedBlue ? data[source] : data[source + 2];
                    }

                    Marshal.Copy(row, 0, IntPtr.Add(bitmapData.Scan0, y * bitmapData.Stride), row.Length);
                }
            }
            finally
            {
                bitmap.UnlockBits(bitmapData);
            }

            return bitmap;
        }

        /// <summary>
        /// 由8位灰度数据创建Bitmap
        /// </summary>
        /// <param name="data">像素数据</param>
        /// <param name="width">图像宽度</param>
        /// <param name="height">图像高度</param>
        /// <returns>
        /// Bitmap；数据长度不足时返回 <c>null</c>
        /// </returns>
        private static Bitmap CreateMono8Bitmap(byte[] data, int width, int height)
        {
            if (data.Length < width * height)
                return null;

            var bitmap = new Bitmap(width, height, PixelFormat.Format8bppIndexed);

            var palette = bitmap.Palette;
            for (var i = 0; i < palette.Entries.Length; i++)
                palette.Entries[i] = Color.FromArgb(255, i, i, i);
            bitmap.Palette = palette;

            var rect = new Rectangle(0, 0, width, height);
            var bitmapData = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format8bppIndexed);

            try
            {
                var row = new byte[Math.Abs(bitmapData.Stride)];

                for (var y = 0; y < height; y++)
                {
                    Buffer.BlockCopy(data, y * width, row, 0, width);
                    Marshal.Copy(row, 0, IntPtr.Add(bitmapData.Scan0, y * bitmapData.Stride), row.Length);
                }
            }
            finally
            {
                bitmap.UnlockBits(bitmapData);
            }

            return bitmap;
        }

        /// <summary>
        /// 转换像素格式
        /// </summary>
        /// <param name="pixelType">Irayple像素格式</param>
        /// <returns>
        /// 统一的像素格式
        /// </returns>
        private static ImagePixelFormat ConvertFormat(IMV_EPixelType pixelType)
        {
            return pixelType switch
            {
                IMV_EPixelType.gvspPixelMono8 => ImagePixelFormat.Mono8,
                IMV_EPixelType.gvspPixelMono10 => ImagePixelFormat.Mono10,
                IMV_EPixelType.gvspPixelMono12 => ImagePixelFormat.Mono12,
                IMV_EPixelType.gvspPixelMono14 => ImagePixelFormat.Mono14,
                IMV_EPixelType.gvspPixelMono16 => ImagePixelFormat.Mono16,
                IMV_EPixelType.gvspPixelMono10Packed => ImagePixelFormat.Mono10Packed,

                IMV_EPixelType.gvspPixelRGB8 => ImagePixelFormat.RGB8,
                IMV_EPixelType.gvspPixelBGR8 => ImagePixelFormat.BGR8,

                IMV_EPixelType.gvspPixelBayRG8 => ImagePixelFormat.BayerRG8,
                IMV_EPixelType.gvspPixelBayRG10 => ImagePixelFormat.BayerRG10,
                IMV_EPixelType.gvspPixelBayRG12 => ImagePixelFormat.BayerRG12,
                IMV_EPixelType.gvspPixelBayRG12Packed => ImagePixelFormat.BayerRG12Packed,
                IMV_EPixelType.gvspPixelBayGB8 => ImagePixelFormat.BayerGB8,
                IMV_EPixelType.gvspPixelBayBG8 => ImagePixelFormat.BayerBG8,

                _ => ImagePixelFormat.Unknown
            };
        }
    }
}
