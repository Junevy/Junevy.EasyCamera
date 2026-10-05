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
        private readonly ConcurrentDictionary<string, CameraStreamSubscriber> subscribers = new();
        private readonly string cameraKey;
        private readonly object operationLock = new();
        private int disposed;

        public CameraStream(string cameraKey)
        {
            this.cameraKey = cameraKey ?? throw new ArgumentNullException(nameof(cameraKey));
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
            string subscriberKey,
            int capacity,
            Func<string, IFrame, Task> handler,
            Action<Exception> whenException = null)
        {
            if (string.IsNullOrEmpty(subscriberKey))
                throw new ArgumentException("The subscriber key is null or empty.", nameof(subscriberKey));

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

            // 先构造订阅者并启动 worker（实例经参数传入，无闭包时序依赖），再进入注册竞争
            var candidate = new CameraStreamSubscriber(subscriberKey, channel, cts);
            candidate.StartWorker(self => this.ConsumeAsync(subscriberKey, self, channel, cts, handler, whenException));

            CameraStreamSubscriber replaced = null;
            bool accepted;
            lock (this.operationLock)
            {
                if (this.disposed == 1)
                {
                    accepted = false;
                }
                else
                {
                    // 替换语义：同 Key 重新订阅时原子替换旧订阅者，旧订阅者在锁外清理
                    this.subscribers.TryRemove(subscriberKey, out replaced);
                    this.subscribers[subscriberKey] = candidate;
                    accepted = true;
                }
            }

            if (!accepted)
            {
                candidate.Dispose();
                throw new ObjectDisposedException(nameof(CameraStream));
            }

            replaced?.Dispose();
        }

        public void Publish(IFrame frame)
        {
            if (frame == null)
                return;

            CameraStreamSubscriber[] snapshot;
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

        public bool Unsubscribe(string subscriberKey)
        {
            if (string.IsNullOrEmpty(subscriberKey))
                return false;

            CameraStreamSubscriber subscriber;
            lock (this.operationLock)
            {
                if (!this.subscribers.TryRemove(subscriberKey, out subscriber))
                    return false;
            }

            subscriber.Dispose();
            return true;
        }

        public void Dispose()
        {
            CameraStreamSubscriber[] snapshot;
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
            string subscriberKey,
            CameraStreamSubscriber self,
            Channel<IFrame> channel,
            CancellationTokenSource cts,
            Func<string, IFrame, Task> handler,
            Action<Exception> whenException)
        {
            // 无异常回调时 handler 异常会终止 worker；终止后必须把订阅者从流中
            // 移除，否则 Publish 会继续向已死订阅者的通道写入帧，造成帧滞留泄漏
            // 且订阅静默失效。
            var terminated = false;
            try
            {
                while (await channel.Reader.WaitToReadAsync(cts.Token).ConfigureAwait(false))
                {
                    while (channel.Reader.TryRead(out var frame))
                    {
                        try
                        {
                            await handler(this.cameraKey, frame).ConfigureAwait(false);
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
                terminated = true;
                // 无异常回调时，handler 异常只终止当前 worker，不能再次调用 null 委托。
                NotifyException(whenException, ex);
            }
            finally
            {
                while (channel.Reader.TryRead(out var leftover))
                    DisposeFrame(leftover, whenException);

                if (terminated)
                    RemoveDeadSubscriber(subscriberKey, self);
            }
        }

        /// <summary>
        /// 订阅工作线程异常终止后移除该订阅者并释放其资源，
        /// 防止后续发布帧滞留在无人消费的通道中造成非托管内存泄漏。
        /// </summary>
        private void RemoveDeadSubscriber(string subscriberKey, CameraStreamSubscriber self)
        {
            lock (this.operationLock)
            {
                // 流已整体释放时，Dispose 路径已处理过该订阅者
                if (this.disposed == 1)
                    return;

                // 仅当字典中仍是该实例时才移除，避免误删同 Key 的重新订阅
                if (this.subscribers.TryGetValue(subscriberKey, out var current)
                    && ReferenceEquals(current, self))
                {
                    this.subscribers.TryRemove(subscriberKey, out _);
                }
            }

            // worker 已结束，Dispose 只会完成通道并释放 CTS
            self?.Dispose();
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
