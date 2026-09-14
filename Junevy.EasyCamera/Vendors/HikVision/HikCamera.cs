using Junevy.EasyCamera.Core.Abstractions;
using MvCameraControl;
using System;
using System.Threading;

namespace Junevy.EasyCamera.Vendors.HikVision
{
    /// <summary>
    /// 海康工业相机
    /// </summary>
    public class HikCamera : ICamera
    {
        /// <summary>
        /// 海康原生设备信息
        /// </summary>
        private readonly IDeviceInfo deviceInfo;

        /// <summary>
        /// 相机帧数据流，由调用方创建并管理生命周期
        /// </summary>
        private readonly ICameraStream stream;

        /// <summary>
        /// 海康相机设备实例
        /// </summary>
        private IDevice device;

        /// <summary>
        /// 相机内部图像缓冲区数量，0表示使用SDK默认值
        /// </summary>
        private int bufferCount;

        /// <summary>
        /// 是否已打开
        /// </summary>
        private int isOpen;

        /// <summary>
        /// 是否正在取流
        /// </summary>
        private int isGrabbing;

        /// <summary>
        /// 是否已释放
        /// </summary>
        private int disposed;

        /// <summary>
        /// 是否已打开
        /// </summary>
        public bool IsOpen => Volatile.Read(ref this.isOpen) == 1 && this.device != null && this.device.IsConnected;

        /// <summary>
        /// 是否正在取流
        /// </summary>
        public bool IsGrabbing => Volatile.Read(ref this.isGrabbing) == 1;

        /// <summary>
        /// 构造海康工业相机
        /// </summary>
        /// <param name="info">海康原生设备信息</param>
        /// <param name="stream">相机帧数据流</param>
        /// <exception cref="ArgumentNullException">
        /// <c>info</c> 或 <c>stream</c> 为 <c>null</c>
        /// </exception>
        public HikCamera(IDeviceInfo info, ICameraStream stream)
        {
            this.deviceInfo = info ?? throw new ArgumentNullException(nameof(info));
            this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
        }

        /// <summary>
        /// 打开相机
        /// </summary>
        /// <returns>
        /// 相机操作结果
        /// </returns>
        public CameraResult Open()
        {
            if (Volatile.Read(ref this.disposed) == 1)
                return CameraResult.Fail(-1, "The camera has been disposed");

            if (this.IsOpen)
                return CameraResult.Fail(-1, "The camera has been opened");

            try
            {
                if (this.device == null)
                {
                    this.device = DeviceFactory.CreateDevice(this.deviceInfo);
                }

                var result = this.device.Open();

                if (result != MvError.MV_OK)
                    return CameraResult.Fail(result, "Open camera failed");

                // 先解绑再绑定，避免重复打开时回调被注册多次
                this.device.StreamGrabber.FrameGrabedEvent -= this.ProcessFrameCallBack;
                this.device.StreamGrabber.FrameGrabedEvent += this.ProcessFrameCallBack;

                // 设备打开成功即置位，避免后续可选配置失败时状态位与设备真实状态不一致
                Interlocked.Exchange(ref this.isOpen, 1);

                // 延迟应用缓冲区配置：必须在设备打开之后、开始取流之前。
                // 该配置为可选优化项，失败不影响相机可用性
                if (this.bufferCount > 0)
                    this.TryApplyBufferCount();

                return CameraResult.Success(result);
            }
            catch (MvException me)
            {
                return CameraResult.Fail(me.ErrorCode, me.Message);
            }
            catch (Exception e)
            {
                return CameraResult.Fail(-1, e.Message);
            }
        }

        /// <summary>
        /// 关闭相机
        /// </summary>
        /// <returns>
        /// 相机操作结果
        /// </returns>
        public CameraResult Close()
        {
            if (this.device == null)
                return CameraResult.Fail(-1, "Camera not initialized");

            this.StopGrab();

            try
            {
                this.device.StreamGrabber.FrameGrabedEvent -= this.ProcessFrameCallBack;

                var result = this.device.Close();
                Interlocked.Exchange(ref this.isOpen, 0);

                return CameraResult.Result(result == MvError.MV_OK, result);
            }
            catch (MvException me)
            {
                return CameraResult.Fail(me.ErrorCode, me.Message);
            }
            catch (Exception e)
            {
                return CameraResult.Fail(-1, e.Message);
            }
        }

