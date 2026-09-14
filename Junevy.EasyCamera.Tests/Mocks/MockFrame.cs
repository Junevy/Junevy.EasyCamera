using Junevy.EasyCamera.Core.Abstractions;
using System;
using System.Drawing;
using System.Threading;

namespace Junevy.EasyCamera.Tests.Mocks
{
    /// <summary>
    /// 可追踪引用计数与释放状态的模拟图像帧
    /// </summary>
    public class MockFrame : IFrame
    {
        private int refCount = 1;

        /// <summary>
        /// 引用计数是否已归零（即原生资源是否已释放）
        /// </summary>
        public bool IsDisposed { get; private set; }

        public IntPtr PixelDataPtr => IntPtr.Zero;

        public byte[] Data { get; } = new byte[4];

        public int Stride => 2;

        public uint Width => 2;

        public uint Height => 1;

        public ImagePixelFormat PixelType => ImagePixelFormat.Mono8;

        public ulong ImageSize => 2;

        public void AddRef() => Interlocked.Increment(ref this.refCount);

        public Bitmap GetBitmap() => null;

        public void Dispose()
        {
            if (Interlocked.Decrement(ref this.refCount) > 0)
                return;

            this.IsDisposed = true;
        }
    }
}
