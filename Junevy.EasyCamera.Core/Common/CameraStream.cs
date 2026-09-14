using Junevy.EasyCamera.Core.Abstractions;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Junevy.EasyCamera.Core.Common
{
    /// <summary>
    /// 相机图像帧数据流类，用于发布和订阅相机图像帧数据。
    /// </summary>
    public class CameraStream : ICameraStream
    {
        /// <summary>
        /// Key: 订阅者自定义的订阅名称
        /// Value：订阅者
        /// </summary>
        private readonly ConcurrentDictionary<string, CameraStreamSuber> subscribers = new();

        private readonly string userDefinedName;

        // 订阅数量
        public int SubscriberCount => subscribers.Count;


        public CameraStream(string userDefinedName)
        {
            this.userDefinedName = userDefinedName;
        }


        public void Subscribe(string subberKey, int capacity, Func<string, IFrame, Task> handler, Action<Exception> whenException = null)
        {
            if (string.IsNullOrEmpty(subberKey))
                throw new ArgumentException("The subscriber key is null or empty.", nameof(subberKey));

            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            if (capacity < 1)
                capacity = 1;

            // DropOldest 丢帧时通过 itemDropped 回调释放帧，防止非托管内存泄漏
            var channel = Channel.CreateBounded<IFrame>(
                new BoundedChannelOptions(capacity)
                {
                    FullMode = BoundedChannelFullMode.DropOldest
                },
                frame => frame.Dispose());

            if (subscribers.TryRemove(subberKey, out var oldSuber))
                oldSuber.Dispose();

            var cts = new CancellationTokenSource();

            var worker = Task.Run(async () =>
            {
                try
                {
                    while (await channel.Reader.WaitToReadAsync(cts.Token))
                    {
                        while (channel.Reader.TryRead(out var frame))
                        {
                            try
                            {
                                await handler(this.userDefinedName, frame);
                            }
                            catch (Exception ex)
                            {
                                // 提供异常回调时交给回调处理；否则重新抛出，终止订阅工作线程
                                if (whenException != null)
                                    whenException(ex);
                                else throw;
                            }
                            finally
                            {
                                frame.Dispose();
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch
                {
                    // 未提供异常回调时处理函数抛出异常，订阅工作线程终止
                }
                finally
                {
                    // 排空通道中剩余帧，保证缓冲帧全部释放
                    while (channel.Reader.TryRead(out var leftover))
                        leftover.Dispose();
                }
            });

            subscribers[subberKey] = new CameraStreamSuber(channel, cts, worker);
        }

        public void Publish(IFrame frame)
        {
            if (frame == null) return;

            if (subscribers.Count <= 0)
            {
                // 无订阅者时直接释放发布方的初始引用，避免非托管帧泄漏
                frame.Dispose();
                return;
            }

            foreach (var sub in subscribers.Values)
            {
                frame.AddRef();

                if (!sub.Channel.Writer.TryWrite(frame))
                    frame.Dispose();
            }

            // 释放发布方持有的初始引用，各订阅者处理完后各自释放
            frame.Dispose();
        }

        public bool Unsubscribe(string subberKey)
        {
            if (string.IsNullOrEmpty(subberKey))
                return false;

            if (subscribers.TryRemove(subberKey, out var suber))
            {
                suber.Dispose();
                return true;
            }
            return false;
        }

        public void Dispose()
        {
            foreach (var sub in subscribers.Values)
                sub.Dispose();

            subscribers.Clear();
        }
    }
}