        /// <summary>
        /// 开始取流
        /// </summary>
        /// <returns>
        /// 相机操作结果
        /// </returns>
        public CameraResult Grab()
        {
            if (!this.IsOpen)
                return CameraResult.Fail(-1, "Camera is not open");

            // 由取流状态位保证并发调用只有一个线程能真正启动取流
            if (Interlocked.CompareExchange(ref this.isGrabbing, 1, 0) == 1)
                return CameraResult.Fail(-1, "Camera is already grabbing");

            try
            {
                var result = this.device.StreamGrabber.StartGrabbing();

                if (result != MvError.MV_OK)
                {
                    Interlocked.Exchange(ref this.isGrabbing, 0);
                    return CameraResult.Fail(result, "Start grabbing failed");
                }

                return CameraResult.Result(true, result);
            }
            catch (MvException me)
            {
                Interlocked.Exchange(ref this.isGrabbing, 0);
                return CameraResult.Fail(me.ErrorCode, me.Message);
            }
            catch (Exception e)
            {
                Interlocked.Exchange(ref this.isGrabbing, 0);
                return CameraResult.Fail(-1, e.Message);
            }
        }

        /// <summary>
        /// 停止取流（幂等，可重复调用）
        /// </summary>
        public void StopGrab()
        {
            // 状态位由1翻转为0的线程负责真正停止取流
            if (Interlocked.CompareExchange(ref this.isGrabbing, 0, 1) != 1)
                return;

            try
            {
                this.device?.StreamGrabber?.StopGrabbing();
            }
            catch
            {
                // 停止取流属于清理动作，失败不向上抛出，避免影响调用方的释放流程
            }
        }

        /// <summary>
        /// 设置相机内部图像缓冲区数量（帧数）。
        /// 相机已打开时立即生效，否则延迟到 <see cref="Open" /> 成功后应用
        /// </summary>
        /// <param name="count">缓冲区数量，须大于0</param>
        /// <returns>
        /// 相机操作结果
        /// </returns>
        public CameraResult SetBufferCount(int count)
        {
            if (count < 1)
                return CameraResult.Fail(-1, "The buffer count must be greater than zero");

            this.bufferCount = count;

            if (this.device == null || !this.IsOpen)
                return CameraResult.Success(0);

            try
            {
                var result = this.device.StreamGrabber.SetImageNodeNum((uint)count);
                return CameraResult.Result(result == MvError.MV_OK, result);
            }
            catch (MvException me)
            {
                return CameraResult.Fail(me.ErrorCode, me.Message);
            }
            catch (Exception e)
            {
                return CameraResult.Fail(-1, e.Message);
            }
        }

        /// <summary>
        /// 尝试应用相机内部图像缓冲区配置。
        /// 该配置属于可选优化项，失败时不影响相机可用性，故不向上抛出异常
        /// </summary>
        private void TryApplyBufferCount()
        {
            try
            {
                this.device?.StreamGrabber?.SetImageNodeNum((uint)this.bufferCount);
            }
            catch
            {
                // 可选配置失败不影响相机可用性
            }
        }

        /// <summary>
        /// 获取相机序列号
        /// </summary>
        /// <returns>
        /// 相机序列号，未知时返回空字符串
        /// </returns>
        public string GetSerialNumber() => this.deviceInfo?.SerialNumber ?? string.Empty;

        /// <summary>
        /// 设置整型参数
        /// </summary>
        /// <param name="paramName">参数名</param>
        /// <param name="value">参数值</param>
        /// <returns>
        /// 相机操作结果
        /// </returns>
        public CameraResult SetParam(string paramName, int value)
        {
            var state = this.CheckReady(paramName);
            if (!state.IsSuccess)
                return state;

            return ToResult(this.device.Parameters.SetIntValue(paramName, value), "Check the paramName!");
        }

