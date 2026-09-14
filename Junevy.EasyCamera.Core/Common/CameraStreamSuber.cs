using Junevy.EasyCamera.Core.Abstractions;
using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Junevy.EasyCamera.Core.Common
{
    /// <summary>
    /// 相机图像帧数据流订阅者，用于取消订阅（Dispose）或手动停止取流（Cancel）。
    /// </summary>
    public class CameraStreamSuber(Channel<IFrame> channel, CancellationTokenSource cts, Task worker)
    {
        public Channel<IFrame> Channel { get; private set; } = channel;

        public CancellationTokenSource Cts { get; private set; } = cts;

        public Task Subber { get; private set; } = worker;

        /// <summary>
        /// 取消订阅：完成通道写入、取消工作线程，并在工作线程结束后释放Cts
        /// </summary>
        public void Dispose()
        {
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

            // Cts 延迟到工作线程退出后再释放，避免工作线程仍在使用 Token 时
            // 因 Cts 被释放而抛出 ObjectDisposedException
            this.Subber.ContinueWith(
                _ => this.Cts.Dispose(),
                System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously);
        }
    }
}
