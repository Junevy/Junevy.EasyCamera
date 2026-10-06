using Junevy.EasyCamera.Core.Abstractions;
using MvCameraControl;
using System;
using System.Drawing;
using System.Threading;

namespace Junevy.EasyCamera.Vendors.HikVision
{
    /// <summary>
    /// 海康相机图像帧包装器。
    /// </summary>
    /// <remarks>
    /// 并发纪律：图像的固有元数据（宽高、步长、像素格式、大小）在构造时一次性从原生帧快照，
    /// 之后全部是字段读取——既消除每帧重复穿透 SDK，也消除了"先判已释放再读原生内存"的竞态。
    /// 仍需触碰原生内存的只有 <see cref="Data" />、<see cref="PixelDataPtr" /> 与
    /// <see cref="GetBitmap" />，三者都在 <see cref="refLock" /> 内执行，与引用计数归零物理释放互斥。
    /// </remarks>
    public class HikFrameWrapper : IFrame
    {
        /// <summary>
        /// 海康相机图像帧（独立缓冲区的克隆帧）
        /// </summary>
        private readonly IFrameOut native;

        /// <summary>
        /// 图像宽度（构造时快照）
        /// </summary>
        private readonly uint width;

        /// <summary>
        /// 图像高度（构造时快照）
        /// </summary>
        private readonly uint height;

        /// <summary>
        /// 图像大小，单位：字节（构造时快照）
        /// </summary>
        private readonly ulong imageSize;

        /// <summary>
        /// 图像行步长，单位：字节（构造时快照）
        /// </summary>
        private readonly int stride;

        /// <summary>
        /// 图像像素格式（构造时快照）
        /// </summary>
        private readonly ImagePixelFormat pixelType;

        /// <summary>
        /// 引用计数，初始为发布方持有的1个引用
        /// </summary>
        private int refCount = 1;

        /// <summary>
        /// 引用计数保护锁，串行化 AddRef 与最后一次 Dispose 的竞态，
        /// 防止引用计数为1时 AddRef 与释放并发导致 use-after-free；
        /// 同时保护 <see cref="pixelData" /> 的填充与原生内存读取
        /// </summary>
        private readonly object refLock = new();

        /// <summary>
        /// 是否已释放原生帧
        /// </summary>
        private int disposed;

        /// <summary>
        /// 托管像素副本缓存：SDK 的 PixelData 可能每次访问重新拷贝，
        /// 缓存为一次性成本；释放后托管副本仍可安全读取（原生缓冲不受影响）
        /// </summary>
        private byte[] pixelData;

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

            // 固有元数据一次性快照：此后不再触碰原生帧的这些属性
            var image = nativeFrame.Image;
            this.width = image.Width;
            this.height = image.Height;
            this.imageSize = image.ImageSize;
            this.pixelType = ConvertFormat(image.PixelType);
            this.stride = this.height > 0 ? (int)(this.imageSize / this.height) : 0;
        }

        /// <summary>
        /// 图像像素数据指针，非托管内存。
        /// 注意：指针生命周期由具体实现管理，可能在相机缓存队列满时失效，
        /// 建议使用更安全的属性 <see cref="Data" />
        /// </summary>
        public IntPtr PixelDataPtr
        {
            get
            {
                lock (this.refLock)
                    return Volatile.Read(ref this.disposed) == 1 ? IntPtr.Zero : this.native.Image.PixelDataPtr;
            }
        }

        /// <summary>
        /// 图像数据数组，托管内存（首次访问时缓存；
        /// 帧已释放且从未访问过时返回空数组）
        /// </summary>
        public byte[] Data
        {
            get
            {
                var cached = Volatile.Read(ref this.pixelData);
                if (cached != null)
                    return cached;

                lock (this.refLock)
                {
                    // 与最后一次 Dispose 互斥：不会出现"判未释放 → 读已释放原生内存"
                    if (this.pixelData != null)
                        return this.pixelData;

                    if (Volatile.Read(ref this.disposed) == 1)
                        return Array.Empty<byte>();

                    this.pixelData = this.native.Image.PixelData;
                    return this.pixelData;
                }
            }
        }

        /// <summary>
        /// 图像行步长，单位：字节
        /// </summary>
        public int Stride => this.stride;

        /// <summary>
        /// 图像宽度
        /// </summary>
        public uint Width => this.width;

        /// <summary>
        /// 图像高度
        /// </summary>
        public uint Height => this.height;

        /// <summary>
        /// 图像像素格式
        /// </summary>
        public ImagePixelFormat PixelType => this.pixelType;

        /// <summary>
        /// 图像大小，单位：字节
        /// </summary>
        public ulong ImageSize => this.imageSize;

        /// <summary>
        /// 增加引用计数。已释放的帧不能重新取得引用，避免下游继续访问已释放的原生图像。
        /// 注意：AddRef 应在持有帧的回调（如订阅 handler）返回前完成引用转移，
        /// 否则可能与最后一次 Dispose 并发导致访问已释放的非托管内存。
        /// </summary>
        public void AddRef()
        {
            lock (this.refLock)
            {
                if (this.disposed == 1 || this.refCount <= 0)
                    throw new ObjectDisposedException(nameof(HikFrameWrapper));

                this.refCount++;
            }
        }

        /// <summary>
        /// 释放图像帧，引用计数归零时释放原生帧
        /// </summary>
        public void Dispose()
        {
            lock (this.refLock)
            {
                this.refCount--;
                if (this.refCount > 0) return;

                // Dispose 应保持幂等；不要让重复释放把引用计数降到负数。
                if (this.refCount < 0)
                {
                    this.refCount++;
                    return;
                }

                // 保证原生帧只被释放一次（防止引用计数异常时重复释放）
                if (Interlocked.Exchange(ref this.disposed, 1) == 1) return;
            }

            this.native.Dispose();

            // 原生帧释放成功后撤销终结器；若释放抛出异常，终结器保留作为兜底重试
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// 终结器兜底：引用方忘记 Dispose 时，由 GC 释放克隆帧的非托管缓冲，
        /// 避免内存永久泄漏。正常路径已在 Dispose 中释放并撤销终结器。
        /// </summary>
        ~HikFrameWrapper()
        {
            try
            {
                this.native?.Dispose();
            }
            catch
            {
                // 终结器线程内不得抛出异常
            }
        }

        /// <summary>
        /// 获取图像帧的Bitmap表示
        /// </summary>
        /// <returns>
        /// 图像帧的Bitmap表示；帧已释放时返回 <c>null</c>
        /// </returns>
        public Bitmap GetBitmap()
        {
            lock (this.refLock)
            {
                if (Volatile.Read(ref this.disposed) == 1)
                    return null;

                return this.native.Image.ToBitmap();
            }
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
