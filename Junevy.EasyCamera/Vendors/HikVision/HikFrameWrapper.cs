using Junevy.EasyCamera.Core.Abstractions;
using MvCameraControl;
using System;
using System.Drawing;
using System.Threading;

namespace Junevy.EasyCamera.Vendors.HikVision
{
    /// <summary>
    /// 海康相机图像帧包装器
    /// </summary>
    public class HikFrameWrapper : IFrame
    {
        /// <summary>
        /// 海康相机图像帧（独立缓冲区的克隆帧）
        /// </summary>
        private readonly IFrameOut native;

        /// <summary>
        /// 引用计数，初始为发布方持有的1个引用
        /// </summary>
        private int refCount = 1;

        /// <summary>
        /// 是否已释放原生帧
        /// </summary>
        private int disposed;

        /// <summary>
        /// 构造海康相机图像帧包装器
        /// </summary>
        /// <param name="nativeFrame">海康相机图像帧（须为具备独立缓冲区的克隆帧）</param>
        /// <exception cref="ArgumentNullException">
        /// <c>nativeFrame</c> 为 <c>null</c>
        /// </exception>
        public HikFrameWrapper(IFrameOut nativeFrame)
        {
            this.native = nativeFrame ?? throw new ArgumentNullException(nameof(nativeFrame));
        }

        /// <summary>
        /// 图像像素数据指针，非托管内存。
        /// 注意：指针生命周期由具体实现管理，可能在相机缓存队列满时失效，
        /// 建议使用更安全的属性 <see cref="Data" />
        /// </summary>
        public IntPtr PixelDataPtr => this.native.Image.PixelDataPtr;

        /// <summary>
        /// 图像数据数组，托管内存
        /// </summary>
        public byte[] Data => this.native.Image.PixelData;

        /// <summary>
        /// 图像行步长，单位：字节
        /// </summary>
        public int Stride => this.Height > 0 ? (int)(this.ImageSize / this.Height) : 0;

        /// <summary>
        /// 图像宽度
        /// </summary>
        public uint Width => this.native.Image.Width;

        /// <summary>
        /// 图像高度
        /// </summary>
        public uint Height => this.native.Image.Height;

        /// <summary>
        /// 图像像素格式
        /// </summary>
        public ImagePixelFormat PixelType => ConvertFormat(this.native.Image.PixelType);

        /// <summary>
        /// 图像大小，单位：字节
        /// </summary>
        public ulong ImageSize => this.native.Image.ImageSize;

        /// <summary>
        /// 增加引用计数。已释放的帧不能重新取得引用，避免下游继续访问已释放的原生图像。
        /// </summary>
        public void AddRef()
        {
            while (true)
            {
                if (Volatile.Read(ref this.disposed) == 1)
                    throw new ObjectDisposedException(nameof(HikFrameWrapper));

                var current = Volatile.Read(ref this.refCount);
                if (current <= 0)
                    throw new ObjectDisposedException(nameof(HikFrameWrapper));

                if (Interlocked.CompareExchange(ref this.refCount, current + 1, current) == current)
                    return;
            }
        }

        /// <summary>
        /// 释放图像帧，引用计数归零时释放原生帧
        /// </summary>
        public void Dispose()
        {
            var remaining = Interlocked.Decrement(ref this.refCount);
            if (remaining > 0)
                return;

            // Dispose 应保持幂等；不要让重复释放把引用计数降到负数。
            if (remaining < 0)
            {
                Interlocked.Increment(ref this.refCount);
                return;
            }

            // 保证原生帧只被释放一次（防止引用计数异常时重复释放）
            if (Interlocked.Exchange(ref this.disposed, 1) == 1)
                return;

            this.native.Dispose();
        }

        /// <summary>
        /// 获取图像帧的Bitmap表示
        /// </summary>
        /// <returns>
        /// 图像帧的Bitmap表示；帧已释放时返回 <c>null</c>
        /// </returns>
        public Bitmap GetBitmap()
        {
            if (Volatile.Read(ref this.disposed) == 1)
                return null;

            return this.native.Image.ToBitmap();
        }

        /// <summary>
        /// 转换像素格式
        /// </summary>
        /// <param name="pixelType">海康像素格式</param>
        /// <returns>
        /// 统一的像素格式
        /// </returns>
        private static ImagePixelFormat ConvertFormat(MvGvspPixelType pixelType)
        {
            return pixelType switch
            {
                MvGvspPixelType.PixelType_Gvsp_Mono8 => ImagePixelFormat.Mono8,
                MvGvspPixelType.PixelType_Gvsp_Mono10 => ImagePixelFormat.Mono10,
                MvGvspPixelType.PixelType_Gvsp_Mono12 => ImagePixelFormat.Mono12,
                MvGvspPixelType.PixelType_Gvsp_Mono14 => ImagePixelFormat.Mono14,
                MvGvspPixelType.PixelType_Gvsp_Mono16 => ImagePixelFormat.Mono16,
                MvGvspPixelType.PixelType_Gvsp_Mono10_Packed => ImagePixelFormat.Mono10Packed,

                MvGvspPixelType.PixelType_Gvsp_BayerRG8 => ImagePixelFormat.BayerRG8,
                MvGvspPixelType.PixelType_Gvsp_BayerRG10 => ImagePixelFormat.BayerRG10,
                MvGvspPixelType.PixelType_Gvsp_BayerRG12 => ImagePixelFormat.BayerRG12,
                MvGvspPixelType.PixelType_Gvsp_BayerRG12_Packed => ImagePixelFormat.BayerRG12Packed,
                MvGvspPixelType.PixelType_Gvsp_BayerGB8 => ImagePixelFormat.BayerGB8,
                MvGvspPixelType.PixelType_Gvsp_BayerBG8 => ImagePixelFormat.BayerBG8,

                MvGvspPixelType.PixelType_Gvsp_RGB8_Packed => ImagePixelFormat.RGB8,
                MvGvspPixelType.PixelType_Gvsp_BGR8_Packed => ImagePixelFormat.BGR8,

                MvGvspPixelType.PixelType_Gvsp_YUV422_Packed => ImagePixelFormat.YUV422,
                MvGvspPixelType.PixelType_Gvsp_YUV444_Packed => ImagePixelFormat.YUV444,

                _ => ImagePixelFormat.Unknown
            };
        }
    }
}