        /// <summary>
        /// 设置浮点型参数
        /// </summary>
        /// <param name="paramName">参数名</param>
        /// <param name="value">参数值</param>
        /// <returns>
        /// 相机操作结果
        /// </returns>
        public CameraResult SetParam(string paramName, float value)
        {
            var state = this.CheckReady(paramName);
            if (!state.IsSuccess)
                return state;

            return ToResult(this.device.Parameters.SetFloatValue(paramName, value), "Check the paramName!");
        }

        /// <summary>
        /// 设置布尔型参数
        /// </summary>
        /// <param name="paramName">参数名</param>
        /// <param name="value">参数值</param>
        /// <returns>
        /// 相机操作结果
        /// </returns>
        public CameraResult SetParam(string paramName, bool value)
        {
            var state = this.CheckReady(paramName);
            if (!state.IsSuccess)
                return state;

            return ToResult(this.device.Parameters.SetBoolValue(paramName, value), "Check the paramName!");
        }

        /// <summary>
        /// 设置字符串参数
        /// </summary>
        /// <param name="paramName">参数名</param>
        /// <param name="value">参数值</param>
        /// <returns>
        /// 相机操作结果
        /// </returns>
        public CameraResult SetParam(string paramName, string value)
        {
            var state = this.CheckReady(paramName);
            if (!state.IsSuccess)
                return state;

            return ToResult(this.device.Parameters.SetStringValue(paramName, value), "Check the paramName!");
        }

        /// <summary>
        /// 设置枚举参数
        /// </summary>
        /// <param name="paramName">参数名</param>
        /// <param name="value">枚举符号名</param>
        /// <returns>
        /// 相机操作结果
        /// </returns>
        public CameraResult SetEnumParam(string paramName, string value)
        {
            if (!this.IsOpen)
                return CameraResult.Fail(-1, "Camera is not open");

            if (string.IsNullOrEmpty(paramName) || string.IsNullOrEmpty(value))
                return CameraResult.Fail(-1, "The paramName or value is null or empty");

            return ToResult(this.device.Parameters.SetEnumValueByString(paramName, value), "Check the paramName or value!");
        }

        /// <summary>
        /// 获取参数
        /// </summary>
        /// <typeparam name="T">参数类型，支持 int、long、float、string、bool</typeparam>
        /// <param name="paramName">参数名</param>
        /// <returns>
        /// 参数值，类型不支持或相机不可用时返回 default(T)
        /// </returns>
        public T GetParam<T>(string paramName)
        {
            if (!this.IsOpen || string.IsNullOrEmpty(paramName))
                return default;

            try
            {
                var type = typeof(T);

                if (type == typeof(int))
                {
                    if (this.device.Parameters.GetIntValue(paramName, out IIntValue intValue) == MvError.MV_OK)
                        return (T)(object)(int)intValue.CurValue;
                    return default;
                }

                if (type == typeof(long))
                {
                    if (this.device.Parameters.GetIntValue(paramName, out IIntValue intValue) == MvError.MV_OK)
                        return (T)(object)intValue.CurValue;
                    return default;
                }

                if (type == typeof(float))
                {
                    if (this.device.Parameters.GetFloatValue(paramName, out IFloatValue floatValue) == MvError.MV_OK)
                        return (T)(object)floatValue.CurValue;
                    return default;
                }

                if (type == typeof(string))
                {
                    if (this.device.Parameters.GetStringValue(paramName, out IStringValue stringValue) == MvError.MV_OK)
                        return (T)(object)(stringValue.CurValue ?? string.Empty);
                    return default;
                }

                if (type == typeof(bool))
                {
                    if (this.device.Parameters.GetBoolValue(paramName, out bool boolValue) == MvError.MV_OK)
                        return (T)(object)boolValue;
                    return default;
                }

                return default;
            }
            catch
            {
                return default;
            }
        }

