using RawHIDBroker.EventLoop;

namespace RawHIDBroker.Tests
{
    [TestClass]
    public class DeviceIDTest
    { 
        [TestMethod]
        public void TestMethod1()
        {
            // Arrange
            var deviceID = new DeviceInformation(0x1234, 0x1234);
            // Act
            var result = deviceID.ToString();


            // Assert
            Assert.AreEqual("0x1234:0x1234", result);
        }
    }
}
