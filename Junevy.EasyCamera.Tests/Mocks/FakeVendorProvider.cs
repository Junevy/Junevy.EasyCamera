using Junevy.EasyCamera.Core.Abstractions;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Junevy.EasyCamera.Tests.Mocks
{
    /// <summary>
    /// 模拟厂商提供器，用于聚合提供器的分发测试
    /// </summary>
    public class FakeVendorProvider : IVendorCameraProvider
    {
        private readonly List<FakeCameraInfo> cameraInfos;

        public FakeVendorProvider(string vendorName, IEnumerable<string> serialNumbers)
        {
            this.VendorName = vendorName;
            this.cameraInfos = serialNumbers
                .Select(sn => new FakeCameraInfo { SerialNumber = sn, Manufacturer = vendorName })
                .ToList();
        }

        public string VendorName { get; }

        public int CreateCount { get; private set; }

        public bool Supports(ICameraInfo info) => info is FakeCameraInfo;

        public ICamera Create(ICameraInfo info, ICameraStream stream)
        {
            if (info is not FakeCameraInfo)
                throw new ArgumentException("Invalid camera info type.", nameof(info));

            this.CreateCount++;
            return new MockCamera();
        }

        public IEnumerable<ICameraInfo> Enumerate() => this.cameraInfos;

        public IEnumerable<ICameraInfo> Enumerate(CameraInterfaceType type) => this.cameraInfos;
    }
}
