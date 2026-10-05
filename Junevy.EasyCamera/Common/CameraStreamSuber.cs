using Junevy.EasyCamera.Core.Abstractions;
using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Junevy.EasyCamera.Common
{
    /// <summary>
    /// 相机图像帧数据流订阅者，用于封装通道、取消令牌和工作线程的生命周期。
    /// 工作线程由 <see cref="StartWorker" /> 在构造后立即启动，
    /// 实例引用以参数形式传入工厂，消除"闭包读取尚未赋值的局部变量"的时序依赖。
    /// </summary>
    public sealed class CameraStreamSuber
    {
        private int disposed;
        private int ctsDisposed;
        private Task worker;

        public CameraStreamSuber(string key, Channel<IFrame> channel, CancellationTokenSource cts)
        {
            this.Key = key ?? throw new ArgumentNullException(nameof(key));
            this.Channel = channel ?? throw new ArgumentNullException(nameof(channel));
            this.Cts = cts ?? throw new ArgumentNullException(nameof(cts));
        }

        /// <summary>
        /// 订阅者标识，与注册到流的 Key 一致
        /// </summary>
        public string Key { get; }

        public Channel<IFrame> Channel { get; }

        public CancellationTokenSource Cts { get; }

        /// <summary>
        /// 消费工作线程任务，由 <see cref="StartWorker" /> 启动后可用
        /// </summary>
        public Task Worker => this.worker;

        /// <summary>
        /// 启动消费工作线程。须在构造后立即调用恰好一次；
        /// 工厂以参数接收本实例，杜绝闭包时序依赖。
        /// </summary>
        public void StartWorker(Func<CameraStreamSuber, Task> workerFactory)
        {
            if (workerFactory == null)
                throw new ArgumentNullException(nameof(workerFactory));

            var task = Task.Run(() => workerFactory(this));
            Volatile.Write(ref this.worker, task);

            // 竞争失败路径可能已先 Dispose：此处兜底补挂 CTS 延迟释放
            if (Volatile.Read(ref this.disposed) == 1)
                this.AttachCtsCleanup();
        }

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

            if (Volatile.Read(ref this.worker) == null)
            {
                // StartWorker 尚未执行（仅竞争失败窗口可能）：其内部会兜底释放 CTS
                return;
            }

            this.AttachCtsCleanup();
        }

        /// <summary>
        /// CTS 延迟到工作线程退出后再释放，避免工作线程仍在使用 Token 时
        /// 因 CTS 被释放而抛出 ObjectDisposedException。幂等。
        /// </summary>
        private void AttachCtsCleanup()
        {
            if (Interlocked.Exchange(ref this.ctsDisposed, 1) == 1)
                return;

            var worker = Volatile.Read(ref this.worker);
            if (worker == null || worker.IsCompleted)
            {
                this.Cts.Dispose();
                return;
            }

            worker.ContinueWith(
                _ => this.Cts.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
