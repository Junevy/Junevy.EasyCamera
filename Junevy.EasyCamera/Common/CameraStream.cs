using Junevy.EasyCamera.Core.Abstractions;
using Junevy.EasyCamera.Core.Common;
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
    /// <remarks>
    /// 锁纪律：<see cref="operationLock" /> 只保护订阅者注册表，绝不在锁内做帧级重活
    /// （引用计数、通道写入、背压淘汰、5MB 帧释放、用户异常回调）。
    /// 这样订阅/退订不会被慢速释放阻塞，发布线程也不会被订阅管理阻塞。
    /// </remarks>
    public class CameraStream : ICameraStream
    {
        private readonly ConcurrentDictionary<string, CameraStreamSubscriber> subscribers = new();
        private readonly string cameraKey;
        private readonly IStreamOptions options;
        private readonly object operationLock = new();
        private int disposed;

        private long published;
        private long delivered;
        private long dropped;

        /// <summary>
        /// 构造相机帧数据流
        /// </summary>
        /// <param name="cameraKey">相机Key，随帧传给订阅 handler</param>
        /// <param name="options">帧流配置（背压策略）；为 <c>null</c> 时使用默认配置</param>
        public CameraStream(string cameraKey, IStreamOptions options = null)
        {
            this.cameraKey = cameraKey ?? throw new ArgumentNullException(nameof(cameraKey));
            this.options = options ?? new StreamOptions();
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

        /// <inheritdoc />
        public FrameStreamStatistics Statistics => new (
            Interlocked.Read(ref this.published),
            Interlocked.Read(ref this.delivered),
            Interlocked.Read(ref this.dropped));

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

            // 背压策略在此一次性映射为通道行为：三种模式都不阻塞采集线程，区别只在丢哪一帧
            var channel = Channel.CreateBounded<IFrame>(
                new BoundedChannelOptions(capacity)
                {
                    FullMode = ToFullMode(this.options.BackpressureMode),
                    SingleReader = true,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false
                },
                // 背压淘汰的帧由流释放其引用；RejectNewest 模式下不会触发该回调
                frame => this.OnFrameDropped(frame, whenException));
            var cts = new CancellationTokenSource();

            // 先构造订阅者并启动 worker（实例经参数传入，无闭包时序依赖），再进入注册竞争
            var candidate = new CameraStreamSubscriber(channel, cts);
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

            Interlocked.Increment(ref this.published);

            CameraStreamSubscriber[] snapshot;
            lock (this.operationLock)
            {
                if (this.disposed == 1 || this.subscribers.Count == 0)
                {
                    snapshot = null;
                }
                else
                {
                    // 只在锁内取快照：AddRef/TryWrite 与帧释放在锁外执行，
                    // 订阅者在锁外被释放时 TryWrite 返回 false，引用照常归还，不会泄漏
                    snapshot = this.subscribers.Values.ToArray();
                }
            }

            if (snapshot == null || snapshot.Length == 0)
            {
                Interlocked.Increment(ref this.dropped);
                DisposeFrame(frame, null);
                return;
            }

            foreach (var subscriber in snapshot)
            {
                var addRefSucceeded = false;
                try
                {
                    frame.AddRef();
                    addRefSucceeded = true;

                    if (!subscriber.TryWrite(frame))
                    {
                        // 通道已关闭或按 RejectNewest 拒绝：归还刚取得的引用
                        Interlocked.Increment(ref this.dropped);
                        DisposeFrame(frame, null);
                    }
                }
                catch (Exception ex)
                {
                    if (addRefSucceeded)
                    {
                        Interlocked.Increment(ref this.dropped);
                        DisposeFrame(frame, null);
                    }

                    // 发布线程不应被订阅者的引用实现拖垮；异常只通过订阅者
                    // 自己的回调报告（若有），并继续处理其他订阅者。
                    _ = ex;
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

        /// <summary>
        /// 背压策略 → 通道满时的行为。
        /// <see cref="BackpressureMode.RejectNewest" /> 映射为 Wait：
        /// <c>TryWrite</c> 在队列满时直接返回 false（不阻塞、不覆盖、不触发淘汰回调），
        /// 由发布方计入丢帧统计。默认 DropOldest：淘汰最旧帧、保留最新画面。
        /// </summary>
        private static BoundedChannelFullMode ToFullMode(BackpressureMode mode)
        {
            return mode == BackpressureMode.RejectNewest
                ? BoundedChannelFullMode.Wait
                : BoundedChannelFullMode.DropOldest;
        }

        private void OnFrameDropped(IFrame frame, Action<Exception> whenException)
        {
            Interlocked.Increment(ref this.dropped);
            DisposeFrame(frame, whenException);
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
                            Interlocked.Increment(ref this.delivered);
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
            catch (ObjectDisposedException)
            {
                // CTS 在 worker 结束后才释放，理论上不可达；兜底避免异常终止路径丢帧
                terminated = true;
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
