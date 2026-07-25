using Xunit;

namespace USugar.Tests
{
    public class UdonTypeIdentityTests
    {
        [Fact]
        public void StorageAliasesDoNotCollapseClrTypeTokens()
        {
            var behaviourStorage = UdonTypeIdentity.FromStorage(
                typeof(UdonSharp.UdonSharpBehaviour));
            var runtimeStorage = UdonTypeIdentity.FromStorage(
                typeof(VRC.Udon.UdonBehaviour));
            var receiverStorage = UdonTypeIdentity.FromStorage(
                typeof(VRC.Udon.Common.Interfaces.IUdonEventReceiver));

            Assert.Equal(receiverStorage, behaviourStorage);
            Assert.Equal(receiverStorage, runtimeStorage);
            Assert.Equal(
                "VRCUdonCommonInterfacesIUdonEventReceiver",
                receiverStorage.Name);

            var behaviourToken = UdonTypeIdentity.FromClrToken(
                typeof(UdonSharp.UdonSharpBehaviour));
            var runtimeToken = UdonTypeIdentity.FromClrToken(
                typeof(VRC.Udon.UdonBehaviour));
            var receiverToken = UdonTypeIdentity.FromClrToken(
                typeof(VRC.Udon.Common.Interfaces.IUdonEventReceiver));

            Assert.Equal("UdonSharpUdonSharpBehaviour", behaviourToken.Name);
            Assert.Equal("VRCUdonUdonBehaviour", runtimeToken.Name);
            Assert.Equal(
                "VRCUdonCommonInterfacesIUdonEventReceiver",
                receiverToken.Name);
            Assert.NotEqual(behaviourToken, runtimeToken);
            Assert.NotEqual(behaviourToken, receiverToken);
            Assert.NotEqual(runtimeToken, receiverToken);
        }
    }
}

namespace UdonSharp
{
    public class UdonSharpBehaviour { }
}

namespace VRC.Udon
{
    public class UdonBehaviour { }
}

namespace VRC.Udon.Common.Interfaces
{
    public interface IUdonEventReceiver { }
}