        /// <summary>
        /// 获取枚举参数的符号名
        /// </summary>
        /// <param name="paramName">参数名</param>
        /// <returns>
        /// 枚举符号名，获取失败时返回空字符串
        /// </returns>
        public string GetEnumValue(string paramName)
        {
            if (!this.IsOpen || string.IsNullOrEmpty(paramName))
                return string.Empty;

            try
            {
                if (this.device.Parameters.GetEnumValue(paramName, out IEnumValue enumValue) == MvError.MV_OK)
                    return enumValue.CurEnumEntry?.Symbolic ?? string.Empty;
            }
            catch
            {
                // 读取失败按“无值”处理
            }

            return string.Empty;
        }

        /// <summary>
        /// 执行命令
        /// </summary>
        /// <param name="command">命令</param>
        /// <returns>
        /// 相机操作结果
        /// </returns>
        public CameraResult ExecuteCommand(string command)
        {
            if (!this.IsOpen)
                return CameraResult.Fail(-1, "Camera is not open");

            if (string.IsNullOrEmpty(command))
                return CameraResult.Fail(-1, "Check the command");

            return ToResult(this.device.Parameters.SetCommandValue(command), "Execute command failed");
        }

        /// <summary>
        /// 释放相机（幂等，可重复调用）
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref this.disposed, 1, 0) == 1)
                return;

            try
            {
                if (this.device != null)
                {
                    this.StopGrab();

                    try
                    {
                        this.device.StreamGrabber.FrameGrabedEvent -= this.ProcessFrameCallBack;
                    }
                    catch
                    {
                        // 解绑回调失败不阻断释放流程
                    }

                    try
                    {
                        this.device.Close();
                    }
                    catch
                    {
                        // 关闭失败不阻断释放流程
                    }

                    try
                    {
                        if (this.device is IDisposable disposable)
                            disposable.Dispose();
                    }
                    catch
                    {
                        // 释放原生设备失败不阻断流程
                    }

                    this.device = null;
                }
            }
            finally
            {
                Interlocked.Exchange(ref this.isOpen, 0);
                Interlocked.Exchange(ref this.isGrabbing, 0);
            }
        }

        /// <summary>
        /// 海康相机帧到达回调，运行在SDK采集线程
        /// </summary>
        /// <param name="sender">事件源</param>
        /// <param name="e">帧到达事件参数</param>
        private void ProcessFrameCallBack(object sender, FrameGrabbedEventArgs e)
        {
            var frameOut = e?.FrameOut;

            if (frameOut == null)
                return;

            HikFrameWrapper frame = null;

            try
            {
                // 克隆帧后再归还SDK缓冲区，使下游订阅者持有独立于SDK缓冲区的内存
                if (frameOut.Clone() is IFrameOut cloned)
                {
                    frame = new HikFrameWrapper(cloned);
                    this.stream.Publish(frame);

                    // 所有权已移交数据流，由订阅者负责释放
                    frame = null;
                }
            }
            catch
            {
                // 回调运行在SDK采集线程，异常不得外泄，否则会中断采集
                frame?.Dispose();
                frame = null;
            }
            finally
            {
                try
                {
                    this.device?.StreamGrabber?.FreeImageBuffer(frameOut);
                }
                catch
                {
                    // 归还缓冲区失败不向上抛出
                }
            }
        }

        /// <summary>
        /// 校验相机与参数名是否可用
        /// </summary>
        /// <param name="paramName">参数名</param>
        /// <returns>
        /// 相机操作结果
        /// </returns>
        private CameraResult CheckReady(string paramName)
        {
            if (!this.IsOpen)
                return CameraResult.Fail(-1, "Camera is not open");

            if (string.IsNullOrEmpty(paramName))
                return CameraResult.Fail(-1, "The paramName is null or empty");

            return CameraResult.Success(0);
        }

        /// <summary>
        /// 将海康错误码转换为统一结果
        /// </summary>
        /// <param name="errorCode">海康错误码</param>
        /// <param name="failMessage">失败提示信息</param>
        /// <returns>
        /// 相机操作结果
        /// </returns>
        private static CameraResult ToResult(int errorCode, string failMessage)
        {
            var success = errorCode == MvError.MV_OK;
            return CameraResult.Result(success, errorCode, success ? string.Empty : failMessage);
        }
    }
}
