using Junevy.EasyCamera.Core.Abstractions;
using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Junevy.EasyCamera.Common
{
    /// <summary>
    /// 相机图像帧数据流订阅者，用于封装通道、取消令牌和工作线程的生命周期。
    /// </summary>
    public sealed class CameraStreamSuber
    {
        private int disposed;

        public CameraStreamSuber(string key, Channel<IFrame> channel, CancellationTokenSource cts, Task worker)
        {
            this.Key = key ?? throw new ArgumentNullException(nameof(key));
            this.Channel = channel ?? throw new ArgumentNullException(nameof(channel));
            this.Cts = cts ?? throw new ArgumentNullException(nameof(cts));
            this.Subber = worker ?? throw new ArgumentNullException(nameof(worker));
        }

        /// <summary>
        /// 订阅者标识，与注册到流的 Key 一致
        /// </summary>
        public string Key { get; }

        public Channel<IFrame> Channel { get; }

        public CancellationTokenSource Cts { get; }

        public Task Subber { get; }

        /// <summary>
        /// 非阻塞地写入一帧。通道已完成或订阅已释放时返回 false。
        /// </summary>
        public bool TryWrite(IFrame frame)
        {
            if (Volatile.Read(ref this.disposed) == 1)
                return false;

            try
            {
                return this.Channel.Writer.TryWrite(frame);
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        /// <summary>
        /// 取消订阅：完成通道写入、取消工作线程，并在工作线程结束后释放 CTS。
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref this.disposed, 1) == 1)
                return;

            try
            {
                this.Channel.Writer.TryComplete();
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                this.Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            if (this.Subber.IsCompleted)
            {
                this.Cts.Dispose();
                return;
            }

            // CTS 延迟到工作线程退出后再释放，避免工作线程仍在使用 Token 时
            // 因 CTS 被释放而抛出 ObjectDisposedException。
            this.Subber.ContinueWith(
                _ => this.Cts.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
