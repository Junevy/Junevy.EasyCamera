using Junevy.EasyCamera.Core.Abstractions;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Junevy.EasyCamera.Common
{
    /// <summary>
    /// 相机图像帧数据流类，用于发布和订阅相机图像帧数据。
    /// 每次成功发布都会把发布方的初始引用转移给流；流为每个成功入队的
    /// 订阅者增加一个引用，并在消费、淘汰或取消时恰好释放该引用。
    /// </summary>
    public class CameraStream : ICameraStream
    {
        private readonly ConcurrentDictionary<string, CameraStreamSuber> subscribers = new();
        private readonly string userDefinedName;
        private readonly object operationLock = new();
        private int disposed;

        public CameraStream(string userDefinedName)
        {
            this.userDefinedName = userDefinedName ?? throw new ArgumentNullException(nameof(userDefinedName));
        }

        /// <summary>
        /// 当前有效订阅数量。
        /// </summary>
        public int SubscriberCount
        {
            get
            {
                lock (this.operationLock)
                    return this.subscribers.Count;
            }
        }

        public void Subscribe(
            string subberKey,
            int capacity,
            Func<string, IFrame, Task> handler,
            Action<Exception> whenException = null)
        {
            if (string.IsNullOrEmpty(subberKey))
                throw new ArgumentException("The subscriber key is null or empty.", nameof(subberKey));

            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            if (capacity < 1)
                capacity = 1;

            lock (this.operationLock)
            {
                if (this.disposed == 1)
                    throw new ObjectDisposedException(nameof(CameraStream));
            }

            var channel = Channel.CreateBounded<IFrame>(
                new BoundedChannelOptions(capacity)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false
                },
                frame => DisposeFrame(frame, whenException));
            var cts = new CancellationTokenSource();
            var worker = Task.Run(() => ConsumeAsync(channel, cts, handler, whenException));
            var candidate = new CameraStreamSuber(channel, cts, worker);

            bool accepted;
            lock (this.operationLock)
            {
                if (this.disposed == 1)
                {
                    accepted = false;
                }
                else
                {
                    // TryAdd 在同一 key 的并发竞争中只允许一个候选成为有效订阅。
                    accepted = this.subscribers.TryAdd(subberKey, candidate);
                }
            }

            if (accepted)
                return;

            // 竞争失败或 Dispose 竞态产生的候选必须立即取消并释放自己的资源。
            candidate.Dispose();
            lock (this.operationLock)
            {
                if (this.disposed == 1)
                    throw new ObjectDisposedException(nameof(CameraStream));
            }
        }

        public void Publish(IFrame frame)
        {
            if (frame == null)
                return;

            CameraStreamSuber[] snapshot;
            lock (this.operationLock)
            {
                if (this.disposed == 1 || this.subscribers.Count == 0)
                {
                    DisposeFrame(frame, null);
                    return;
                }

                // 在锁内完成快照和 TryWrite，避免 Unsubscribe 在引用增加后提前 Dispose
                // 同一个 subscriber。TryWrite 始终是非阻塞操作。
                snapshot = this.subscribers.Values.ToArray();
                foreach (var subscriber in snapshot)
                {
                    var addRefSucceeded = false;
                    try
                    {
                        frame.AddRef();
                        addRefSucceeded = true;
                        if (!subscriber.TryWrite(frame))
                            DisposeFrame(frame, null);
                    }
                    catch (Exception ex)
                    {
                        if (addRefSucceeded)
                            DisposeFrame(frame, null);
                        // 发布线程不应被订阅者的引用实现拖垮；异常只通过订阅者
                        // 自己的回调报告（若有），并继续处理其他订阅者。
                        _ = ex;
                    }
                }
            }

            // 释放发布方持有的初始引用。
            DisposeFrame(frame, null);
        }

        public bool Unsubscribe(string subberKey)
        {
            if (string.IsNullOrEmpty(subberKey))
                return false;

            CameraStreamSuber subscriber;
            lock (this.operationLock)
            {
                if (!this.subscribers.TryRemove(subberKey, out subscriber))
                    return false;
            }

            subscriber.Dispose();
            return true;
        }

        public void Dispose()
        {
            CameraStreamSuber[] snapshot;
            lock (this.operationLock)
            {
                if (Interlocked.Exchange(ref this.disposed, 1) == 1)
                    return;

                snapshot = this.subscribers.Values.ToArray();
                this.subscribers.Clear();
            }

            foreach (var subscriber in snapshot)
                subscriber.Dispose();
        }

        private async Task ConsumeAsync(
            Channel<IFrame> channel,
            CancellationTokenSource cts,
            Func<string, IFrame, Task> handler,
            Action<Exception> whenException)
        {
            try
            {
                while (await channel.Reader.WaitToReadAsync(cts.Token).ConfigureAwait(false))
                {
                    while (channel.Reader.TryRead(out var frame))
                    {
                        try
                        {
                            await handler(this.userDefinedName, frame).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            if (whenException != null)
                                NotifyException(whenException, ex);
                            else
                                throw;
                        }
                        finally
                        {
                            DisposeFrame(frame, whenException);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Dispose/Unsubscribe 会取消 worker；finally 仍会排空并释放剩余帧。
            }
            catch (Exception ex)
            {
                // 无异常回调时，handler 异常只终止当前 worker，不能再次调用 null 委托。
                NotifyException(whenException, ex);
            }
            finally
            {
                while (channel.Reader.TryRead(out var leftover))
                    DisposeFrame(leftover, whenException);
            }
        }

        private static void DisposeFrame(IFrame frame, Action<Exception> whenException)
        {
            if (frame == null)
                return;

            try
            {
                frame.Dispose();
            }
            catch (Exception ex)
            {
                NotifyException(whenException, ex);
            }
        }

        private static void NotifyException(Action<Exception> whenException, Exception exception)
        {
            if (whenException == null)
                return;

            try
            {
                whenException(exception);
            }
            catch
            {
                // 异常通知回调不能让 worker 或发布线程再次失败。
            }
        }
    }
}
