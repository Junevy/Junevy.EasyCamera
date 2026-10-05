using Junevy.EasyCamera.Core.Abstractions;
using System;
using System.Collections.Generic;

namespace Junevy.EasyCamera.Tests.Mocks
{
    public class MockCameraProvider : ICameraProvider
    {
        private readonly List<MockCameraInfo> cameraInfos;
        private readonly List<MockCamera> createdCameras = new List<MockCamera>();

        public IReadOnlyList<MockCamera> CreatedCameras => this.createdCameras;

        public MockCameraProvider(List<MockCameraInfo> cameraInfos)
        {
            this.cameraInfos = cameraInfos ?? new List<MockCameraInfo>();
        }

        public bool Supports(ICameraInfo info) => info is MockCameraInfo;

        public ICamera Create(ICameraInfo info, ICameraStream stream)
        {
            if (info is not MockCameraInfo)
                throw new ArgumentException("Invalid camera info type.", nameof(info));

            var mockCamera = new MockCamera();
            this.createdCameras.Add(mockCamera);
            return mockCamera;
        }

        public IEnumerable<ICameraInfo> Enumerate() => this.cameraInfos;

        public IEnumerable<ICameraInfo> Enumerate(CameraInterfaceType type) => this.cameraInfos;
    }
}
